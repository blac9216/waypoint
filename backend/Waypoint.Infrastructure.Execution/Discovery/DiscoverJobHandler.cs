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
using Waypoint.Core.Components;
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
	private readonly IComponentRepository _components;
	private readonly IJobRunnerRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;

	public DiscoverJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		TargetRepository targets,
		InventoryRepository inventory,
		IComponentRepository components,
		IJobRunnerRepository jobs,
		ISecretRedactor redactor,
		IOptions<PowerShellOptions> powerShellOptions)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(inventory);
		ArgumentNullException.ThrowIfNull(components);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_inventory = inventory;
		_components = components;
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

		// Issue #585 (epic #582): the job's own immutable snapshot (job_credential_bindings,
		// migration 0044 -- discovery's one required purpose is vsphere-api, ADR-0021 §3)
		// is the preferred credential source, so a target edit after enqueue can no
		// longer change what an in-flight discover job authenticates with. A job with no
		// snapshot rows (fanned out before 0044, or enqueued by a legacy path) keeps the
		// pre-#585 live read of the target's assigned credential.
		Guid? discoverCredentialId = null;
		IReadOnlyList<JobCredentialBinding> bindings = await _jobs
			.GetJobCredentialBindingsAsync(context.Job.Id, cancellationToken).ConfigureAwait(false);
		JobCredentialBinding? snapshotBinding = bindings
			.FirstOrDefault(b => string.Equals(b.Purpose, Waypoint.Core.Secrets.CredentialPurposes.VSphereApi, StringComparison.Ordinal));
		if (snapshotBinding is not null)
		{
			if (snapshotBinding.CredentialId is null)
			{
				return JobExecutionOutcome.Failed(
					$"job '{context.Job.Id}' snapshot for purpose '{Waypoint.Core.Secrets.CredentialPurposes.VSphereApi}' no longer names a credential (deleted after this job reached a terminal state).");
			}

			discoverCredentialId = snapshotBinding.CredentialId;
		}
		else
		{
			discoverCredentialId = target.CredentialId;
		}

		if (discoverCredentialId is null)
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' has no credential assigned.");
		}

		Waypoint.Core.Secrets.CredentialResponse? credential = await _credentials
			.GetAsync(discoverCredentialId.Value, cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			return JobExecutionOutcome.Failed($"target '{targetId}' references credential '{discoverCredentialId}', which no longer exists.");
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
				.DecryptAsync(discoverCredentialId.Value, actor, context.Job.Id, context.Job.RunId, cancellationToken)
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

		DiscoveryParseOutcome parseOutcome = ParseDiscoveredItems(result.Output);
		if (parseOutcome.MalformedCount > 0)
		{
			// Issue #618: a row the module emitted but this handler could not parse
			// (missing/empty Type-MoRef-Name, or not even a PSObject -- e.g. the
			// executor's output-capture path losing a pscustomobject's NoteProperties)
			// must never read as a quiet "0 items" success. A target that legitimately
			// has zero inventory still succeeds below (MalformedCount is 0 for a
			// genuinely empty raw output); only rows that WERE returned but could not be
			// understood fail the job, with a note that names the shape mismatch instead
			// of the redacted-credential text a connection failure would produce.
			await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Failed, stampLastRefreshed: false, cancellationToken)
				.ConfigureAwait(false);
			return JobExecutionOutcome.Failed(
				$"discover invoked successfully but {parseOutcome.MalformedCount} of {result.Output.Count} returned row(s) could not be parsed " +
				"(missing Type/MoRef/Name, or not a structured object) -- treating this as a failure rather than a silent zero-item success. " +
				"This usually means the PowerShell output-capture path lost the module's object properties; check the compliance-runner logs.");
		}

		IReadOnlyList<DiscoveredInventoryItem> items = parseOutcome.Items;
		InventoryUpsertOutcome outcome = await _inventory.UpsertDiscoveryResultsAsync(targetId, items, cancellationToken).ConfigureAwait(false);

		// Issue #732 (discovery-wiring remainder): this point in the method is reached
		// ONLY for a successful, fully-parsed enumeration -- every earlier return above
		// (connection/auth failure, malformed row, PowerShell invocation failure) already
		// bails out before this line, so a partial or failed discovery boundary can never
		// reach ComponentUpsertOutcome/mass-absent components (ADR-0023 "a failed or
		// partial boundary ... neither claims completeness nor advances absence" --
		// enforced here by construction, not by a separate flag: the same fail-closed gate
		// InventoryRepository's own removal detection already relies on for `items`, one
		// call above this one). An empty-but-successful enumeration (a target that
		// genuinely has zero inventory) still reaches here and correctly marks every
		// previously-active component absent, matching InventoryUpsertOutcome's own
		// documented behavior for the identical case.
		IReadOnlyList<DiscoveredComponent> componentItems = MapToComponents(items);
		ComponentUpsertOutcome componentOutcome = await _components
			.UpsertDiscoveredAsync(targetId, componentItems, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new
		{
			upserted = outcome.Upserted,
			removed = outcome.Removed,
			components_upserted = componentOutcome.Upserted,
			components_absent = componentOutcome.MarkedAbsent,
			components_reconnected = componentOutcome.Reconnected,
		});
		await context.Events
			.EmitAsync(JobEventTypes.DiscoverProgress, context.Job.Id, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Discovered, stampLastRefreshed: true, cancellationToken)
			.ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded(
			$"Discovered {outcome.Upserted} item(s), marked {outcome.Removed} removed. " +
			$"Components: {componentOutcome.Upserted} upserted, {componentOutcome.Reconnected} reconnected, {componentOutcome.MarkedAbsent} marked absent.");
	}

	/// <summary>
	/// Maps this pass's flat cluster/host/VM inventory result onto
	/// <see cref="Waypoint.Core.Components.IComponentRepository.UpsertDiscoveredAsync"/>'s
	/// stable-identity input shape (issue #732's remainder: "wire discovery to
	/// component materialization ... for real targets"). Only `esxi`/`vm` inventory
	/// rows become components -- `cluster` is a vSphere grouping construct with no
	/// catalog-declared component analogue (<see cref="Waypoint.Core.ComplianceContent.CatalogSelectorKinds"/>
	/// has no "cluster" selector kind) and is never itself an executable compliance
	/// subject, so it is deliberately dropped rather than materialized.
	///
	/// Every mapped component hangs directly off one synthetic per-target `vcenter`
	/// root component (<see cref="Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter"/>,
	/// <c>VendorIdentity: null</c> -- this discovery boundary's PowerShell module
	/// (<c>Invoke-WaypointDiscovery</c>) enumerates only cluster/host/VM objects, never
	/// a distinct vCenter ServiceInstance MoRef, so there is no independent upstream
	/// object to key the root on; identity instead falls to the no-vendor-identity
	/// partial-index case migration 0054 already defines for exactly this shape),
	/// never through an intermediate cluster component -- a host's or VM's
	/// <see cref="DiscoveredInventoryItem.ParentMoref"/> may point at a cluster
	/// (dropped, no component) rather than another host, so component parentage is
	/// deliberately flattened to (root vcenter) -&gt; (esxi | vm) rather than trying to
	/// preserve the cluster tier as a component tier that does not exist in the catalog
	/// vocabulary.
	///
	/// Non-inventory catalog-defined expansion (named VCSA services, NSX functional
	/// components, whole-appliance SSH component sets -- ADR-0023's "component
	/// families with no independent upstream object") is explicitly NOT built in this
	/// slice: it requires resolving this target's active catalog product/version to
	/// read the catalog's declared expected-component set, which no discovery-job code
	/// path does today (<see cref="Waypoint.Core.Components.ComponentCapabilityMatcher"/>
	/// consumes a resolved fact, it does not supply one). Tracked as a deferred finding
	/// in this PR's own report rather than guessed at here.
	/// </summary>
	internal static IReadOnlyList<DiscoveredComponent> MapToComponents(IReadOnlyList<DiscoveredInventoryItem> items)
	{
		List<DiscoveredComponent> components = [];

		// The synthetic root has no independent vendor identity (see doc comment above)
		// and therefore no ParentVendorIdentity of its own -- it is always a top-level
		// component under the target.
		components.Add(new DiscoveredComponent(
			CatalogComponentKey: Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter,
			VendorIdentity: null,
			DisplayName: "vCenter Server",
			ParentVendorIdentity: null,
			CatalogComponentId: null,
			ExactVersion: null));

		foreach (DiscoveredInventoryItem item in items)
		{
			string? catalogComponentKey = item.Type switch
			{
				InventoryItemTypes.Host => Waypoint.Core.ComplianceContent.CatalogSelectorKinds.Esxi,
				InventoryItemTypes.Vm => Waypoint.Core.ComplianceContent.CatalogSelectorKinds.Vm,
				_ => null, // InventoryItemTypes.Cluster and anything else: no component analogue.
			};

			if (catalogComponentKey is null)
			{
				continue;
			}

			components.Add(new DiscoveredComponent(
				CatalogComponentKey: catalogComponentKey,
				VendorIdentity: item.Moref,
				DisplayName: item.Name,
				// Flattened parentage (see doc comment): every esxi/vm component's parent
				// is the synthetic root, never the (possibly cluster, possibly another
				// host) inventory parent -- both this root and every esxi/vm row have
				// ParentVendorIdentity null, so both land as top-level (ParentComponentId
				// null) components under the target, matching the root's own top-level
				// position. Build is captured as the component's discovered fact only for
				// a host -- a VM's Build carries VMware Tools version in this module's
				// output, which is not a product-version fact for the VM itself, so it is
				// intentionally NOT passed as ExactVersion here (that would misrepresent a
				// guest property as the component's own compliance-relevant version).
				ParentVendorIdentity: null,
				CatalogComponentId: null,
				ExactVersion: item.Type == InventoryItemTypes.Host ? item.Build : null));
		}

		return components;
	}

	/// <summary>
	/// Parses every row the module returned. Issue #618: a row that came back is
	/// either a valid item or a MALFORMED one -- there is no longer a silent third
	/// option where an unparseable row just vanishes from the count. A raw output of
	/// zero rows (a target the vCenter genuinely has no inventory for) still yields
	/// zero malformed rows and succeeds; a raw output where even one row fails to
	/// parse is surfaced to the caller so it can fail the job instead of reporting a
	/// clean "0 items" success over data that was actually lost or malformed.
	/// </summary>
	private static DiscoveryParseOutcome ParseDiscoveredItems(IReadOnlyList<object?> output)
	{
		List<DiscoveredInventoryItem> items = [];
		int malformedCount = 0;
		foreach (object? item in output)
		{
			DiscoveredInventoryItem? parsed = TryParseItem(item);
			if (parsed is not null)
			{
				items.Add(parsed);
			}
			else
			{
				malformedCount++;
			}
		}

		return new DiscoveryParseOutcome(items, malformedCount);
	}

	private sealed record DiscoveryParseOutcome(IReadOnlyList<DiscoveredInventoryItem> Items, int MalformedCount);

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
			// Issue #618: unlike CatalogIndexJobHandler.TryParseArtifact's "one bad row
			// does not halt the batch" tolerance, a malformed discovery row is NOT
			// silently dropped here -- ParseDiscoveredItems counts it and the caller
			// fails the whole job rather than reporting a clean "Discovered 0 item(s)"
			// success over data that came back but could not be understood (the exact
			// silent-success shape the live vCenter reproduction exposed: the executor's
			// output-capture path returned 535 rows with their NoteProperties stripped,
			// every row failed this check, and the job still reported success).
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
