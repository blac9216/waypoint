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
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;
using Waypoint.Core.SystemState;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The <c>tool-install</c> <see cref="Waypoint.Core.Jobs.JobShape.Simple"/> job handler
/// (issue #39, ADR-0015 decision 3): places a candidate <c>vcf-download-tool</c>
/// artifact into the managed-tool volume once its source-specific verification passes,
/// and appends the outcome (installed, rejected, or failed) to
/// the append-only <c>managed_tool_installs</c> ledger regardless of which way it goes.
///
/// Implements all three ADR-0015 install paths:
/// <see cref="ManagedToolInstallSources.LocalRepository"/> (an operator-provisioned
/// local indexed repository, works air-gapped), <see cref="ManagedToolInstallSources.Upload"/>
/// (a manual upload already staged by <c>ManagedToolController.Upload</c>), and
/// <see cref="ManagedToolInstallSources.Depot"/> (connected-mode-only: fetched live
/// using the stored <see cref="CredentialTypes.DepotActivationCode"/> credential --
/// issue #690: this is the VCF 9.1 Activation Code, never the legacy Download Token,
/// which cannot authenticate <c>vcf-download-tool</c> commands -- mirroring the
/// decrypt-for-one-call pattern <c>CatalogIndexJobHandler</c> used before #690
/// removed its own credential dependency). The first two resolve a file already on this host's
/// filesystem; the depot path additionally fetches the candidate over HTTP via
/// <see cref="IManagedToolDepotFetcher"/> before the same verify-then-activate
/// pipeline every path shares.
///
/// Payload contract (JSON object, set by <c>ManagedToolController</c>):
/// <c>{"source": "local-repository"|"upload"|"depot", "source_path": "&lt;file name
/// under the configured source root&gt;"}</c> -- <c>source_path</c> is required for
/// <c>local-repository</c>/<c>upload</c> (resolved against
/// <see cref="ManagedToolOptions.LocalRepositoryPath"/> or
/// <see cref="ManagedToolOptions.UploadStagingPath"/>, never taken as an absolute path
/// from the payload so a crafted payload cannot walk outside the configured root) and
/// ignored for <c>depot</c> (the depot URL is server-side configuration, not
/// operator-supplied).
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
	private readonly IManagedToolCatalogVerifier _catalogVerifier;
	private readonly IManagedToolInstallRepository _installs;
	private readonly IOptions<ManagedToolOptions> _options;
	private readonly IManagedToolDepotFetcher _depotFetcher;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly IApplianceStateRepository _applianceState;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly IManagedToolDistributionInstaller _distributionInstaller;

	public ManagedToolInstallJobHandler(
		IManagedToolSignatureVerifier verifier,
		IManagedToolCatalogVerifier catalogVerifier,
		IManagedToolInstallRepository installs,
		IOptions<ManagedToolOptions> options,
		IManagedToolDepotFetcher depotFetcher,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		IApplianceStateRepository applianceState,
		IOptions<CatalogOptions> catalogOptions,
		IManagedToolDistributionInstaller distributionInstaller)
	{
		ArgumentNullException.ThrowIfNull(verifier);
		ArgumentNullException.ThrowIfNull(installs);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(depotFetcher);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(applianceState);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(distributionInstaller);
		_verifier = verifier;
		_catalogVerifier = catalogVerifier ?? throw new ArgumentNullException(nameof(catalogVerifier));
		_installs = installs;
		_options = options;
		_depotFetcher = depotFetcher;
		_secrets = secrets;
		_credentials = credentials;
		_applianceState = applianceState;
		_catalogOptions = catalogOptions;
		_distributionInstaller = distributionInstaller;
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

		if (payload is null || string.IsNullOrWhiteSpace(payload.Source))
		{
			return JobExecutionOutcome.Failed("tool-install payload requires a non-empty 'source'.");
		}

		if (payload.Source != ManagedToolInstallSources.LocalRepository
			&& payload.Source != ManagedToolInstallSources.Upload
			&& payload.Source != ManagedToolInstallSources.Depot)
		{
			// Any other value is simply unknown -- fail the job, do not touch the
			// filesystem, and do not write a ledger row for a source we never
			// validated a real candidate for.
			return JobExecutionOutcome.Failed(
				$"tool-install source '{payload.Source}' is not implemented by this handler. Supported: " +
				$"'{ManagedToolInstallSources.LocalRepository}', '{ManagedToolInstallSources.Upload}', '{ManagedToolInstallSources.Depot}'.");
		}

		if (payload.Source != ManagedToolInstallSources.Depot && string.IsNullOrWhiteSpace(payload.SourcePath))
		{
			return JobExecutionOutcome.Failed("tool-install payload requires non-empty 'source_path' for this source.");
		}

		string initiatedBy = payload.InitiatedBy ?? "system";

		// Issue #647: job_id is the natural dedup key for one tool-install attempt. A
		// genuine crash-recovery requeue (runner dies mid-handler; lease-recovery sweep
		// puts the same jobs.id row back on the queue) re-runs this method for a job
		// that may have already recorded a terminal ledger outcome on a prior
		// execution. Re-running verify-then-activate in that case would both duplicate
		// the append-only ledger row and, for an already-installed outcome,
		// re-extract/re-smoke-test/re-activate the distribution a second time --
		// pointless work and a window where the active install is briefly replaced by
		// an identical copy of itself for no reason. Short-circuit here, before any
		// file or network I/O, by returning the recorded outcome unchanged.
		ManagedToolInstall? existing = await _installs.FindByJobIdAsync(context.Job.Id, cancellationToken).ConfigureAwait(false);
		if (existing is not null)
		{
			return existing.Outcome == ManagedToolInstallOutcomes.Installed
				? JobExecutionOutcome.Succeeded(
					$"tool-install job {context.Job.Id} already recorded outcome '{existing.Outcome}' (ledger row {existing.Id}) on a prior execution; requeue re-run skipped without duplicating the ledger or re-activating.")
				: JobExecutionOutcome.Failed(
					$"tool-install job {context.Job.Id} already recorded outcome '{existing.Outcome}' (ledger row {existing.Id}) on a prior execution; requeue re-run skipped without duplicating the ledger.");
		}

		return payload.Source == ManagedToolInstallSources.Depot
			? await ExecuteDepotFetchAsync(payload, initiatedBy, context, cancellationToken).ConfigureAwait(false)
			: await ExecuteFileBasedAsync(payload, initiatedBy, context, cancellationToken).ConfigureAwait(false);
	}

	private async Task<JobExecutionOutcome> ExecuteFileBasedAsync(
		ToolInstallPayload payload, string initiatedBy, JobExecutionContext context, CancellationToken cancellationToken)
	{
		ManagedToolOptions options = _options.Value;
		string source = payload.Source!;
		string sourcePath = payload.SourcePath!;

		string rootPath = source == ManagedToolInstallSources.LocalRepository
			? options.LocalRepositoryPath
			: options.UploadStagingPath;

		string? resolvedArtifactPath = ResolveWithinRoot(rootPath, sourcePath);
		if (resolvedArtifactPath is null)
		{
			return JobExecutionOutcome.Failed(
				$"source_path '{sourcePath}' does not resolve within the configured '{source}' root. Rejected without recording a ledger row (not a legitimate candidate path).");
		}

		string signaturePath = resolvedArtifactPath + ".sig";

		if (!File.Exists(resolvedArtifactPath))
		{
			return JobExecutionOutcome.Failed($"Candidate artifact not found at '{resolvedArtifactPath}'.");
		}

		return await VerifyAndActivateAsync(
			source, sourcePath, payload.Version, resolvedArtifactPath, signaturePath, initiatedBy, context, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// The connected-mode-only depot-fetch path (issue #39 remainder, #690's
	/// purpose-specific resolution): refuses cleanly and without any network attempt
	/// when disconnected or when no <see cref="CredentialTypes.DepotActivationCode"/>
	/// credential is configured, decrypts that credential for exactly the duration of
	/// the fetch (the same fail-closed audit + in-play redaction pattern the rest of
	/// this codebase's decrypt-for-one-call handlers use), then runs the fetched pair
	/// through the identical verify-then-activate pipeline the file-based paths use.
	/// This never resolves <see cref="CredentialTypes.DepotToken"/> (the deprecated
	/// legacy alias) or <see cref="CredentialTypes.LegacyDownloadToken"/> -- only the
	/// Activation Code authenticates <c>vcf-download-tool</c> commands.
	/// </summary>
	private async Task<JobExecutionOutcome> ExecuteDepotFetchAsync(
		ToolInstallPayload payload, string initiatedBy, JobExecutionContext context, CancellationToken cancellationToken)
	{
		ApplianceState? state = await _applianceState.GetAsync(cancellationToken).ConfigureAwait(false);
		bool connected = string.Equals(state?.Mode, "connected", StringComparison.Ordinal);
		if (!connected)
		{
			// Disconnected (or an unreadable appliance_state row -- treated as
			// disconnected, same fail-safe default LibraryController/ScheduleDispatchService
			// use) refuses before any network attempt and before any ledger row --
			// this is a configuration/mode mismatch, not a real install attempt.
			return JobExecutionOutcome.Failed(
				"Depot-fetch install is unavailable in disconnected mode. Use the local-repository or manual-upload install path instead.");
		}

		CredentialResponse? activationCodeCredential = await _credentials
			.FindByTypeAsync(_catalogOptions.Value.DepotActivationCodeCredentialType, cancellationToken)
			.ConfigureAwait(false);
		if (activationCodeCredential is null || !activationCodeCredential.HasSecret)
		{
			return JobExecutionOutcome.Failed(
				$"No credential of type '{_catalogOptions.Value.DepotActivationCodeCredentialType}' is configured for the Software Depot Activation Code. Configure a depot Activation Code before using the depot-fetch install path.");
		}

		ManagedToolOptions options = _options.Value;
		string stagingDirectory = Path.Combine(options.UploadStagingPath, "depot-fetch");

		ManagedToolDepotFetchResult fetchResult;
		DecryptedSecret? decrypted = null;
		try
		{
			// security.md control 4 / #8's fail-closed decrypt audit: writes the
			// secret.decrypted audit row before any plaintext reaches this method.
			decrypted = await _secrets
				.DecryptAsync(activationCodeCredential.Id, initiatedBy, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);

			fetchResult = await _depotFetcher
				.FetchAsync(decrypted.Value, payload.Version, stagingDirectory, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			return JobExecutionOutcome.Failed($"Depot Activation Code credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			return JobExecutionOutcome.Failed($"Depot Activation Code could not be decrypted: {exception.Message}");
		}
		finally
		{
			// Ends the in-play redaction window as soon as the fetch is done, whether
			// it succeeded or not -- the value must not still be "in play" once this
			// method has returned.
			decrypted?.Dispose();
		}

		if (!fetchResult.Succeeded)
		{
			// A fetch failure (auth, unreachable, oversize, misconfigured) never wrote
			// any file worth verifying and is not itself a signature rejection -- no
			// ledger row, same "not a legitimate candidate" treatment path-traversal
			// and missing-file cases get on the file-based paths above. The reason
			// string here is fully own-authored (never echoes response bodies), so it
			// cannot carry the token even indirectly.
			return JobExecutionOutcome.Failed($"Depot fetch failed ({fetchResult.FailureKind}): {fetchResult.FailureReason}");
		}

		try
		{
			return await VerifyAndActivateAsync(
				ManagedToolInstallSources.Depot, ArtifactDescription(payload.Version), payload.Version,
				fetchResult.ArtifactPath!, fetchResult.RepositoryRoot!, initiatedBy, context, cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			DeleteFetchedStaging(fetchResult.ArtifactPath!, fetchResult.RepositoryRoot!);
		}
	}

	private static string ArtifactDescription(string? version) =>
		string.IsNullOrWhiteSpace(version) ? "depot:latest" : $"depot:{version}";

	/// <summary>Removes the transient depot-fetch staging (artifact file and the staged catalog/signature repository-root directory) once verification/activation has run its course -- unlike upload's staging path, a depot-fetched candidate has no independent lifecycle worth preserving after this one job.</summary>
	private static void DeleteFetchedStaging(string artifactPath, string repositoryRoot)
	{
		try
		{
			if (File.Exists(artifactPath))
			{
				File.Delete(artifactPath);
			}

			if (Directory.Exists(repositoryRoot))
			{
				Directory.Delete(repositoryRoot, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup only -- a stray staging file/directory is not a
			// correctness issue for a job that has already recorded its ledger outcome.
		}
	}

	/// <summary>
	/// The shared tail every install path funnels through once it has a candidate
	/// artifact sitting on disk: hash, apply its source-specific verifier, and either
	/// record a <c>rejected</c> ledger row or atomically activate and record
	/// <c>installed</c>/<c>failed</c>.
	/// </summary>
	/// <param name="signaturePathOrCatalogRoot">
	/// For <see cref="ManagedToolInstallSources.Upload"/>: unused. For
	/// <see cref="ManagedToolInstallSources.LocalRepository"/>: unused (that path
	/// verifies against <see cref="ManagedToolOptions.LocalRepositoryPath"/> directly).
	/// For <see cref="ManagedToolInstallSources.Depot"/> (issue #671): the staged
	/// repository-root directory <see cref="IManagedToolDepotFetcher"/> fetched the
	/// catalog and catalog signature into, handed to the same
	/// <see cref="IManagedToolCatalogVerifier"/> the local-repository path uses --
	/// there is no source-specific verification logic for the connected path.
	/// </param>
	private async Task<JobExecutionOutcome> VerifyAndActivateAsync(
		string source, string sourcePath, string? version, string artifactPath, string signaturePathOrCatalogRoot,
		string initiatedBy, JobExecutionContext context, CancellationToken cancellationToken)
	{
		ManagedToolOptions options = _options.Value;

		string? sha256;
		string? failureReason;
		if (source == ManagedToolInstallSources.LocalRepository || source == ManagedToolInstallSources.Depot)
		{
			string repositoryRoot = source == ManagedToolInstallSources.LocalRepository
				? options.LocalRepositoryPath
				: signaturePathOrCatalogRoot;
			ManagedToolCatalogVerificationResult verification = await _catalogVerifier
				.VerifyAsync(repositoryRoot, artifactPath, version, cancellationToken).ConfigureAwait(false);
			sha256 = verification.ActualSha256;
			failureReason = verification.Valid ? null : verification.FailureReason;
		}
		else
		{
			sha256 = await ComputeSha256Async(artifactPath, cancellationToken).ConfigureAwait(false);
			failureReason = await VerifyUploadChecksumsAsync(
				artifactPath, sha256, context.Job.Payload, cancellationToken).ConfigureAwait(false);
		}

		if (failureReason is not null)
		{
			await _installs.RecordAsync(
				new ManagedToolInstallAttempt(
					source, sourcePath, version, sha256,
					ManagedToolInstallOutcomes.Rejected, failureReason, initiatedBy, context.Job.Id),
				cancellationToken).ConfigureAwait(false);

			return JobExecutionOutcome.Failed($"Artifact verification failed, install rejected: {failureReason}");
		}

		// The verified candidate is a vendor distribution archive, never the executable
		// itself (issue #686's Exec format error regression) -- safely extract, validate
		// the bin/lib layout, smoke-test the real executable, and atomically activate.
		// Preserves the prior-good installation on any rejection/failure and cleans up
		// staging on every path (ManagedToolDistributionInstaller's own contract).
		ManagedToolDistributionInstallResult installResult;
		try
		{
			installResult = await _distributionInstaller.InstallAsync(artifactPath, cancellationToken).ConfigureAwait(false);
		}
		catch (IOException exception)
		{
			await _installs.RecordAsync(
				new ManagedToolInstallAttempt(
					source, sourcePath, version, sha256,
					ManagedToolInstallOutcomes.Failed, null, initiatedBy, context.Job.Id),
				cancellationToken).ConfigureAwait(false);

			return JobExecutionOutcome.Failed($"Verified artifact could not be activated: {exception.Message}");
		}

		if (!installResult.Succeeded)
		{
			// A distribution that fails safe extraction, layout validation, or the
			// smoke-test is an actionable rejection (bad content), not a job-infra
			// failure -- recorded the same way a checksum/signature rejection is, with
			// the specific rejection kind folded into the reason text so the ledger
			// stays human-actionable.
			string reason = $"{installResult.RejectionKind}: {installResult.FailureReason}";
			await _installs.RecordAsync(
				new ManagedToolInstallAttempt(
					source, sourcePath, version, sha256,
					ManagedToolInstallOutcomes.Rejected, reason, initiatedBy, context.Job.Id),
				cancellationToken).ConfigureAwait(false);

			return JobExecutionOutcome.Failed($"Distribution install rejected: {reason}");
		}

		await _installs.RecordAsync(
			new ManagedToolInstallAttempt(
				source, sourcePath, version, sha256,
				ManagedToolInstallOutcomes.Installed, null, initiatedBy, context.Job.Id),
			cancellationToken).ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"vcf-download-tool installed from {source}.");
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

	private static async Task<string?> VerifyUploadChecksumsAsync(
		string path, string actualSha256, string payloadJson, CancellationToken cancellationToken)
	{
		ToolInstallPayload? payload = JsonSerializer.Deserialize<ToolInstallPayload>(payloadJson, PayloadOptions);
		if (payload is null || (payload.ExpectedSha256 is null && payload.ExpectedMd5 is null))
		{
			return "Upload requires a published SHA-256 or legacy MD5 checksum.";
		}
		if (payload.ExpectedSha256 is not null && !string.Equals(payload.ExpectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
		{
			return $"SHA-256 mismatch: expected {payload.ExpectedSha256}, actual {actualSha256}.";
		}
		if (payload.ExpectedMd5 is not null)
		{
			await using FileStream stream = File.OpenRead(path);
			// Broadcom still publishes MD5 for legacy integrity comparison. It is never
			// treated as authentication, and SHA-256 remains the preferred input.
#pragma warning disable CA5351
			string actualMd5 = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
#pragma warning restore CA5351
			if (!string.Equals(payload.ExpectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
			{
				return $"Legacy MD5 mismatch: expected {payload.ExpectedMd5}, actual {actualMd5}.";
			}
		}
		return null;
	}

	private sealed record ToolInstallPayload(
		string? Source, string? SourcePath, string? Version, string? InitiatedBy,
		string? ExpectedSha256, string? ExpectedMd5);
}
