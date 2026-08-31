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

using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The <c>binaries-download</c> <see cref="JobShape.Simple"/> job handler (issue #1482,
/// epic #1181, split from #795's design record) -- the namesake acquisition step this
/// issue exists for: runs the installed tool's <c>binaries download --id &lt;id&gt;
/// --depot-store=&lt;depot&gt; --ceip=DISABLE</c> for one job the enqueue sibling's fanout
/// (issue #1479, <c>DownloadsController.QueueBinariesDownload</c>) created.
///
/// Registers the handler AND adds <c>binaries-download</c> to
/// <see cref="Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed"/> in this same
/// change -- see that allowlist's own doc comment and migration 0099's comment for the
/// full citation: issue #619 was a handler that WAS registered while the allowlist was
/// never updated (jobs queued forever unclaimed); this is the inverse case, a type that
/// was allowlist-reserved with no handler, which
/// <c>EveryRegisteredJobHandlerIsClaimableTests.DownloadRunnerAllowlist_NamesOnlyJobTypesWithARegisteredHandler</c>
/// would have failed CI over the instant it was added without this handler landing
/// alongside it.
///
/// Payload contract (JSON object, set by <c>DownloadsController.QueueBinariesDownload</c>):
/// <c>{"depot_artifact_id": "&lt;guid&gt;", "external_id": "&lt;depot-relative id&gt;"}</c>.
/// <c>external_id</c> is passed as the tool's <c>--id</c> value -- the only artifact
/// identifier the enqueue sibling's fanout carries onto the job payload today; whether
/// the real tool expects exactly this value for <c>--id</c> is one of this issue's
/// pending-live facts (see the issue's "Verified expectation").
///
/// Concurrency (2026-08-28 grill decision R2-8, unbounded): every invocation gets its
/// OWN job-scoped identity home (<c>&lt;ManagedTool:ToolStatePath&gt;/&lt;BinariesDownloadIdentityDirectoryName&gt;/job-&lt;job id&gt;</c>),
/// never the shared enrollment/catalog-pull identity home issue #790 documents as
/// unserialized across concurrent depot jobs -- see <see cref="IBinariesDownloadTool"/>'s
/// doc comment. That home, and the job-scoped decrypted-Activation-Code temp file
/// (issue #760's atomic-restrictive-mode pattern, mirroring <c>CatalogPullJobHandler</c>),
/// are always removed in <c>finally</c>.
/// </summary>
public sealed class BinariesDownloadJobHandler : IJobHandler
{
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IDepotEnrollmentRepository _enrollment;
	private readonly IBinariesDownloadTool _tool;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly IOptions<ManagedToolOptions> _toolOptions;
	private readonly IOptions<CatalogOptions> _catalogOptions;

	public BinariesDownloadJobHandler(
		IDepotEnrollmentRepository enrollment,
		IBinariesDownloadTool tool,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		IOptions<ManagedToolOptions> toolOptions,
		IOptions<CatalogOptions> catalogOptions)
	{
		ArgumentNullException.ThrowIfNull(enrollment);
		ArgumentNullException.ThrowIfNull(tool);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(toolOptions);
		ArgumentNullException.ThrowIfNull(catalogOptions);

		_enrollment = enrollment;
		_tool = tool;
		_secrets = secrets;
		_credentials = credentials;
		_toolOptions = toolOptions;
		_catalogOptions = catalogOptions;
	}

	public string JobType => RunTypes.BinariesDownload;

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		BinariesDownloadPayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<BinariesDownloadPayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed binaries-download payload: {exception.Message}");
		}

		if (payload is null || string.IsNullOrWhiteSpace(payload.ExternalId))
		{
			return JobExecutionOutcome.Failed("binaries-download payload requires a non-empty 'external_id'.");
		}

		DepotEnrollment? enrollment = await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false);
		if (enrollment is null || !string.Equals(enrollment.State, DepotEnrollmentStates.Validated, StringComparison.Ordinal))
		{
			return JobExecutionOutcome.Failed(
				"Connected binaries download is disabled until the managed tool is installed, a Software Depot ID is " +
				"generated, and a matching Activation Code has been validated (see Depot & Tokens enrollment).");
		}

		CredentialResponse? activationCode = await _credentials
			.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken).ConfigureAwait(false);
		if (activationCode is null || !activationCode.HasSecret)
		{
			return JobExecutionOutcome.Failed($"No credential of type '{CredentialTypes.DepotActivationCode}' is configured.");
		}

		ManagedToolOptions toolOptions = _toolOptions.Value;
		string depotStorePath = _catalogOptions.Value.DepotPath;

		// Issue #1482 (grill decision R2-8): a fresh, job-scoped staging root for BOTH
		// the decrypted-Activation-Code temp file (issue #760's atomic-restrictive-mode
		// pattern) and this job's isolated identity home -- never the shared enrollment
		// identity home, so two concurrent binaries-download jobs can never collide on
		// machine_id (the exposure issue #790 documents for the shared home).
		string stagingRoot = Path.Combine(toolOptions.ToolStatePath, "binaries-download-staging", $"job-{context.Job.Id:N}");
		string activationCodePath = Path.Combine(stagingRoot, "activation-code.txt");
		string identityHome = Path.Combine(toolOptions.ToolStatePath, toolOptions.BinariesDownloadIdentityDirectoryName, $"job-{context.Job.Id:N}");

		string? assetId;
		DecryptedSecret? decrypted = null;
		try
		{
			CreateRestrictedDirectory(stagingRoot);

			decrypted = await _secrets
				.DecryptAsync(activationCode.Id, "system", context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);
			await WriteRestrictedFileAsync(activationCodePath, decrypted.Value, cancellationToken).ConfigureAwait(false);

			// Issue #787: identity follows the code -- derive the non-secret asset_id
			// from THIS code and seed this job's own identity home from it (never the
			// shared enrollment home) immediately before invoking the tool.
			assetId = DepotActivationCodeCodec.TryExtractAssetId(decrypted.Value);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			return JobExecutionOutcome.Failed($"Activation Code credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			return JobExecutionOutcome.Failed($"Activation Code could not be decrypted: {exception.Message}");
		}
		finally
		{
			decrypted?.Dispose();
		}

		if (string.IsNullOrWhiteSpace(assetId))
		{
			TryDeleteDirectory(stagingRoot);
			return JobExecutionOutcome.Failed("The configured Activation Code does not decode a usable asset_id; cannot seed identity.");
		}

		try
		{
			BinariesDownloadResult result = await _tool
				.DownloadAsync(payload.ExternalId, depotStorePath, activationCodePath, identityHome, assetId, cancellationToken)
				.ConfigureAwait(false);

			// Issue #1482 AC: tool stdout is captured verbatim in job logs, never parsed
			// for control flow -- actual progress/rate/ETA computation is #1041's later
			// concern, not this handler's job.
			if (!string.IsNullOrEmpty(result.Stdout))
			{
				await EmitLogAsync(context, result.Succeeded ? "information" : "warning", result.Stdout, cancellationToken)
					.ConfigureAwait(false);
			}

			if (result.Succeeded)
			{
				return JobExecutionOutcome.Succeeded($"binaries download completed for '{payload.ExternalId}'.");
			}

			return result.IsAuthFailure
				? JobExecutionOutcome.AuthFailed(result.FailureReason)
				: JobExecutionOutcome.Failed(result.FailureReason);
		}
		finally
		{
			TryDeleteDirectory(stagingRoot);
			TryDeleteDirectory(identityHome);
		}
	}

	private static async Task EmitLogAsync(JobExecutionContext context, string severity, string line, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new { severity, line });
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Issue #760: atomic 0700 <c>mkdir</c>, never create-then-chmod -- mirrors <c>CatalogPullJobHandler</c>'s identical helper.</summary>
	private static void CreateRestrictedDirectory(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(path);
			return;
		}

		Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	/// <summary>Issue #760: the secret-bearing temp file's mode is restrictive from creation -- mirrors <c>CatalogPullJobHandler</c>'s identical helper.</summary>
	private static async Task WriteRestrictedFileAsync(string path, string contents, CancellationToken cancellationToken)
	{
		FileStreamOptions options = new()
		{
			Mode = FileMode.Create,
			Access = FileAccess.Write,
			Share = FileShare.None,
		};

		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		await using FileStream stream = new(path, options);
		await using StreamWriter writer = new(stream);
		await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup only, matching CatalogPullJobHandler's identical
			// convention -- a stray staging/identity directory is not a correctness
			// issue once the job's outcome is recorded.
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private sealed record BinariesDownloadPayload(string? DepotArtifactId, string? ExternalId);
}
