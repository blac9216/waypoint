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
/// <c>{"depot_artifact_id": "&lt;guid&gt;", "external_id": "&lt;relative path&gt;",
/// "bundle_id": "&lt;vendor bundle id&gt;"}</c>. Issue #1783's live validation run 2
/// answered #1482's own pending-live question: the real tool's <c>--id</c> is the
/// vendor catalog's <c>artifacts.bundles[].id</c> (#1027 finding:
/// <c>BINARY_NOT_FOUND_IN_LOCAL_PVC</c>, "bundles are addressed by catalog id"), NOT
/// <c>external_id</c> (the binary fileName/relative path) -- passing <c>external_id</c>
/// as <c>--id</c> resolves an empty ("0 elements") selection every time. <c>bundle_id</c>
/// is what this handler now passes as <c>--id</c>; <c>external_id</c> stays on the
/// payload for identity/evidence (job log lines, the verification lookup's error text)
/// but is never passed to the tool.
///
/// Concurrency (2026-08-28 grill decision R2-8, unbounded): every invocation gets its
/// OWN job-scoped identity home (<c>&lt;ManagedTool:ToolStatePath&gt;/&lt;BinariesDownloadIdentityDirectoryName&gt;/job-&lt;job id&gt;</c>),
/// never the shared enrollment/catalog-pull identity home issue #790 documents as
/// unserialized across concurrent depot jobs -- see <see cref="IBinariesDownloadTool"/>'s
/// doc comment. That home, and the job-scoped decrypted-Activation-Code temp file
/// (issue #760's atomic-restrictive-mode pattern, mirroring <c>CatalogPullJobHandler</c>),
/// are always removed in <c>finally</c>.
///
/// Verification (issue #1486): a tool-reported success is not, by itself, grounds to
/// report the artifact present -- <see cref="_verifier"/> checks the downloaded file
/// against the <see cref="Waypoint.Core.Catalog.DepotArtifact"/> row's already-
/// authenticated size/SHA-256 (<see cref="IBinaryDownloadVerifier"/>'s doc comment) and
/// ONLY a passing verification results in a <c>present</c> upsert. A verification
/// failure never reports presence; it upserts <c>status = "failed"</c> (mirroring
/// <c>DownloadJobHandler</c>'s identical convention) and raises a
/// <see cref="JobEventTypes.SystemNotice"/> alert (design decision Q11/R2-9: alert
/// instead of drop -- a failed/partial download must never be silently absorbed as if
/// nothing happened).
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
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IBinaryDownloadVerifier _verifier;
	private readonly IOptions<ManagedToolOptions> _toolOptions;
	private readonly IOptions<CatalogOptions> _catalogOptions;

	public BinariesDownloadJobHandler(
		IDepotEnrollmentRepository enrollment,
		IBinariesDownloadTool tool,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		IDepotArtifactRepository artifacts,
		IBinaryDownloadVerifier verifier,
		IOptions<ManagedToolOptions> toolOptions,
		IOptions<CatalogOptions> catalogOptions)
	{
		ArgumentNullException.ThrowIfNull(enrollment);
		ArgumentNullException.ThrowIfNull(tool);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(verifier);
		ArgumentNullException.ThrowIfNull(toolOptions);
		ArgumentNullException.ThrowIfNull(catalogOptions);

		_enrollment = enrollment;
		_tool = tool;
		_secrets = secrets;
		_credentials = credentials;
		_artifacts = artifacts;
		_verifier = verifier;
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

		if (!Guid.TryParse(payload.DepotArtifactId, out Guid depotArtifactId))
		{
			// Issue #1486: verification resolves the catalog's authenticated
			// size/SHA-256 by id -- without a valid depot_artifact_id there is nothing
			// to verify the eventual download against, so this fails before the tool
			// is ever invoked rather than downloading a file this handler could never
			// honestly report present.
			return JobExecutionOutcome.Failed("binaries-download payload requires a valid GUID 'depot_artifact_id'.");
		}

		// Issue #1783: the enqueue path (DownloadsController.QueueBinariesDownload)
		// already refuses to queue a job for an artifact with no bundle_id, but a job
		// payload is untrusted input to this handler regardless -- defense in depth
		// against a malformed/hand-crafted payload reaching the claim loop with
		// nothing usable to pass as the real tool's --id (see #1783's root cause: the
		// prior shape passed 'external_id', the binary fileName, which the real tool
		// never matches and resolves an empty selection for).
		if (string.IsNullOrWhiteSpace(payload.BundleId))
		{
			return JobExecutionOutcome.Failed(
				$"binaries-download payload requires a non-empty 'bundle_id' (the catalog artifact '{payload.ExternalId}' has none -- re-pull the catalog).");
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

		// Issue #1482 review (round 1, major): the staging root's decrypted-Activation-
		// Code secret file must never survive on disk regardless of WHERE this method
		// exits -- including an IOException thrown by WriteRestrictedFileAsync itself,
		// which the prior shape did not catch, so it propagated straight out of the
		// method and skipped every cleanup call below it. A single try/finally owning
		// the staging root's (and identity home's) entire lifetime, wrapping every use
		// of activationCodePath, closes that gap: whatever exits this block -- a
		// classified failure, a thrown exception, a successful run -- the finally always
		// runs.
		try
		{
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
				return JobExecutionOutcome.Failed("The configured Activation Code does not decode a usable asset_id; cannot seed identity.");
			}

			// Issue #1041: sample the destination path's size on disk while the tool
			// runs -- never parse its (buffered, no-TTY) stdout for progress (issue
			// #719). depotArtifactId was already validated as a well-formed GUID above;
			// a lookup miss here just means an unknown total (sampling still reports
			// bytes-only progress, never a divide-by-zero).
			DepotArtifact? sizingHint = await _artifacts.GetByIdAsync(depotArtifactId, cancellationToken).ConfigureAwait(false);
			long? knownSizeBytes = sizingHint?.SizeBytes;
			string? destinationPath = ResolveDestinationPath(depotStorePath, payload.ExternalId);

			await DownloadProgressEvents.EmitAsync(
				context, DownloadStates.Downloading, depotArtifactId, bytesDownloaded: 0, bytesTotal: knownSizeBytes,
				downloadRateBps: null, etaSeconds: null, downloadId: null, retries: null, cancellationToken).ConfigureAwait(false);

			BinariesDownloadResult result = await FileGrowthProgressSampler.RunAsync(
				measureBytes: () => destinationPath is null ? 0 : FileGrowthProgressSampler.MeasurePathSize(destinationPath),
				bytesTotal: knownSizeBytes,
				onSample: (sample, sampleToken) => DownloadProgressEvents.EmitAsync(
					context, DownloadStates.Downloading, depotArtifactId, sample.BytesDownloaded, sample.BytesTotal,
					sample.DownloadRateBps, sample.EtaSeconds, downloadId: null, retries: null, sampleToken),
				operation: token => _tool.DownloadAsync(payload.BundleId, depotStorePath, activationCodePath, identityHome, assetId, token),
				cancellationToken: cancellationToken,
				interval: toolOptions.ProgressSampleInterval).ConfigureAwait(false);

			// Issue #1482 AC: tool stdout is captured verbatim in job logs, never parsed
			// for control flow -- issue #1041 (above) is what actually computes
			// progress/rate/ETA, from sampled file size, not from this stdout.
			if (!string.IsNullOrEmpty(result.Stdout))
			{
				await EmitLogAsync(context, result.Succeeded ? "information" : "warning", result.Stdout, cancellationToken)
					.ConfigureAwait(false);
			}

			if (!result.Succeeded)
			{
				return result.IsAuthFailure
					? JobExecutionOutcome.AuthFailed(result.FailureReason)
					: JobExecutionOutcome.Failed(result.FailureReason);
			}

			string quarantineRoot = Path.Combine(toolOptions.ToolStatePath, toolOptions.BinariesDownloadQuarantineDirectoryName);
			return await VerifyAndRecordAsync(context, depotArtifactId, payload.ExternalId, depotStorePath, quarantineRoot, cancellationToken)
				.ConfigureAwait(false);
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

	/// <summary>
	/// Issue #1486: the tool reporting success is not, on its own, grounds to report
	/// the artifact present -- this looks up the authenticated catalog row the tool's
	/// output claims to have satisfied, runs <see cref="_verifier"/> against the file
	/// it actually wrote, and only upserts <c>status = "present"</c> when that passes.
	/// A missing catalog row (deleted mid-flight) or a failed verification both raise
	/// a <see cref="JobEventTypes.SystemNotice"/> alert (Q11/R2-9: alert instead of
	/// drop) and fail the job outcome; a failed verification additionally upserts
	/// <c>status = "failed"</c> so the catalog itself reflects the outcome, mirroring
	/// <c>DownloadJobHandler</c>'s identical convention. <paramref name="sha256"/> is
	/// passed through as-is (<c>artifact.Sha256</c> when the catalog already had one)
	/// on failure, and COALESCEs safely (never null) since the DB upsert only
	/// overwrites <c>size_bytes</c>/<c>sha256</c> when the incoming value is non-null
	/// (issue #1488 review finding 1) -- omitting <c>size_bytes</c> here for the same
	/// reason leaves the catalog-supplied size untouched.
	/// </summary>
	private async Task<JobExecutionOutcome> VerifyAndRecordAsync(
		JobExecutionContext context, Guid depotArtifactId, string externalId, string depotStorePath, string quarantineRoot,
		CancellationToken cancellationToken)
	{
		DepotArtifact? artifact = await _artifacts.GetByIdAsync(depotArtifactId, cancellationToken).ConfigureAwait(false);
		if (artifact is null)
		{
			string missingReason =
				$"binaries download reported success for '{externalId}' but depot artifact '{depotArtifactId}' " +
				"no longer exists in the catalog -- cannot verify against authenticated metadata; refusing to report presence.";
			await EmitVerificationAlertAsync(context, externalId, depotArtifactId, missingReason, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(missingReason);
		}

		BinaryDownloadVerificationResult verification = await _verifier
			.VerifyAsync(artifact, depotStorePath, cancellationToken).ConfigureAwait(false);

		if (!verification.Verified)
		{
			// Issue #1486 review (round 1, major finding 1): the other half of the
			// convention this handler already claimed to mirror from
			// DownloadJobHandler -- a failed-verification file must be quarantined out
			// of the served/indexed tree BEFORE the "failed" upsert, never left at its
			// depot path. Left in place, the next catalog-index disk walk re-indexes it
			// (status='indexed', sha256 = the corrupt bytes' own hash) and the upsert's
			// `sha256 = COALESCE(EXCLUDED.sha256, ...)` + `status = EXCLUDED.status`
			// lets that hash silently overwrite the catalog's authenticated one and
			// resurrect the row as indexed -- laundering a corrupt download into one a
			// later re-download would verify "present" against. Quarantining first
			// means the index walk can never see the file at all.
			QuarantineOutcome quarantine = QuarantineFile(verification.ResolvedPath, quarantineRoot);
			string failureReason = quarantine.Kind switch
			{
				// Issue #1486 review (round 2, finding 1): NotNeeded (verification.ResolvedPath
				// was null -- missing file or path-escape, nothing existed to move) and Failed
				// (a file existed but the move itself threw) are NOT the same outcome and must
				// not collapse to the same reason string -- Failed leaves the corrupt bytes at
				// the depot path, which is the exact laundering exposure this quarantine step
				// exists to close, so the operator MUST be told the file remains and must act.
				QuarantineResultKind.NotNeeded => verification.FailureReason!,
				QuarantineResultKind.Quarantined =>
					$"{verification.FailureReason} (quarantined at '{quarantine.Path}').",
				_ =>
					$"{verification.FailureReason} (QUARANTINE FAILED: {quarantine.FailureDetail}; " +
					$"the failed-verification file REMAINS at '{verification.ResolvedPath}' and will be " +
					"re-indexed by the next catalog-index run -- remove it manually.)",
			};

			await _artifacts.UpsertAsync(
				new DepotArtifactUpsert(artifact.ExternalId, artifact.Sha256, DepotArtifactStatuses.Failed, artifact.MetadataJson),
				cancellationToken).ConfigureAwait(false);
			await EmitVerificationAlertAsync(context, artifact.ExternalId, artifact.Id, failureReason, cancellationToken)
				.ConfigureAwait(false);
			return JobExecutionOutcome.Failed($"Download verification failed for '{artifact.ExternalId}': {failureReason}");
		}

		// Grill decision Q8: the freshly computed self-hash is what gets recorded, not
		// an echo of artifact.Sha256 -- when the catalog row had no vendor hash yet
		// (size-only), this is the write that gives it one.
		await _artifacts.UpsertAsync(
			new DepotArtifactUpsert(artifact.ExternalId, verification.Sha256, DepotArtifactStatuses.Present, artifact.MetadataJson),
			cancellationToken).ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded(
			$"binaries download completed and verified for '{artifact.ExternalId}' (sha256 {verification.Sha256}).");
	}

	/// <summary>
	/// Moves a failed-verification file into <paramref name="quarantineRoot"/>
	/// (overwriting any stale quarantined copy from a previous failed attempt, so a
	/// retry starts clean) -- mirroring <c>DownloadJobHandler.QuarantineAsync</c>'s
	/// identical convention (move-out-of-the-served-tree before marking failed), but
	/// deliberately NOT reusing that sibling's own
	/// <c>&lt;ArtifactStorePath&gt;/quarantine/</c> path shape: see
	/// <see cref="ManagedToolOptions.BinariesDownloadQuarantineDirectoryName"/>'s doc
	/// comment for why a subdirectory of <c>CatalogOptions.DepotPath</c> would still
	/// be re-indexed by the catalog-index disk walk, which is exactly the bug this
	/// quarantine exists to close.
	///
	/// <paramref name="resolvedPath"/> is null when there is genuinely nothing to
	/// move (<see cref="BinaryDownloadVerificationResult.ResolvedPath"/> is null for a
	/// missing file or a path-escape) -- that is <see cref="QuarantineResultKind.NotNeeded"/>,
	/// not a failure. A move failure (e.g. issue #1486 review round 2 finding 1: the
	/// tool-state volume this handler's job-scoped identity homes also live on is full)
	/// is caught rather than thrown -- the caller still upserts <c>failed</c> and still
	/// alerts -- but it is reported as <see cref="QuarantineResultKind.Failed"/>, NOT
	/// folded into the same null the "nothing to quarantine" case returns: collapsing
	/// them was round 2's finding, since the corrupt file stays at its depot path
	/// either way, and only the Failed case needs the caller to say so.
	/// </summary>
	private static QuarantineOutcome QuarantineFile(string? resolvedPath, string quarantineRoot)
	{
		if (resolvedPath is null)
		{
			return QuarantineOutcome.NotNeeded;
		}

		try
		{
			Directory.CreateDirectory(quarantineRoot);
			string quarantinePath = Path.Combine(quarantineRoot, Path.GetFileName(resolvedPath));

			if (File.Exists(quarantinePath))
			{
				File.Delete(quarantinePath);
			}

			File.Move(resolvedPath, quarantinePath);
			return QuarantineOutcome.Quarantined(quarantinePath);
		}
		catch (IOException ex)
		{
			return QuarantineOutcome.Failed(ex.Message);
		}
		catch (UnauthorizedAccessException ex)
		{
			return QuarantineOutcome.Failed(ex.Message);
		}
	}

	/// <summary>
	/// <see cref="QuarantineFile"/>'s outcome (issue #1486 review round 2, finding 1):
	/// distinguishes "nothing to quarantine" (<see cref="NotNeeded"/>, the verifier
	/// never resolved a path) from a move that was attempted and failed
	/// (<see cref="Failed"/>) -- the two were collapsed to the same <c>null</c> before
	/// this round, which silently reopened the round-1 laundering exposure on any
	/// quarantine-move failure (most plausibly <c>IOException</c> from the small
	/// <c>ToolStatePath</c> volume filling up, per <see cref="ManagedToolOptions.BinariesDownloadQuarantineDirectoryName"/>).
	/// </summary>
	private sealed record QuarantineOutcome(QuarantineResultKind Kind, string? Path, string? FailureDetail)
	{
		public static readonly QuarantineOutcome NotNeeded = new(QuarantineResultKind.NotNeeded, null, null);

		public static QuarantineOutcome Quarantined(string path) => new(QuarantineResultKind.Quarantined, path, null);

		public static QuarantineOutcome Failed(string detail) => new(QuarantineResultKind.Failed, null, detail);
	}

	private enum QuarantineResultKind
	{
		NotNeeded,
		Quarantined,
		Failed,
	}

	private static async Task EmitVerificationAlertAsync(
		JobExecutionContext context, string externalId, Guid depotArtifactId, string reason, CancellationToken cancellationToken)
	{
		// Issue #1486 review round 1, note D (non-blocking, #1636 owns the repo-wide
		// fix): the sole consumer (JobLogDrawer.tsx) renders `data.message`, not
		// `data.reason` -- emitting under `message` here is a one-word change that
		// keeps this alert from reaching the operator as a blank line, ahead of
		// #1636's broader cleanup of the other four emitters.
		string payload = JsonSerializer.Serialize(new
		{
			kind = "download.verification_failed",
			depot_artifact_id = depotArtifactId,
			external_id = externalId,
			message = reason,
		});
		await context.Events.EmitAsync(JobEventTypes.SystemNotice, context.Job.Id, context.Job.RunId, payload, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #1041: resolves the tool's eventual destination path under
	/// <paramref name="depotStorePath"/> for progress sampling, purely so the sampler
	/// has something to stat before <see cref="IBinaryDownloadVerifier"/> ever runs --
	/// mirrors <c>BinaryDownloadVerifier.ResolveConfinedPath</c>'s identical confinement
	/// guard rather than exposing that private helper. Null (never throws) when
	/// <paramref name="externalId"/> would resolve outside the depot store root; the
	/// sampler then reports zero bytes until the path exists (or, in the escape case,
	/// forever) rather than tracking an untrusted or out-of-root location.
	/// </summary>
	private static string? ResolveDestinationPath(string depotStorePath, string externalId)
	{
		string fullRoot = Path.GetFullPath(depotStorePath);
		string fullPath = Path.GetFullPath(Path.Combine(depotStorePath, externalId));
		bool confined = fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
			&& (fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
		return confined ? fullPath : null;
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

	private sealed record BinariesDownloadPayload(string? DepotArtifactId, string? ExternalId, string? BundleId);
}
