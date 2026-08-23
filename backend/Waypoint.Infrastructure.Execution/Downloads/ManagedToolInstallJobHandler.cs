// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The <c>tool-install</c> <see cref="Waypoint.Core.Jobs.JobShape.Simple"/> job handler
/// (issue #39, ADR-0015 decision 3): copies a candidate <c>vcf-download-tool</c>
/// artifact into the managed-tool volume once its detached signature verifies against
/// the Broadcom release key, and appends the outcome (installed, rejected, or failed) to
/// the append-only <c>managed_tool_installs</c> ledger regardless of which way it goes.
///
/// Covers two of the three ADR-0015 install paths this slice implements:
/// <see cref="ManagedToolInstallSources.LocalRepository"/> (an operator-provisioned
/// local indexed repository, works air-gapped) and
/// <see cref="ManagedToolInstallSources.Upload"/> (a manual upload already staged by
/// <c>ManagedToolController.Upload</c>). The connected-mode depot-fetch path
/// (<see cref="ManagedToolInstallSources.Depot"/>) is deferred to a follow-up issue (see
/// this PR's body) -- a payload naming that source fails cleanly here rather than being
/// silently accepted.
///
/// Payload contract (JSON object, set by <c>ManagedToolController</c>):
/// <c>{"source": "local-repository"|"upload", "source_path": "&lt;file name under the
/// configured source root&gt;"}</c>. <c>source_path</c> is resolved against
/// <see cref="ManagedToolOptions.LocalRepositoryPath"/> or
/// <see cref="ManagedToolOptions.UploadStagingPath"/> depending on <c>source</c> --
/// never taken as an absolute path from the payload, so a crafted payload cannot walk
/// outside the configured root (path traversal is rejected before any file I/O).
/// </summary>
public sealed class ManagedToolInstallJobHandler : IJobHandler
{
	// snake_case, matching the payload ManagedToolController serializes
	// (source_path/initiated_by) -- the Web default (camelCase) would silently leave
	// those properties null.
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IManagedToolSignatureVerifier _verifier;
	private readonly IManagedToolInstallRepository _installs;
	private readonly IOptions<ManagedToolOptions> _options;

	public ManagedToolInstallJobHandler(
		IManagedToolSignatureVerifier verifier,
		IManagedToolInstallRepository installs,
		IOptions<ManagedToolOptions> options)
	{
		ArgumentNullException.ThrowIfNull(verifier);
		ArgumentNullException.ThrowIfNull(installs);
		ArgumentNullException.ThrowIfNull(options);
		_verifier = verifier;
		_installs = installs;
		_options = options;
	}

	public string JobType => "tool-install";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		ToolInstallPayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<ToolInstallPayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed tool-install payload: {exception.Message}");
		}

		if (payload is null || string.IsNullOrWhiteSpace(payload.Source) || string.IsNullOrWhiteSpace(payload.SourcePath))
		{
			return JobExecutionOutcome.Failed("tool-install payload requires non-empty 'source' and 'source_path'.");
		}

		if (payload.Source != ManagedToolInstallSources.LocalRepository && payload.Source != ManagedToolInstallSources.Upload)
		{
			// Depot-fetch is deferred (see this type's doc comment); any other value is
			// simply unknown. Either way: fail the job, do not touch the filesystem, and
			// do not write a ledger row for a source we never validated a real path for.
			return JobExecutionOutcome.Failed(
				$"tool-install source '{payload.Source}' is not implemented by this handler. Supported: '{ManagedToolInstallSources.LocalRepository}', '{ManagedToolInstallSources.Upload}'.");
		}

		ManagedToolOptions options = _options.Value;
		string initiatedBy = payload.InitiatedBy ?? "system";

		string rootPath = payload.Source == ManagedToolInstallSources.LocalRepository
			? options.LocalRepositoryPath
			: options.UploadStagingPath;

		string? resolvedArtifactPath = ResolveWithinRoot(rootPath, payload.SourcePath);
		if (resolvedArtifactPath is null)
		{
			return JobExecutionOutcome.Failed(
				$"source_path '{payload.SourcePath}' does not resolve within the configured '{payload.Source}' root. Rejected without recording a ledger row (not a legitimate candidate path).");
		}

		string signaturePath = resolvedArtifactPath + ".sig";

		if (!File.Exists(resolvedArtifactPath))
		{
			return JobExecutionOutcome.Failed($"Candidate artifact not found at '{resolvedArtifactPath}'.");
		}

		string sha256 = await ComputeSha256Async(resolvedArtifactPath, cancellationToken).ConfigureAwait(false);

		ManagedToolSignatureResult verification = await _verifier
			.VerifyAsync(resolvedArtifactPath, signaturePath, cancellationToken)
			.ConfigureAwait(false);

		if (!verification.Valid)
		{
			await _installs.RecordAsync(
				new ManagedToolInstallAttempt(
					payload.Source, payload.SourcePath, payload.Version, sha256,
					ManagedToolInstallOutcomes.Rejected, verification.FailureReason, initiatedBy, context.Job.Id),
				cancellationToken).ConfigureAwait(false);

			return JobExecutionOutcome.Failed($"Signature verification failed, install rejected: {verification.FailureReason}");
		}

		try
		{
			Directory.CreateDirectory(options.ToolStatePath);
			string destinationPath = Path.Combine(options.ToolStatePath, options.ExecutableName);
			string stagingPath = destinationPath + ".staging";

			// Copy to a same-volume staging name first, then atomically rename into
			// place (File.Move with overwrite is atomic on the same filesystem on both
			// Linux and Windows) -- a download job's tool-presence check must never
			// observe a partially-written executable.
			File.Copy(resolvedArtifactPath, stagingPath, overwrite: true);
			File.Move(stagingPath, destinationPath, overwrite: true);
			TrySetExecutable(destinationPath);
		}
		catch (IOException exception)
		{
			await _installs.RecordAsync(
				new ManagedToolInstallAttempt(
					payload.Source, payload.SourcePath, payload.Version, sha256,
					ManagedToolInstallOutcomes.Failed, null, initiatedBy, context.Job.Id),
				cancellationToken).ConfigureAwait(false);

			return JobExecutionOutcome.Failed($"Verified artifact could not be activated: {exception.Message}");
		}

		await _installs.RecordAsync(
			new ManagedToolInstallAttempt(
				payload.Source, payload.SourcePath, payload.Version, sha256,
				ManagedToolInstallOutcomes.Installed, null, initiatedBy, context.Job.Id),
			cancellationToken).ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"vcf-download-tool installed from {payload.Source}.");
	}

	/// <summary>
	/// Resolves <paramref name="relativePath"/> against <paramref name="root"/> and
	/// rejects anything that escapes it (rooted paths, <c>..</c> segments) -- the payload
	/// is API-accepted input (a file name an operator picked from a directory listing or
	/// an upload's staged file name), never trusted as an absolute filesystem path.
	/// </summary>
	private static string? ResolveWithinRoot(string root, string relativePath)
	{
		if (Path.IsPathRooted(relativePath))
		{
			return null;
		}

		string fullRoot = Path.GetFullPath(root);
		string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));

		string normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return candidate.StartsWith(normalizedRoot, StringComparison.Ordinal) ? candidate : null;
	}

	private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = File.OpenRead(path);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static void TrySetExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		File.SetUnixFileMode(path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
			UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
	}

	private sealed record ToolInstallPayload(string? Source, string? SourcePath, string? Version, string? InitiatedBy);
}
