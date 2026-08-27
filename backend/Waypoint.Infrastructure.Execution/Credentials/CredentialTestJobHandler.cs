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

using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Credentials;

/// <summary>
/// The <c>credential-test</c> <see cref="JobShape.Simple"/> job handler (issue #245,
/// epic #13): replaces the old synchronous decrypt-liveness-only
/// <c>POST /credentials/{id}/test</c> (issue #20) with a real per-type connectivity
/// probe -- open a session, close it, no state-changing calls -- run through the same
/// #6/#194 job-handler pattern (decrypt-under-identity, redacted, fail-closed audit)
/// every other handler uses.
///
/// A stored credential carries no host of its own (domain-model.md: targets reference
/// a credential, not the reverse -- "one global service account" is the degenerate
/// case of many targets sharing one), so this handler borrows the host from the first
/// target that references the credential (<see cref="TargetRepository.FindFirstByCredentialAsync"/>).
/// No target -&gt; nothing to dial -&gt; ordinary (non-auth) failure with a clear note,
/// same "misconfiguration, not a bad password" shape <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/>
/// already uses for a target with no host.
///
/// Payload contract: none needed -- <see cref="ClaimedJob.CredentialId"/> (set by
/// <c>CredentialsController.Test</c>'s fan-out) is the only input, so payload stays
/// <c>{}</c>.
///
/// <see cref="CredentialTypes.Token"/> has no dialable endpoint at all in this slice
/// (docs/api-contract.md does not name one) -- per #245's AC, a token credential test
/// stays decrypt-only: this handler still runs it through the same job/audit path
/// (rather than leaving the old synchronous 200) so the health-flip source is uniform,
/// it just never invokes PowerShell or looks up a target.
///
/// <see cref="CredentialTypes.DepotToken"/> (deprecated legacy alias, issue #252/#383,
/// revisited #560/#690), <see cref="CredentialTypes.DepotActivationCode"/>, and
/// <see cref="CredentialTypes.LegacyDownloadToken"/> (both issue #690) all get the
/// same decrypt-only treatment as <see cref="CredentialTypes.Token"/>, for a related
/// but distinct reason: the only thing in this codebase that actually authenticates to
/// the Broadcom Support Portal is the operator-installed, account-gated
/// <c>vcf-download-tool</c> binary (ADR-0015) -- and that binary, along with the
/// managed-tool volume it lives on, is mounted only into <c>download-runner</c>
/// (deploy/compose.yaml), never into <c>compliance-runner</c>, which is where
/// <c>credential-test</c> is architecturally pinned to run (<c>JobCapabilities.Compliance</c>).
/// <c>CatalogIndexJobHandler</c> (which does run on download-runner) resolves no
/// credential at all post-#690 -- its indexing walk is a pure filesystem read
/// (domain-model.md open question 4) that never authenticated to anything. There is
/// therefore no real depot-auth code path anywhere yet to invoke honestly from here;
/// wiring one is a job-routing change (moving the depot types' test to
/// download-runner) plus a #39 dependency (the tool must be installed to have anything
/// to invoke), tracked as a follow-up rather than built out in this slice. This handler
/// still proves the one thing it safely can: the stored value decrypts under the
/// appliance master key, through the same audited path every other credential type's
/// decrypt-only test already uses.
/// </summary>
public sealed class CredentialTestJobHandler : IJobHandler
{
	private const string VCenterCommand = "Invoke-WaypointVCenterCredentialTest";
	private const string NsxCommand = "Invoke-WaypointNsxCredentialTest";
	private const string SshCommand = "Invoke-WaypointSshCredentialTest";

	private readonly IPowerShellExecutor _executor;
	private readonly ICredentialSecretStore _secrets;
	private readonly CredentialRepository _credentials;
	private readonly TargetRepository _targets;
	private readonly IJobRunnerRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;

