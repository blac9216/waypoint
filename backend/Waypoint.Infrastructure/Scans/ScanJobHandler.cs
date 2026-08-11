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
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// The <c>scan</c> job type's handler (issues #274/#275, second and third slices of
/// the #23 split; replaces the #273 not-implemented stub). <c>scan</c> is
/// <see cref="JobShape.Standard"/> (<c>queued -&gt; running -&gt; attesting -&gt;
/// converting -&gt; uploaded</c>, ADR-0012): this handler resolves target + credential
/// and runs InSpec (<c>Stage == null</c>), applies resolved attestations to the HDF
/// (<c>Stage == "attesting"</c>), then converts to CKL and stamps benchmark metadata
/// (<c>Stage == "converting"</c>) -- reporting <see cref="JobOutcomeKind.StageComplete"/>
/// after each non-terminal stage and <see cref="JobOutcomeKind.Succeeded"/> (which the
/// dispatcher forces to <c>uploaded</c>, this shape's terminal state) after convert.
/// <c>uploaded</c> here means "artifacts ready"; #311/PR #318 added the actual STIG
/// Manager upload as a post-convert action inside the convert stage (not a new pipeline
/// stage), gated behind <see cref="ScanUploadCoordinator"/>.
///
/// An <c>ssh</c>-kind target (SRG products: Photon/Aria/vIDM) is <see cref="JobShape.Srg"/>
/// instead (<c>queued -&gt; running -&gt; attesting -&gt; done</c>, issue #309): the InSpec
/// stage dispatches to <c>Invoke-WaypointSrgScan</c> (sudo-aware) and the attest stage
/// reports <see cref="JobOutcomeKind.Succeeded"/> directly rather than advancing to
/// <c>converting</c> -- so the convert stage (and its STIG Manager upload) is
/// unreachable for SRG by construction, matching the HDF-only, no-CKL,
/// no-STIG-Manager-upload predecessor behavior.
///
/// Credential resolution mirrors <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/>:
/// a stored credential (<c>jobs.credential_id</c>) is decrypted under job/run
/// attribution (security.md control 4), and a stored vCenter credential with no
/// dedicated <see cref="CredentialResponse.Username"/> fails cleanly rather than
/// falling back to the display name (#262). <c>jobs.credential_id</c> NULL means the ad
/// hoc "my credentials" flow (ADR-0011, #276): the secret comes from
/// <see cref="IEphemeralCredentialCache.TryTake"/>, single-shot, never falling back to
/// a stored credential -- a job resumed after its ephemeral entry is gone (TTL,
/// process restart, or a prior claim already took it) fails auth-style, exactly like a
/// rejected credential.
/// </summary>
public sealed class ScanJobHandler : IJobHandler
{
	internal const string AttestingStage = "attesting";
	internal const string ConvertingStage = "converting";
	private const string InvocationCommand = "Invoke-WaypointScan";
	private const string NsxInvocationCommand = "Invoke-WaypointNsxScan";
	private const string SrgInvocationCommand = "Invoke-WaypointSrgScan";
	private const string AttestCommand = "Invoke-WaypointAttest";
	private const string ConvertCommand = "Invoke-WaypointConvert";
	private const int LogTailLines = 20;

	private readonly IPowerShellExecutor _executor;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly TargetRepository _targets;
	private readonly IEphemeralCredentialCache _ephemeralCredentials;
	private readonly IJobRunnerRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;
	private readonly IOptions<ScanOptions> _scanOptions;
	private readonly ConfigDocRepository _configDocs;
	private readonly AttestationSnapshotRepository _attestationSnapshots;
	private readonly ScanUploadCoordinator _upload;

