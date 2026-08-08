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
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// The <c>scan</c> job type's handler (issue #274, second slice of the #23 split;
/// replaces the #273 not-implemented stub). <c>scan</c> is <see cref="JobShape.Standard"/>
/// (<c>queued -&gt; running -&gt; attesting -&gt; converting -&gt; uploaded</c>,
/// ADR-0012): this handler owns only the first stage -- resolve target + credential,
/// run the sibling <c>vmware-stig-docker</c> InSpec step through the runspace host, and
/// persist the resulting HDF -- then reports <see cref="JobOutcomeKind.StageComplete"/>
/// to rest the job, lease-cleared, at the <c>attesting</c> marker. Attest/convert
/// bodies are #275; a job claimed with that marker fails cleanly with a
/// <c>not_implemented</c> note (mirroring #273's original stub) so a run today ends
/// predictably instead of hanging.
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
	private const string InvocationCommand = "Invoke-WaypointScan";
	private const int LogTailLines = 20;

	private readonly IPowerShellExecutor _executor;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly TargetRepository _targets;
	private readonly IEphemeralCredentialCache _ephemeralCredentials;
	private readonly IJobQueueRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;
	private readonly IOptions<ScanOptions> _scanOptions;

	public ScanJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		TargetRepository targets,
		IEphemeralCredentialCache ephemeralCredentials,
		IJobQueueRepository jobs,
		ISecretRedactor redactor,
		IOptions<PowerShellOptions> powerShellOptions,
		IOptions<ScanOptions> scanOptions)
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

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_ephemeralCredentials = ephemeralCredentials;
		_jobs = jobs;
		_redactor = redactor;
		_powerShellOptions = powerShellOptions;
		_scanOptions = scanOptions;
	}

	public string JobType => "scan";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		// ADR-0012 stage routing: a fresh claim (Stage == null) runs the InSpec stage
		// below. Any other marker means a prior execution already reached (or rested
		// at) a later stage -- attest/convert bodies are #275, so this handler cannot
		// resume them yet. Failing cleanly here (rather than throwing, which the
		// dispatcher would report as an opaque "Unhandled exception") keeps a run's
		// outcome predictable today: the job lands in `failed` with a note that says
		// exactly why, and #275 replaces this branch instead of ever needing to touch
		// the InSpec stage below.
		if (context.Job.Stage is not null)
		{
			return JobExecutionOutcome.Failed(
				$"not_implemented: scan stage '{context.Job.Stage}' has no handler yet -- attest/convert land in #275.");
		}

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

		if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal))
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' is kind '{target.Kind}'; the InSpec stage only supports '{TargetKinds.VSphere}'.");
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

		PowerShellExecutionResult result;
		try
		{
			Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
			{
				["VCenter"] = host,
				["Username"] = resolved.Username,
				["Password"] = resolved.Secret,
				["ProfilePath"] = _scanOptions.Value.ProfilePath,
				["ReportPath"] = reportPath,
				["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
			};

			PowerShellRequest request = new(InvocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
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

			return new ResolvedCredential(ephemeral.Username, ephemeral.Secret, static () => { });
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

		return new ResolvedCredential(credential.Username!, decrypted.Value, decrypted.Dispose);
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

	private sealed record ResolvedCredential(string Username, string Secret, Action Release);

	private sealed record ScanInvocationOutput(bool Success, string? ReportPath, string? FailureReason);

	/// <summary>Internal-only signal from <see cref="ResolveCredentialAsync"/> to its caller; never crosses a handler boundary as a thrown exception.</summary>
	private sealed class ScanCredentialException(string message) : Exception(message);
}
