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
using Waypoint.Core.Discovery;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Discovery;

/// <summary>
/// The <c>discover</c> <see cref="Waypoint.Core.Jobs.JobShape.Simple"/> job handler
/// (issue #21, epic #13): connects to a vSphere target's vCenter via the runspace
/// host using the target's referenced credential (decrypt-under-identity, redacted,
/// never argv/logs/events -- the same #8/#194 canary machinery
/// <see cref="Waypoint.Infrastructure.Catalog.CatalogIndexJobHandler"/> established),
/// enumerates clusters/hosts/VMs, and upserts them into <c>inventory_items</c>
/// (migration 0011) via <see cref="InventoryRepository"/>.
///
/// Payload contract (JSON object, set by <c>DiscoveryController.Discover</c>):
/// <c>{"target_id": "&lt;uuid&gt;"}</c>.
///
/// Username: migration 0012 (issue #262) added a dedicated
/// <see cref="Waypoint.Core.Secrets.CredentialResponse.Username"/> column, replacing
/// the earlier stopgap that overloaded <c>Name</c> (a human-facing label, e.g. "Prod
/// vCenter admin") as the vSphere SSO login. A vCenter credential with no username
/// set fails fast here rather than silently falling back to <c>Name</c> -- that
/// fallback is exactly the conflation this issue exists to undo, and would turn a
/// credential rename into a silent SSO-login change.
/// </summary>
public sealed class DiscoverJobHandler : IJobHandler
{
	private const string InvocationCommand = "Invoke-WaypointDiscovery";

	private readonly IPowerShellExecutor _executor;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly TargetRepository _targets;
	private readonly InventoryRepository _inventory;
	private readonly IJobQueueRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;