	public CredentialTestJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		CredentialRepository credentials,
		TargetRepository targets,
		IJobRunnerRepository jobs,
		ISecretRedactor redactor,
		IOptions<PowerShellOptions> powerShellOptions)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_jobs = jobs;
		_redactor = redactor;
		_powerShellOptions = powerShellOptions;
	}

	public string JobType => "credential-test";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (context.Job.CredentialId is not Guid credentialId)
		{
			return JobExecutionOutcome.Failed("credential-test job has no credential_id.");
		}

		CredentialResponse? credential = await _credentials.GetAsync(credentialId, cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			return JobExecutionOutcome.Failed($"credential '{credentialId}' no longer exists.");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);

		if (credential.CredentialType is CredentialTypes.Token or CredentialTypes.DepotToken
			or CredentialTypes.DepotActivationCode or CredentialTypes.LegacyDownloadToken)
		{
			return await RunDecryptOnlyAsync(credentialId, actor, context, cancellationToken).ConfigureAwait(false);
		}

		Target? target = await _targets.FindFirstByCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed(
				$"credential '{credentialId}' is not referenced by any target; there is no host to test connectivity against.");
		}

		string? host = TryGetConnectionHost(target.ConnectionJson);
		if (string.IsNullOrWhiteSpace(host))
		{
			return JobExecutionOutcome.Failed($"target '{target.Id}' has no 'connection.host' to test connectivity against.");
		}

		(string command, Dictionary<string, object?> extraParameters) = credential.CredentialType switch
		{
			CredentialTypes.VCenter => (VCenterCommand, new Dictionary<string, object?>(StringComparer.Ordinal) { ["VCenter"] = host }),
			CredentialTypes.Nsx => (NsxCommand, new Dictionary<string, object?>(StringComparer.Ordinal) { ["Manager"] = host }),
			CredentialTypes.Ssh => (SshCommand, new Dictionary<string, object?>(StringComparer.Ordinal) { ["SshHost"] = host }),
			_ => (string.Empty, []),
		};

		if (command.Length == 0)
		{
			return JobExecutionOutcome.Failed($"credential type '{credential.CredentialType}' has no connectivity test defined.");
		}

		if (credential.CredentialType != CredentialTypes.Ssh && string.IsNullOrWhiteSpace(credential.Username))
		{
			return JobExecutionOutcome.Failed(
				$"credential '{credentialId}' has no username set; set one (PUT /credentials/{{id}}) before testing connectivity.");
		}

		PowerShellExecutionResult result;
		DecryptedSecret? decrypted = null;
		try
		{
			// security.md control 4 / #8's fail-closed decrypt audit: writes the
			// secret.decrypted row before any plaintext reaches this method.
			decrypted = await _secrets.DecryptAsync(credentialId, actor, context.Job.Id, context.Job.RunId, cancellationToken).ConfigureAwait(false);

			if (credential.CredentialType != CredentialTypes.Ssh)
			{
				extraParameters["Username"] = credential.Username;
				extraParameters["Password"] = decrypted.Value;
			}

			PowerShellRequest request = new(command, PowerShellRequestKind.Command, extraParameters, context.Job.Id, context.Job.RunId);
			result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: false, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed($"credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: false, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed($"credential could not be decrypted: {exception.Message}");
		}
		finally
		{
			// Ends the in-play redaction window as soon as the invocation is done.
			decrypted?.Dispose();
		}

		return await ClassifyResultAsync(credentialId, credential.CredentialType, result, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// <see cref="CredentialTypes.Token"/>, <see cref="CredentialTypes.DepotToken"/>,
	/// <see cref="CredentialTypes.DepotActivationCode"/>, and
	/// <see cref="CredentialTypes.LegacyDownloadToken"/> have no endpoint to dial in
	/// this slice -- the same decrypt-liveness check issue #20's synchronous handler
	/// ran, now driven through the job/audit path so the health flip has one uniform
	/// source (<see cref="CredentialRepository.MarkTestOutcomeAsync"/> from a job
	/// outcome, never from the controller directly).
	/// </summary>
	private async Task<JobExecutionOutcome> RunDecryptOnlyAsync(
		Guid credentialId, string actor, JobExecutionContext context, CancellationToken cancellationToken)
	{
		try
		{
			using DecryptedSecret decrypted = await _secrets
				.DecryptAsync(credentialId, actor, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);
			await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: true, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Succeeded(
				"Stored secret decrypted successfully. This credential type has no dialable endpoint in this slice; this does not verify connectivity to a target.");
		}
		catch (CredentialSecretNotFoundException)
		{
			await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: false, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed("No secret is stored for this credential.");
		}
		catch (MasterKeyUnavailableException)
		{
			await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: false, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed("The appliance master key is unavailable; the secret could not be decrypted.");
		}
	}

	private async Task<JobExecutionOutcome> ClassifyResultAsync(
		Guid credentialId, string credentialType, PowerShellExecutionResult result, CancellationToken cancellationToken)
	{
		// The executor's own Succeeded only reflects the transport (the invocation
		// itself threw, timed out, or hit a non-zero native exit code) -- a normal,
		// non-throwing call whose function body reported failure (the ordinary shape
		// for a rejected credential/unreachable host) still needs classifying from the
		// module's own returned object, same as ScanJobHandler.TryParseOutput /
		// DownloadJobHandler.TryParseOutput already do for their own modules.
		(bool success, string? failureReason) = TryParseOutput(result.Output);
		bool succeeded = result.Succeeded && success;

		if (succeeded)
		{
			await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: true, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Succeeded(
				credentialType == CredentialTypes.Ssh
					? "TCP connection to the target's SSH port succeeded. This proves reachability, not that the credential authenticates -- SSH auth is verified at scan time."
					: "Connection opened and closed successfully.");
		}

		// jobs.note is a sink too (security.md control 1) -- classify on the raw reason
		// so a redacted password doesn't defeat the auth-failure markers, return only
		// the scrubbed text.
		string rawNote = (result.Succeeded ? failureReason : result.FailureReason)
			?? "credential-test invocation failed with no failure reason.";
		bool isAuthFailure = AuthFailureClassifier.IsAuthFailure(rawNote, [.. _powerShellOptions.Value.AuthFailureMarkers]);
		string note = _redactor.Redact(rawNote);
		await _credentials.MarkTestOutcomeAsync(credentialId, succeeded: false, cancellationToken).ConfigureAwait(false);
		return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
	}

	/// <summary>Parses one of Invoke-WaypointVCenterCredentialTest/Invoke-WaypointNsxCredentialTest/Invoke-WaypointSshCredentialTest's returned [pscustomobject] (Success, FailureReason) -- all three share the same shape.</summary>
	private static (bool Success, string? FailureReason) TryParseOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return (false, null);
		}

		// Issue #976: routed through the PowerShellValueUnwrap chokepoint for uniformity
		// -- top-level pipeline-output properties, already unwrapped by
		// PowerShellExecutor.Unwrap, so never actually exposed to #972's nested-property
		// hazard, but Unwrap/UnwrapAs are idempotent on an already-unwrapped value.
		bool success = PowerShellValueUnwrap.Unwrap(psObject.Properties["Success"]?.Value) is true;
		string? failureReason = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["FailureReason"]?.Value);
		return (success, failureReason);
	}

	private static string? TryGetConnectionHost(string connectionJson)
	{
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(connectionJson);
		return document.RootElement.TryGetProperty("host", out System.Text.Json.JsonElement hostElement)
			&& hostElement.ValueKind == System.Text.Json.JsonValueKind.String
				? hostElement.GetString()
				: null;
	}

	/// <summary>Same run-initiator-or-"system" attribution DiscoverJobHandler uses.</summary>
	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}
}