	public ScanJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		TargetRepository targets,
		IEphemeralCredentialCache ephemeralCredentials,
		IJobRunnerRepository jobs,
		ISecretRedactor redactor,
		IOptions<PowerShellOptions> powerShellOptions,
		IOptions<ScanOptions> scanOptions,
		ConfigDocRepository configDocs,
		AttestationSnapshotRepository attestationSnapshots,
		ScanUploadCoordinator upload)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(ephemeralCredentials);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(powerShellOptions);
		ArgumentNullException.ThrowIfNull(scanOptions);
		ArgumentNullException.ThrowIfNull(configDocs);
		ArgumentNullException.ThrowIfNull(attestationSnapshots);
		ArgumentNullException.ThrowIfNull(upload);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_ephemeralCredentials = ephemeralCredentials;
		_jobs = jobs;
		_redactor = redactor;
		_powerShellOptions = powerShellOptions;
		_scanOptions = scanOptions;
		_configDocs = configDocs;
		_attestationSnapshots = attestationSnapshots;
		_upload = upload;
	}

	public string JobType => "scan";

	public Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		// ADR-0012 stage routing: a fresh claim (Stage == null) runs the InSpec stage;
		// a job resumed at a durable marker resumes at that stage's body instead of
		// re-running earlier ones. Any other marker is unreachable today (Standard's
		// only stages are attesting/converting) -- failing cleanly rather than
		// throwing keeps a future new marker's outcome predictable instead of an
		// opaque "Unhandled exception".
		return context.Job.Stage switch
		{
			null => ExecuteInspecStageAsync(context, cancellationToken),
			AttestingStage => ExecuteAttestStageAsync(context, cancellationToken),
			ConvertingStage => ExecuteConvertStageAsync(context, cancellationToken),
			_ => Task.FromResult(JobExecutionOutcome.Failed(
				$"not_implemented: scan stage '{context.Job.Stage}' has no handler.")),
		};
	}

	private async Task<JobExecutionOutcome> ExecuteInspecStageAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ScanPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(payload.TargetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' does not exist.");
		}

		if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal)
			&& !string.Equals(target.Kind, TargetKinds.NsxApi, StringComparison.Ordinal)
			&& !string.Equals(target.Kind, TargetKinds.Ssh, StringComparison.Ordinal))
		{
			return JobExecutionOutcome.Failed(
				$"target '{payload.TargetId}' is kind '{target.Kind}'; the InSpec stage only supports '{TargetKinds.VSphere}', '{TargetKinds.NsxApi}', and '{TargetKinds.Ssh}'.");
		}

		string? host = TryGetConnectionHost(target.ConnectionJson);
		if (string.IsNullOrWhiteSpace(host))
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' has no 'connection.host' to scan.");
		}

		ResolvedCredential resolved;
		try
		{
			resolved = await ResolveCredentialAsync(context, cancellationToken).ConfigureAwait(false);
		}
		catch (ScanCredentialException exception)
		{
			return JobExecutionOutcome.Failed(exception.Message);
		}

		string reportPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.json");
		bool isNsx = string.Equals(target.Kind, TargetKinds.NsxApi, StringComparison.Ordinal);
		bool isSrg = string.Equals(target.Kind, TargetKinds.Ssh, StringComparison.Ordinal);

		PowerShellExecutionResult result;
		try
		{
			// The NSX path passes Manager/Username/Password/ProfilePath/ReportPath to
			// Invoke-WaypointNsxScan, which acquires the session token itself, inside its
			// own PowerShell invocation -- the token is generated and consumed entirely
			// within that call and never crosses back into this C# handler, so there is
			// nothing here to additionally track via ISecretTracker: the token's whole
			// lifetime sits inside the same bound-parameter, non-argv, non-logged
			// invocation the vSphere password already relies on (security.md controls
			// 1/2). Password is still resolved and bound the same way for both kinds --
			// NSX authenticates to /api/session/create with it before InSpec ever runs.
			//
			// The SRG (ssh) path passes Sudo/SudoRequiresPassword alongside the same
			// Host/Username/Password shape (issue #309 AC "sudo_enabled honored"):
			// SudoEnabled comes from the resolved credential's typed field for a stored
			// credential (#249), or false for the ephemeral "my credentials" tier, which
			// carries no sudo flag (ADR-0011 scope -- see ResolveCredentialAsync). The ssh
			// password doubles as the sudo password when sudo is enabled, matching the
			// vendor's own module.scan.ps1 SRG branch (Config.Sudo -> --sudo,
			// SudoRequiresPassword -> the same credential's password via --config).
			Dictionary<string, object?> parameters;
			string invocationCommand;
			if (isNsx)
			{
				invocationCommand = NsxInvocationCommand;
				parameters = new(StringComparer.Ordinal)
				{
					["Manager"] = host,
					["Username"] = resolved.Username,
					["Password"] = resolved.Secret,
					["ProfilePath"] = _scanOptions.Value.NsxProfilePath,
					["ReportPath"] = reportPath,
					["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
				};
			}
			else if (isSrg)
			{
				invocationCommand = SrgInvocationCommand;
				parameters = new(StringComparer.Ordinal)
				{
					["SshHost"] = host,
					["Username"] = resolved.Username,
					["Password"] = resolved.Secret,
					["ProfilePath"] = _scanOptions.Value.SrgProfilePath,
					["ReportPath"] = reportPath,
					["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
					["Sudo"] = resolved.SudoEnabled,
					["SudoRequiresPassword"] = true,
				};
			}
			else
			{
				invocationCommand = InvocationCommand;
				parameters = new(StringComparer.Ordinal)
				{
					["VCenter"] = host,
					["Username"] = resolved.Username,
					["Password"] = resolved.Secret,
					["ProfilePath"] = _scanOptions.Value.ProfilePath,
					["ReportPath"] = reportPath,
					["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
				};
			}

			PowerShellRequest request = new(
				invocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
			result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// Ends the in-play redaction window as soon as the invocation is done, same
			// discipline as DiscoverJobHandler regardless of which credential tier this
			// came from -- a stored credential's DecryptedSecret disposes its own
			// redaction handle; TryTake's ephemeral entry already ended its window at
			// the moment it was taken (IEphemeralCredentialCache.TryTake's contract), so
			// Release is a no-op there.
			resolved.Release();
		}

		// Transport-level failure (the invocation itself threw, timed out, or a native
		// exit code outside 0 -- WaypointScan.psm1 never lets a native `inspec` call
		// leave a non-{0,100,101} exit code unhandled, so a nonzero NativeExitCode here
		// means Invoke-ExternalCommand's own AllowedExitCodes check rejected it).
		if (!result.Succeeded)
		{
			string rawNote = result.FailureReason ?? "scan invocation failed with no failure reason.";
			return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
		}

		// Predecessor constraint (#274 AC): InSpec exit codes 100 (compliance failures
		// present) and 101 (skipped controls present) are a completed, reportable scan,
		// not a tool failure -- WaypointScan.psm1 already absorbed that mapping at the
		// `Invoke-ExternalCommand -AllowedExitCodes @(0,100,101)` layer, so a
		// `Succeeded` PowerShellExecutionResult never reaches here for one of those
		// codes. What remains to check is the module's own Success/FailureReason
		// output -- a normal (non-throwing, zero-native-exit) invocation whose function
		// body itself reported failure (e.g. an auth rejection with no native exit code
		// at all) still needs classifying here, exactly as
		// DownloadJobHandler.TryParseOutput inspects its own module's returned object
		// rather than trusting the executor's Succeeded alone.
		ScanInvocationOutput? output = TryParseOutput(result.Output);
		if (output is null)
		{
			return await FailScanAsync(context, "scan invocation returned no result.", cancellationToken).ConfigureAwait(false);
		}

		if (!output.Success)
		{
			string rawNote = output.FailureReason ?? "scan invocation reported failure with no reason.";
			return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(output.ReportPath) || !File.Exists(output.ReportPath))
		{
			return await FailScanAsync(context, "scan invocation reported success but produced no HDF report file.", cancellationToken).ConfigureAwait(false);
		}

		await context.AdvanceAsync(JobStates.Attesting, "InSpec scan complete; HDF persisted.", cancellationToken).ConfigureAwait(false);
		return JobExecutionOutcome.StageComplete(AttestingStage, $"HDF persisted at '{output.ReportPath}'.");
	}

	/// <summary>
	/// The attest stage (issue #275): resolves the target's attestation config-doc via
	/// <see cref="ConfigDocResolver.Resolve"/> (Global -&gt; Site -&gt; Target,
	/// most-specific-wins, same resolution <c>GET /config-docs/resolve</c> uses) and
	/// applies it to the HDF with the SAF CLI. An expired waiver is never applied --
	/// <see cref="ConfigDocResolution.AttestationExpired"/> already encodes that per
	/// <see cref="ConfigDocResolver"/>'s fall-through -- so it is reported as a WARN
	/// <c>job.log</c> event and folded into this stage's <see cref="JobExecutionOutcome.Note"/>
	/// (docs/domain-model.md: "the control reports Open, the run logs a WARN, and
	/// Results lists expired attestations explicitly"). No resolved doc at all (a
	/// target with no attestation config-doc anywhere in the three layers) is an
	/// equally valid path: the HDF passes through unattested.
	/// </summary>
	private async Task<JobExecutionOutcome> ExecuteAttestStageAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ScanPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(payload.TargetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' does not exist.");
		}

		string reportPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.json");
		if (!File.Exists(reportPath))
		{
			return JobExecutionOutcome.Failed($"attest stage found no HDF report at '{reportPath}' (InSpec stage did not persist one).");
		}

		string profile = _scanOptions.Value.AttestationProfile;
		ConfigDocWithLatestVersion? global = await _configDocs
			.FindWithLatestVersionAsync(ConfigDocKinds.Attestation, profile, ConfigDocLayers.Global, null, cancellationToken).ConfigureAwait(false);
		ConfigDocWithLatestVersion? site = await _configDocs
			.FindWithLatestVersionAsync(ConfigDocKinds.Attestation, profile, ConfigDocLayers.Site, target.SiteId, cancellationToken).ConfigureAwait(false);
		ConfigDocWithLatestVersion? targetDoc = await _configDocs
			.FindWithLatestVersionAsync(ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, target.Id, cancellationToken).ConfigureAwait(false);

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(
			ConfigDocKinds.Attestation, profile, global, site, targetDoc, DateTimeOffset.UtcNow);

		if (resolution.AttestationExpired)
		{
			string warnLine = $"config-doc attestation for profile '{profile}' target '{payload.TargetId}' expired "
				+ $"{resolution.AttestationExpiresAt:O}; not applied (control remains Open).";
			await EmitWarnAsync(context, warnLine, cancellationToken).ConfigureAwait(false);
		}

		// Issue #306: persist THIS resolution, right now, as the immutable at-scan-time
		// record -- before anything downstream (or any later edit to the config-doc) can
		// affect what GET /runs/{id}/attestations-applied will ever report for this run.
		// context.Job.RunId is null only for a scan job created outside the normal
		// run-fan-out path, which does not happen in practice (RunsController always
		// fans scan jobs out from a run) -- skipped defensively rather than failing the
		// scan, since there would be no run to attribute the row to.
		if (context.Job.RunId is { } runId)
		{
			await _attestationSnapshots.RecordAsync(
				runId,
				context.Job.Id,
				target.Id,
				profile,
				resolution.Layer ?? ConfigDocLayers.Global,
				resolution.DocId,
				resolution.Version,
				resolution.Author,
				resolution.UpdatedAt,
				applied: resolution.Body is not null,
				expired: resolution.AttestationExpired,
				cancellationToken).ConfigureAwait(false);
		}

		string? templatePath = null;
		string? tempFile = null;
		try
		{
			if (resolution.Body is not null)
			{
				tempFile = Path.Combine(Path.GetTempPath(), $"waypoint-attest-{context.Job.Id:N}.yml");

				// Issue #304's 0600 pattern (mirrors #308/PR #315's NSX inputs-file fix):
				// create the file empty, narrow its mode to owner-only on Unix, THEN write
				// the resolved attestation body -- so it is never world-readable (the shared
				// system temp dir's umask default is typically 0644) even briefly. The body
				// is waiver content (status/justification/expires), not a raw secret, but a
				// justification can carry sensitive rationale worth this defense in depth.
				using (FileStream createStream = File.Create(tempFile))
				{
					if (!OperatingSystem.IsWindows())
					{
						File.SetUnixFileMode(
							tempFile,
							UnixFileMode.UserRead | UnixFileMode.UserWrite);
					}
				}

				await File.WriteAllTextAsync(tempFile, resolution.Body, cancellationToken).ConfigureAwait(false);
				templatePath = tempFile;
			}

			string attestedPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.attested.json");
			Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
			{
				["ReportPath"] = reportPath,
				["AttestTemplatePath"] = templatePath,
				["AttestedReportPath"] = attestedPath,
				["TimeoutSeconds"] = _scanOptions.Value.SafTimeoutSeconds,
			};

			PowerShellRequest request = new(AttestCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
			PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

			if (!result.Succeeded)
			{
				return await FailScanAsync(context, result.FailureReason ?? "attest invocation failed with no failure reason.", cancellationToken).ConfigureAwait(false);
			}

			AttestInvocationOutput? output = TryParseAttestOutput(result.Output);
			if (output is null || !output.Success)
			{
				string rawNote = output?.FailureReason ?? "attest invocation returned no result.";
				return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
			}

			string note = resolution.AttestationExpired
				? $"attest: {(output.AttestApplied ? "applied" : "none applied")}; 1 expired-skipped (profile '{profile}')."
				: $"attest: {(output.AttestApplied ? "applied" : "none applied")}.";

			// A fresh claim always resumes at `running` (JobQueueRepository.ClaimSql
			// unconditionally sets state = 'running' regardless of the durable `stage`
			// marker -- only `stage` records pipeline position, per ADR-0012). This
			// stage's own resumption therefore first replays the `running -> attesting`
			// transition PR #298's InSpec stage made durable on the previous cycle,
			// before advancing further -- legal per JobStateMachine for both shapes, and
			// consistent with how the row's `state` column always reads 'running' the
			// instant any stage is (re)claimed.
			await context.AdvanceAsync(JobStates.Attesting, "attest stage claimed.", cancellationToken).ConfigureAwait(false);

			// Issue #309 AC "HDF-only: attest then terminate at done, NO convert, NO CKL,
			// NO STIG Manager upload": an ssh (SRG) target's attest stage is the Srg
			// shape's LAST stage (JobStateMachine.SrgTransitions: attesting -> done),
			// unlike Standard's attesting -> converting. Reporting Succeeded here (rather
			// than StageComplete(ConvertingStage)) makes ExecuteAsync's Stage switch never
			// see 'converting' for this target at all -- so the convert stage's STIG
			// Manager upload path (#311/#318, not yet on main when this slice branched) is
			// unreachable for SRG by construction, not by a runtime kind check inside
			// convert. If a future change makes convert reachable independent of shape,
			// that guard-by-unreachability assumption breaks and needs re-verifying here.
			if (string.Equals(target.Kind, TargetKinds.Ssh, StringComparison.Ordinal))
			{
				return JobExecutionOutcome.Succeeded(note);
			}

			await context.AdvanceAsync(JobStates.Converting, note, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.StageComplete(ConvertingStage, note);
		}
		finally
		{
			if (tempFile is not null && File.Exists(tempFile))
			{
				File.Delete(tempFile);
			}
		}
	}

	/// <summary>
	/// The convert stage (issue #275): converts the (optionally attested) HDF to a CKL
	/// with the SAF CLI, stamps <see cref="ScanOptions.BenchmarkMetadata"/>, and
	/// persists the CKL next to the HDF under the same artifact-store keying (job id).
	/// This is the pipeline's last stage for <see cref="JobShape.Standard"/> -- success
	/// here reports <see cref="JobOutcomeKind.Succeeded"/>, which the dispatcher forces
	/// to the shape's terminal state (<c>uploaded</c>, meaning "artifacts ready"; the
	/// STIG Manager upload itself is #25).
	/// </summary>
	private async Task<JobExecutionOutcome> ExecuteConvertStageAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ScanPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(payload.TargetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' does not exist.");
		}

		string attestedPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.attested.json");
		string rawPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.json");
		string convertInput = File.Exists(attestedPath) ? attestedPath : rawPath;
		if (!File.Exists(convertInput))
		{
			return JobExecutionOutcome.Failed($"convert stage found no HDF report for job '{context.Job.Id}'.");
		}

		string cklPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.ckl");
		ScanBenchmarkMetadata? staticMetadata = _scanOptions.Value.BenchmarkMetadata.GetValueOrDefault(target.Kind);

		// Issue #311: enrich the static catalog stamp with STIG Manager's installed
		// title/release/version when reachable -- degrades to staticMetadata unchanged
		// on any failure (ScanUploadCoordinator.ResolveBenchmarkMetadataAsync never
		// throws), so this call site needs no try/catch of its own, matching the
		// predecessor Resolve-BenchmarkMetadata's "resolution never throws" contract.
		StigManagerBenchmarkMetadata fallback = new(staticMetadata?.BenchmarkId, staticMetadata?.Title, staticMetadata?.ReleaseInfo, staticMetadata?.Version);
		StigManagerBenchmarkMetadata metadata = await _upload
			.ResolveBenchmarkMetadataAsync(context.Job.Id, target, fallback, cancellationToken).ConfigureAwait(false);

		// Same replay as ExecuteAttestStageAsync: a fresh claim always resumes at
		// `running` (ClaimSql), so this stage first replays `running -> attesting`
		// (the InSpec stage's own durable transition) then `attesting -> converting`
		// before doing any convert work -- both legal per JobStateMachine, and correct
		// regardless of how the convert step itself turns out: a subsequent failure
		// legally transitions converting -> failed, and success legally transitions
		// converting -> uploaded (forced by the dispatcher on JobOutcomeKind.Succeeded).
		await context.AdvanceAsync(JobStates.Attesting, "convert stage claimed.", cancellationToken).ConfigureAwait(false);
		await context.AdvanceAsync(JobStates.Converting, "convert stage claimed.", cancellationToken).ConfigureAwait(false);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["ConvertInputPath"] = convertInput,
			["CklOutputPath"] = cklPath,
			["BenchmarkId"] = metadata.BenchmarkId,
			["Title"] = metadata.Title,
			["ReleaseInfo"] = metadata.ReleaseInfo,
			["Version"] = metadata.Version,
			["TimeoutSeconds"] = _scanOptions.Value.SafTimeoutSeconds,
		};

		PowerShellRequest request = new(ConvertCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
		PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

		if (!result.Succeeded)
		{
			return await FailScanAsync(context, result.FailureReason ?? "convert invocation failed with no failure reason.", cancellationToken).ConfigureAwait(false);
		}

		ConvertInvocationOutput? output = TryParseConvertOutput(result.Output);
		if (output is null || !output.Success)
		{
			string rawNote = output?.FailureReason ?? "convert invocation returned no result.";
			return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(output.CklPath) || !File.Exists(output.CklPath))
		{
			return await FailScanAsync(context, "convert invocation reported success but produced no CKL file.", cancellationToken).ConfigureAwait(false);
		}

		// Issue #311: post-convert upload, not a new pipeline stage -- ADR-0012's
		// `uploaded` terminal already means "artifacts ready" (PR #302), so this action
		// runs inside the same convert-stage execution rather than resting the job at a
		// new intermediate marker. ScanUploadCoordinator.UploadAsync never throws and
		// always persists a jobs.upload_status regardless of outcome; this stage reports
		// Succeeded either way (issue #311 AC: "upload failure must NEVER fail the scan
		// run" -- artifacts stay downloadable, only upload_status reflects the failure).
		// Only reachable from JobShape.Standard's convert stage, so SRG/HDF-only runs
		// (#24/#309) never call this -- they terminate at `attesting -> done` and this
		// method is never entered for them.
		StigManagerUploadResult uploadResult = await _upload.UploadAsync(context.Job.Id, target, output.CklPath, cancellationToken).ConfigureAwait(false);
		string uploadNote = uploadResult.Outcome switch
		{
			StigManagerUploadOutcome.Uploaded => "uploaded to STIG Manager",
			StigManagerUploadOutcome.Conflict => "STIG Manager reported a conflict (retry available)",
			_ => $"STIG Manager upload failed: {uploadResult.Detail ?? "no detail"}",
		};

		return JobExecutionOutcome.Succeeded(
			$"CKL persisted at '{output.CklPath}' (benchmark metadata applied: {output.MetadataApplied}; {uploadNote}).");
	}

	/// <summary>
	/// Classifies a raw failure reason (auth-shaped -&gt; the credential halt path,
	/// otherwise ordinary), redacts it for the two sinks it reaches (jobs.note,
	/// job.log), and emits the log-tail event (#274 AC "real failures mapped to failed
	/// with the log tail in job events").
	/// </summary>
	private async Task<JobExecutionOutcome> FailScanAsync(JobExecutionContext context, string rawNote, CancellationToken cancellationToken)
	{
		bool isAuthFailure = AuthFailureClassifier.IsAuthFailure(rawNote, [.. _powerShellOptions.Value.AuthFailureMarkers]);
		string note = _redactor.Redact(rawNote);
		await EmitLogTailAsync(context, note, cancellationToken).ConfigureAwait(false);
		return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
	}

	/// <summary>
	/// Stored credential (decrypt-under-identity) when <c>jobs.credential_id</c> is set,
	/// otherwise the ad hoc ephemeral credential registered for this job id (#276). The
	/// two tiers never mix: a NULL <c>credential_id</c> with no ephemeral entry available
	/// (never registered, TTL-expired, or already taken by a prior claim) is an
	/// auth-style failure, never a silent fall-through to a stored credential -- see
	/// <see cref="IEphemeralCredentialCache"/>'s "no personal rows, ever" contract.
	/// </summary>
	private async Task<ResolvedCredential> ResolveCredentialAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		if (context.Job.CredentialId is not { } credentialId)
		{
			// TryTake is single-shot and already ends the in-play redaction window on a
			// successful take (IEphemeralCredentialCache's contract) -- there is nothing
			// further for this handler to release for the ephemeral tier.
			EphemeralCredential? ephemeral = _ephemeralCredentials.TryTake(context.Job.Id);
			if (ephemeral is null)
			{
				throw new ScanCredentialException(
					$"job '{context.Job.Id}' has no stored credential and no ephemeral credential is available (never registered, expired, or already claimed).");
			}

			// The ephemeral "my credentials" tier (ADR-0011, #276) carries no sudo flag --
			// it is a personal, ad hoc secret with no stored typed-credential row to read
			// SudoEnabled from -- so an SRG scan using an ephemeral credential always runs
			// without sudo. Sudo (#249's typed credentials field) is only meaningful for a
			// stored credential.
			return new ResolvedCredential(ephemeral.Username, ephemeral.Secret, SudoEnabled: false, static () => { });
		}

		CredentialResponse? credential = await _credentials.GetAsync(credentialId, cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			throw new ScanCredentialException($"job '{context.Job.Id}' references credential '{credentialId}', which no longer exists.");
		}

		if (string.IsNullOrWhiteSpace(credential.Username))
		{
			// Issue #262: no falling back to Name as the SSO login -- same rule
			// DiscoverJobHandler enforces.
			throw new ScanCredentialException(
				$"job '{context.Job.Id}' references credential '{credential.Id}', which has no username set; set one before scanning this target.");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);
		DecryptedSecret decrypted;
		try
		{
			decrypted = await _secrets
				.DecryptAsync(credentialId, actor, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			throw new ScanCredentialException($"target credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			throw new ScanCredentialException($"target credential could not be decrypted: {exception.Message}");
		}

		return new ResolvedCredential(credential.Username!, decrypted.Value, credential.SudoEnabled, decrypted.Dispose);
	}

	/// <summary>Attribution for the decrypt audit row (security.md control 4): the run's initiator when recorded, falling back to a fixed system marker -- same pattern as DiscoverJobHandler.ResolveActorAsync.</summary>
	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}

	/// <summary>
	/// Log-tail-on-failure (#274 AC "real failures mapped to failed with the log tail in
	/// job events"), mirroring module.scan.ps1's own <c>-SurfaceOutputOnFailure</c>
	/// convention: the redacted failure note is the tail this handler has -- the
	/// PowerShell layer's own captured-output-on-failure already ran through the same
	/// redacting <c>job.log</c> buffer via <see cref="IPowerShellExecutor"/>, so this
	/// emits one more line summarizing the outcome rather than duplicating that stream.
	/// </summary>
	private static async Task EmitLogTailAsync(JobExecutionContext context, string note, CancellationToken cancellationToken)
	{
		string[] lines = note.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string tail = string.Join('\n', lines.TakeLast(LogTailLines));
		string payload = JsonSerializer.Serialize(new { severity = "Error", line = tail });
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The expired-attestation WARN (#275 AC, docs/domain-model.md: "the run logs a
	/// WARN, and Results lists expired attestations explicitly") -- same job.log
	/// event/severity shape as <see cref="EmitLogTailAsync"/>'s Error line, at Warning
	/// severity instead, so #27's Results sidebar can distinguish the two.
	/// </summary>
	private static async Task EmitWarnAsync(JobExecutionContext context, string line, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new { severity = "Warning", line });
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	private static string? TryGetConnectionHost(string connectionJson)
	{
		using JsonDocument document = JsonDocument.Parse(connectionJson);
		return document.RootElement.TryGetProperty("host", out JsonElement hostElement) && hostElement.ValueKind == JsonValueKind.String
			? hostElement.GetString()
			: null;
	}

	/// <summary>Parses Invoke-WaypointScan's returned [pscustomobject] the same way DownloadJobHandler.TryParseOutput reads its own module's result -- the executor's own Succeeded only reflects the transport, not what the function body itself reported.</summary>
	private static ScanInvocationOutput? TryParseOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		bool success = psObject.Properties["Success"]?.Value is true;
		string? reportPath = psObject.Properties["ReportPath"]?.Value as string;
		string? failureReason = psObject.Properties["FailureReason"]?.Value as string;
		return new ScanInvocationOutput(success, reportPath, failureReason);
	}

	/// <summary>Parses Invoke-WaypointAttest's returned [pscustomobject], same rationale as <see cref="TryParseOutput"/>.</summary>
	private static AttestInvocationOutput? TryParseAttestOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		bool success = psObject.Properties["Success"]?.Value is true;
		bool attestApplied = psObject.Properties["AttestApplied"]?.Value is true;
		string? failureReason = psObject.Properties["FailureReason"]?.Value as string;
		return new AttestInvocationOutput(success, attestApplied, failureReason);
	}

	/// <summary>Parses Invoke-WaypointConvert's returned [pscustomobject], same rationale as <see cref="TryParseOutput"/>.</summary>
	private static ConvertInvocationOutput? TryParseConvertOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		bool success = psObject.Properties["Success"]?.Value is true;
		string? cklPath = psObject.Properties["CklPath"]?.Value as string;
		bool metadataApplied = psObject.Properties["MetadataApplied"]?.Value is true;
		string? failureReason = psObject.Properties["FailureReason"]?.Value as string;
		return new ConvertInvocationOutput(success, cklPath, metadataApplied, failureReason);
	}

	private static ScanPayload ParsePayload(string payloadJson)
	{
		using JsonDocument document = JsonDocument.Parse(payloadJson);
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("target_id", out JsonElement targetIdElement) || !Guid.TryParse(targetIdElement.GetString(), out Guid targetId))
		{
			throw new ArgumentException("payload requires a GUID string 'target_id' property");
		}

		return new ScanPayload(targetId);
	}

	private sealed record ScanPayload(Guid TargetId);

	private sealed record ResolvedCredential(string Username, string Secret, bool SudoEnabled, Action Release);

	private sealed record ScanInvocationOutput(bool Success, string? ReportPath, string? FailureReason);

	private sealed record AttestInvocationOutput(bool Success, bool AttestApplied, string? FailureReason);

	private sealed record ConvertInvocationOutput(bool Success, string? CklPath, bool MetadataApplied, string? FailureReason);

	/// <summary>Internal-only signal from <see cref="ResolveCredentialAsync"/> to its caller; never crosses a handler boundary as a thrown exception.</summary>
	private sealed class ScanCredentialException(string message) : Exception(message);
}