	public DiscoverJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		TargetRepository targets,
		InventoryRepository inventory,
		IJobQueueRepository jobs,
		ISecretRedactor redactor,
		IOptions<PowerShellOptions> powerShellOptions)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(inventory);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_inventory = inventory;
		_jobs = jobs;
		_redactor = redactor;
		_powerShellOptions = powerShellOptions;
	}

	public string JobType => "discover";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		Guid targetId;
		try
		{
			targetId = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"discover payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"discover payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' does not exist.");
		}

		if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal))
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' is kind '{target.Kind}'; discover only supports '{TargetKinds.VSphere}'.");
		}

		if (target.CredentialId is null)
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' has no credential assigned.");
		}

		Waypoint.Core.Secrets.CredentialResponse? credential = await _credentials
			.GetAsync(target.CredentialId.Value, cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' references credential '{target.CredentialId}', which no longer exists.");
		}

		string? host = TryGetConnectionHost(target.ConnectionJson);
		if (string.IsNullOrWhiteSpace(host))
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' has no 'connection.host' to discover.");
		}

		if (string.IsNullOrWhiteSpace(credential.Username))
		{
			// Issue #262: no more falling back to Name as the SSO login -- a vCenter
			// credential without a dedicated username is a configuration error the
			// operator must fix (set one via PUT /credentials/{id}), not something
			// this handler should paper over with the display label.
			return JobExecutionOutcome.Failed(
				$"target '{targetId}' references credential '{credential.Id}', which has no username set; set one before discovering this target.");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);

		await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Discovering, stampLastRefreshed: false, cancellationToken)
			.ConfigureAwait(false);

		PowerShellExecutionResult result;
		DecryptedSecret? decrypted = null;
		try
		{
			// security.md control 4 / #8's fail-closed decrypt audit: this call writes
			// the secret.decrypted audit row (credential, job, run, actor, timestamp) in
			// the same transaction as the ciphertext read, before any plaintext reaches
			// this method -- attribution is durable even if this handler crashes on the
			// next line.
			decrypted = await _secrets
				.DecryptAsync(target.CredentialId.Value, actor, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);

			Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
			{
				["VCenter"] = host,
				["Username"] = credential.Username,
				["Password"] = decrypted.Value,
			};

			PowerShellRequest request = new(InvocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
			result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Failed, stampLastRefreshed: false, cancellationToken)
				.ConfigureAwait(false);
			return JobExecutionOutcome.Failed($"target credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Failed, stampLastRefreshed: false, cancellationToken)
				.ConfigureAwait(false);
			return JobExecutionOutcome.Failed($"target credential could not be decrypted: {exception.Message}");
		}
		finally
		{
			// Ends the in-play redaction window as soon as the invocation is done -- the
			// value must not still be "in play" once this method has returned.
			decrypted?.Dispose();
		}

		if (!result.Succeeded)
		{
			// jobs.note is a sink too (security.md control 1) -- classify on the raw
			// reason (so a redacted password doesn't defeat the auth-failure markers)
			// but return only the scrubbed text.
			string rawNote = result.FailureReason ?? "discover invocation failed with no failure reason.";
			bool isAuthFailure = AuthFailureClassifier.IsAuthFailure(rawNote, [.. _powerShellOptions.Value.AuthFailureMarkers]);
			string note = _redactor.Redact(rawNote);
			await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Failed, stampLastRefreshed: false, cancellationToken)
				.ConfigureAwait(false);
			return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
		}

		IReadOnlyList<DiscoveredInventoryItem> items = ParseDiscoveredItems(result.Output);
		InventoryUpsertOutcome outcome = await _inventory.UpsertDiscoveryResultsAsync(targetId, items, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new { upserted = outcome.Upserted, removed = outcome.Removed });
		await context.Events
			.EmitAsync(JobEventTypes.DiscoverProgress, context.Job.Id, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Discovered, stampLastRefreshed: true, cancellationToken)
			.ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"Discovered {outcome.Upserted} item(s), marked {outcome.Removed} removed.");
	}

	private static List<DiscoveredInventoryItem> ParseDiscoveredItems(IReadOnlyList<object?> output)
	{
		List<DiscoveredInventoryItem> items = [];
		foreach (object? item in output)
		{
			DiscoveredInventoryItem? parsed = TryParseItem(item);
			if (parsed is not null)
			{
				items.Add(parsed);
			}
		}

		return items;
	}

	private static DiscoveredInventoryItem? TryParseItem(object? item)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? type = GetProperty<string>(psObject, "Type");
		string? moRef = GetProperty<string>(psObject, "MoRef");
		string? name = GetProperty<string>(psObject, "Name");
		if (!InventoryItemTypes.IsValid(type) || string.IsNullOrWhiteSpace(moRef) || string.IsNullOrWhiteSpace(name))
		{
			// One malformed row must not fail the whole discovery pass -- same
			// "individual failures don't halt the batch" principle
			// CatalogIndexJobHandler.TryParseArtifact applies.
			return null;
		}

		string? parentMoRef = GetProperty<string>(psObject, "ParentMoRef");
		string? build = GetProperty<string>(psObject, "Build");
		bool? maintenanceMode = psObject.Properties["MaintenanceMode"]?.Value as bool?;

		return new DiscoveredInventoryItem(type!, moRef, name, parentMoRef, build, maintenanceMode);
	}

	private static T? GetProperty<T>(System.Management.Automation.PSObject psObject, string name)
		where T : class
	{
		return psObject.Properties[name]?.Value as T;
	}

	private static string? TryGetConnectionHost(string connectionJson)
	{
		using JsonDocument document = JsonDocument.Parse(connectionJson);
		return document.RootElement.TryGetProperty("host", out JsonElement hostElement) && hostElement.ValueKind == JsonValueKind.String
			? hostElement.GetString()
			: null;
	}

	private static Guid ParsePayload(string payloadJson)
	{
		using JsonDocument document = JsonDocument.Parse(payloadJson);
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("target_id", out JsonElement targetIdElement) || !Guid.TryParse(targetIdElement.GetString(), out Guid targetId))
		{
			throw new ArgumentException("payload requires a GUID string 'target_id' property");
		}

		return targetId;
	}

	/// <summary>
	/// Attribution for the decrypt audit row (security.md control 4): the run's
	/// initiator when recorded (issue #208's derive-from-identity pattern), falling
	/// back to a fixed system marker for a scheduled/unattributed run so the audit row
	/// is never written with a null/empty actor.
	/// </summary>
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
