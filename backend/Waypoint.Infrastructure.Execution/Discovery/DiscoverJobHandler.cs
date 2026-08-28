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

using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
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
	private readonly ICatalogRepository _catalog;
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
		ICatalogRepository catalog,
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
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_inventory = inventory;
		_components = components;
		_catalog = catalog;
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

		// Issue #865: pull the trailing discovery-meta completeness marker (if any) off
		// the raw output BEFORE parsing inventory rows -- it is the module's own signal,
		// never an inventory item, and must not be counted as a malformed row nor mapped
		// into a component.
		CompletenessMarker completeness = ExtractCompletenessMarker(result.Output, out IReadOnlyList<object?> itemOutput);

		DiscoveryParseOutcome parseOutcome = ParseDiscoveredItems(itemOutput);
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
				$"discover invoked successfully but {parseOutcome.MalformedCount} of {itemOutput.Count} returned row(s) could not be parsed " +
				"(missing Type/MoRef/Name, or not a structured object) -- treating this as a failure rather than a silent zero-item success. " +
				"This usually means the PowerShell output-capture path lost the module's object properties; check the compliance-runner logs.");
		}

		// Issue #865 (ADR-0023 "a failed or partial boundary ... neither claims
		// completeness nor advances absence"): a non-terminating per-subtree failure
		// (unreachable ESXi, permission-denied cluster) reports Succeeded == true with
		// only the objects it COULD enumerate -- completeness.IsComplete is false for
		// exactly that case (see the module's discovery-meta marker doc comment).
		// advanceAbsence gates ONLY the mark-removed/mark-absent block in each
		// repository; the items this pass DID see are still upserted/un-removed below
		// as an unverified-cache refresh, never silently dropped.
		bool advanceAbsence = completeness.IsComplete;

		IReadOnlyList<DiscoveredInventoryItem> items = parseOutcome.Items;
		InventoryUpsertOutcome outcome = await _inventory
			.UpsertDiscoveryResultsAsync(targetId, items, cancellationToken, advanceAbsence).ConfigureAwait(false);

		// Issue #732 (discovery-wiring remainder): this point in the method is reached
		// ONLY for a successful, fully-parsed enumeration -- every earlier return above
		// (connection/auth failure, malformed row, PowerShell invocation failure) already
		// bails out before this line. A partial pass (advanceAbsence == false) still
		// upserts the components it DID see, but neither repository call above/below
		// advances absence for anything it didn't.
		IReadOnlyList<DiscoveredComponent> componentItems = MapToComponents(items);

		// Issue #985: the linkage pass MapToComponents' own doc comment said no
		// discovery-job code path performed -- resolves each mapped component's
		// CatalogComponentId from its (catalog_component_key, exact_version) fact
		// against the catalog, re-evaluated every discovery pass so a version change
		// re-links (or honestly unlinks) rather than keeping a stale id (see
		// ResolveCatalogLinkageAsync's own doc comment for the exact-match/ambiguity
		// rules -- ADR-0022 never guesses).
		(IReadOnlyList<DiscoveredComponent> linkedComponentItems, IReadOnlyList<string> linkageWarnings) =
			await ResolveCatalogLinkageAsync(_catalog, componentItems, cancellationToken).ConfigureAwait(false);

		ComponentUpsertOutcome componentOutcome = await _components
			.UpsertDiscoveredAsync(targetId, linkedComponentItems, cancellationToken, advanceAbsence).ConfigureAwait(false);

		if (linkageWarnings.Count > 0)
		{
			// Same job.log severity idiom issue #865 uses for a partial-enumeration
			// warning (PowerShellExecutor.Emit). Issue #995 widened this from
			// "ambiguous match only" to "any per-component linkage condition worth an
			// operator's attention" (ambiguous match, or an unexpected repository fault
			// caught per-item below) -- neither is a job failure (this component simply
			// stays unlinked), but both ARE actionable/loud, never a silent null in the
			// components table.
			string warningSummary = string.Join("; ", linkageWarnings);
			string warningPayload = JsonSerializer.Serialize(new { severity = "warning", line = $"discover: catalog linkage warning(s) for one or more components -- left unlinked. {warningSummary}" });
			await context.Events
				.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, warningPayload, cancellationToken)
				.ConfigureAwait(false);
		}

		// Issue #741: catalog-declared service expansion, the step MapToComponents' doc
		// comment previously deferred ("non-inventory catalog-defined expansion ... is
		// explicitly NOT built in this slice"). Runs AFTER the component upsert so it
		// reads the root's POST-pass linkage state -- which #1000's CASE preservation
		// keeps from a configured-fact PUT even though discovery itself never supplies
		// a vCenter version. Derived from catalog + linkage facts, not from this pass's
		// enumeration, so it runs for partial boundaries too (advanceAbsence has no
		// bearing: an unlinked root marking declared children absent is a fact-based
		// reconciliation, not an incomplete-enumeration inference).
		CatalogDeclaredChildSyncOutcome declaredOutcome;
		try
		{
			declaredOutcome = await SyncCatalogDeclaredServiceChildrenAsync(targetId, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Fail-soft (same shape as #995's per-component linkage fault handling): the
			// enumeration itself already succeeded and was persisted above; a failed
			// declared-service expansion is loud (warning job.log) but never converts a
			// successful discovery into a failure -- it self-heals on the next pass or
			// the next configured-fact write.
			declaredOutcome = new CatalogDeclaredChildSyncOutcome(0, 0, 0);
			string declaredWarning = JsonSerializer.Serialize(new
			{
				severity = "warning",
				line = $"discover: catalog-declared service expansion failed ({exception.GetType().Name}: {exception.Message}) -- declared VCSA service components were not reconciled this pass.",
			});
			await context.Events
				.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, declaredWarning, cancellationToken)
				.ConfigureAwait(false);
		}

		string progressPayload = JsonSerializer.Serialize(new
		{
			upserted = outcome.Upserted,
			removed = outcome.Removed,
			components_upserted = componentOutcome.Upserted,
			components_absent = componentOutcome.MarkedAbsent,
			components_reconnected = componentOutcome.Reconnected,
			declared_services_upserted = declaredOutcome.Upserted,
			declared_services_reconnected = declaredOutcome.Reconnected,
			declared_services_absent = declaredOutcome.MarkedAbsent,
			complete = completeness.IsComplete,
			enumeration_errors = completeness.Errors,
		});
		await context.Events
			.EmitAsync(JobEventTypes.DiscoverProgress, context.Job.Id, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		if (!completeness.IsComplete)
		{
			// Issue #865's alert path: reuse the same job.log severity idiom every other
			// PowerShell stream record uses (PowerShellExecutor.Emit) rather than
			// inventing a new alert channel -- a "warning" job.log line naming exactly
			// which subtrees failed, readable from the same live log view/job history an
			// operator already checks for a target.
			string errorSummary = string.Join("; ", completeness.Errors);
			string warningPayload = JsonSerializer.Serialize(new { severity = "warning", line = $"discover: partial vSphere enumeration -- absence NOT advanced this pass. {errorSummary}" });
			await context.Events
				.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, warningPayload, cancellationToken)
				.ConfigureAwait(false);
		}

		await _targets.SetDiscoveryStatusAsync(targetId, TargetDiscoveryStatuses.Discovered, stampLastRefreshed: true, cancellationToken)
			.ConfigureAwait(false);

		string completenessNote = completeness.IsComplete
			? string.Empty
			: $" PARTIAL enumeration ({completeness.Errors.Count} subtree error(s)) -- absence NOT advanced this pass, earlier rows retained as unverified cache (ADR-0023).";
		return JobExecutionOutcome.Succeeded(
			$"Discovered {outcome.Upserted} item(s), marked {outcome.Removed} removed. " +
			$"Components: {componentOutcome.Upserted} upserted, {componentOutcome.Reconnected} reconnected, {componentOutcome.MarkedAbsent} marked absent.{completenessNote}");
	}

	/// <summary>
	/// Whether the discovery boundary the module ran was complete. <see cref="IsComplete"/>
	/// defaults true for a legacy/stub module that never emits a <c>discovery-meta</c>
	/// row at all (issue #865 ships behind the existing <c>succeeded</c> gate, not a
	/// breaking output-shape requirement) -- only an explicit
	/// <c>Complete = $false</c> marker gates absence off.
	/// </summary>
	private sealed record CompletenessMarker(bool IsComplete, IReadOnlyList<string> Errors)
	{
		public static readonly CompletenessMarker AssumedComplete = new(true, []);
	}

	/// <summary>
	/// Pulls the module's trailing <c>Type = 'discovery-meta'</c> record (issue #865) off
	/// the end of the raw output, if present, and returns the remaining rows via
	/// <paramref name="itemOutput"/> for <see cref="ParseDiscoveredItems"/>. A module that
	/// predates this marker (e.g. an older stub) simply never emits one --
	/// <see cref="CompletenessMarker.AssumedComplete"/> preserves today's behavior for it.
	/// </summary>
	private static CompletenessMarker ExtractCompletenessMarker(IReadOnlyList<object?> output, out IReadOnlyList<object?> itemOutput)
	{
		if (output.Count == 0 || output[^1] is not System.Management.Automation.PSObject last ||
			GetProperty<string>(last, "Type") != "discovery-meta")
		{
			itemOutput = output;
			return CompletenessMarker.AssumedComplete;
		}

		itemOutput = output.Take(output.Count - 1).ToList();

		bool isComplete = PowerShellValueUnwrap.UnwrapAsStruct<bool>(last.Properties["Complete"]?.Value) ?? true;
		List<string> errors = [];
		foreach (object? entry in PowerShellValueUnwrap.UnwrapEach(last.Properties["Errors"]?.Value))
		{
			if (entry is not null)
			{
				errors.Add(entry.ToString() ?? string.Empty);
			}
		}

		return new CompletenessMarker(isComplete, errors);
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
	/// Non-inventory catalog-defined expansion of the ssh/service family (named VCSA
	/// services -- ADR-0023's "component families with no independent upstream object")
	/// is issue #741's <see cref="SyncCatalogDeclaredServiceChildrenAsync"/>, which runs
	/// AFTER the component upsert (it needs the root's post-pass catalog linkage), not
	/// here in the enumeration mapping -- these components are never enumerated, they
	/// are declared by the catalog release the root resolved to. NSX functional
	/// components and whole-appliance SSH component sets remain future work on their own
	/// target kinds (#742/#743).
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
				// position. Issue #974 (owner decision on #967's options analysis, "Option
				// A"): a host's ExactVersion is its semantic vSphere product version
				// (item.Version, e.g. "8.0.3"), NOT its raw Build number -- Build never
				// equals the catalog's semantic VersionKey byte-for-byte (ADR-0022), so
				// using it as ExactVersion could never match and made every discovered
				// ESXi component permanently incompatible. Build is still captured/stored
				// in inventory_items exactly as before (see InventoryRepository), the
				// owner wants it retained for later use -- it is simply no longer what a
				// host's catalog match is keyed on. A host whose Version is unavailable
				// this pass gets ExactVersion=null -- fail-closed, per ADR-0023: never
				// substitute or infer a version from Build.
				// A VM's Build carries VMware Tools version in this module's output, which
				// is not a product-version fact for the VM itself, so it is intentionally
				// NOT passed as ExactVersion here (that would misrepresent a guest property
				// as the component's own compliance-relevant version); VMs have no
				// analogous Version field either, so they keep ExactVersion=null.
				// Issue #995: a powered-off/disconnected/connecting host reports Version as
				// an EMPTY STRING, not null -- string.IsNullOrWhiteSpace normalizes that (and
				// whitespace-only) to null right here, at the mapping boundary, so "version
				// unavailable this pass" has exactly one representation (ExactVersion=null)
				// for every downstream consumer, never a "" that looks falsy to a human but
				// is non-null to an `is null` guard.
				ParentVendorIdentity: null,
				CatalogComponentId: null,
				ExactVersion: item.Type == InventoryItemTypes.Host && !string.IsNullOrWhiteSpace(item.Version) ? item.Version : null));
		}

		return components;
	}

	/// <summary>
	/// Issue #741: materializes the catalog release's declared VCSA service set as
	/// inventory child components beneath this target's root connection component --
	/// "the catalog release determines the exact VCSA component list" (issue #741 AC),
	/// never a hard-coded service list. The root (the synthetic no-vendor-identity,
	/// no-parent <c>vcenter</c> component) anchors the expansion because the declared
	/// services are OS-level services of the appliance the target's connection reaches;
	/// discovered objects with their own vendor identity (ESXi hosts, VMs) are never
	/// expansion anchors. Root unlinked (no version fact yet, cleared, conflicted, or
	/// ambiguous): the declared set is empty and any previously-derived children are
	/// marked absent -- honest lifecycle, never a silent stale service list. Fails soft
	/// on a repository fault the same way per-component linkage does (#995): discovery
	/// itself already succeeded; a failed expansion pass surfaces as a warning-level
	/// job.log line and re-heals on the next pass or configured-fact write.
	/// </summary>
	private async Task<CatalogDeclaredChildSyncOutcome> SyncCatalogDeclaredServiceChildrenAsync(
		Guid targetId, CancellationToken cancellationToken)
	{
		IReadOnlyList<Component> current = await _components
			.ListForTargetAsync(targetId, includeRetired: true, cancellationToken).ConfigureAwait(false);
		Component? root = current.FirstOrDefault(c => c.ParentComponentId is null && c.VendorIdentity is null);
		if (root is null)
		{
			return new CatalogDeclaredChildSyncOutcome(0, 0, 0);
		}

		IReadOnlyList<CatalogDeclaredChild> declared = [];
		if (root.CatalogComponentId is { } linkedCatalogComponentId)
		{
			CatalogComponent? linkedComponent = await _catalog
				.GetComponentAsync(linkedCatalogComponentId, cancellationToken).ConfigureAwait(false);
			if (linkedComponent is not null)
			{
				IReadOnlyList<CatalogComponent> versionComponents = await _catalog
					.ListComponentsAsync(linkedComponent.ProductVersionId, cancellationToken).ConfigureAwait(false);
				declared = CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren(versionComponents, linkedCatalogComponentId);
			}
		}

		return await _components
			.SyncCatalogDeclaredChildrenAsync(targetId, root.Id, declared, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #985: the missing linkage step <see cref="MapToComponents"/>'s own doc
	/// comment named as absent. Resolves each mapped component's
	/// <see cref="DiscoveredComponent.CatalogComponentId"/> from the fact
	/// <see cref="MapToComponents"/> already computed -- <see cref="DiscoveredComponent.CatalogComponentKey"/>
	/// plus <see cref="DiscoveredComponent.ExactVersion"/> -- against
	/// <see cref="ICatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync"/>.
	///
	/// Design (stated for the reviewer, docs read before writing this):
	/// <list type="bullet">
	/// <item>Runs HERE, at discovery-map time, immediately before the upsert -- not as a
	/// separate post-upsert pass and not deferred into <see cref="Waypoint.Core.Components.ComponentCapabilityMatcher"/>.
	/// The matcher's own doc comment states it is "intentionally domain logic with no
	/// I/O" and stays trivially unit-testable without a database; adding a catalog
	/// lookup there would break that invariant. A separate post-upsert pass would mean
	/// a discovery boundary that fails between upsert and linkage leaves components
	/// durably unlinked with no self-healing signal -- doing it inline keeps "discover
	/// a fact" and "link it" one atomic unit of work from the caller's perspective, and
	/// matches how <see cref="Waypoint.Core.Discovery.DiscoveredInventoryItem"/>'s own
	/// facts (Build/Version) are resolved in the same mapping pass.</item>
	/// <item>Exact match only (ADR-0022 "no ranges, no nearest-version fallback"): a
	/// component with no <see cref="DiscoveredComponent.ExactVersion"/> (unavailable
	/// this pass) is never looked up at all -- it stays unlinked with no ambiguity
	/// entry, the same fail-closed shape one layer up in
	/// <see cref="Waypoint.Core.Components.ComponentCapabilityMatcher"/> ("no configured
	/// or discovered exact product version").</item>
	/// <item>No activated-baseline requirement: <see cref="Waypoint.Infrastructure.Runs.ScopeResolutionService"/>
	/// (this linkage's only real consumer today) reads <c>catalog_component_id</c>
	/// straight into <see cref="ICatalogRepository.ListExecutionProfilesByComponentAsync"/>
	/// with no baseline-activation gate anywhere in that path -- the execution-profile
	/// -presence check the matcher already performs ("content may not yet be staged or
	/// activated") is the documented downstream gate for "seeded but not yet
	/// activated" catalog rows, not this linkage. This linkage only needs a seeded
	/// <c>catalog_components</c> row to exist; it does not check
	/// <c>catalog_execution_profiles</c>/baseline state at all. Flagged for the
	/// reviewer as the one place the domain docs (ADR-0022/ADR-0023, domain-model.md)
	/// do not explicitly spell out "linkage requires X" -- this choice follows the
	/// existing consumer rather than widening or narrowing it independently.</item>
	/// <item>Ambiguous (more than one catalog component sharing the same component_key
	/// + exact version across different products -- structurally possible since
	/// discovery supplies no product) fails closed: stays unlinked
	/// (<c>CatalogComponentId: null</c>) with an honest per-component reason surfaced
	/// via <see cref="JobEventTypes.JobLog"/> (ADR-0022 "never guesses a winner"),
	/// never an arbitrary "first match wins."</item>
	/// </list>
	/// </summary>
	internal static async Task<(IReadOnlyList<DiscoveredComponent> Items, IReadOnlyList<string> Warnings)> ResolveCatalogLinkageAsync(
		ICatalogRepository catalog, IReadOnlyList<DiscoveredComponent> items, CancellationToken cancellationToken)
	{
		List<DiscoveredComponent> resolved = [];
		List<string> warnings = [];

		foreach (DiscoveredComponent item in items)
		{
			// Issue #1000: the exact-match/ambiguity/fail-closed rule itself now lives in
			// the shared Waypoint.Core.Components.CatalogLinkageResolver -- extracted
			// verbatim from this loop -- so the configured-fact path
			// (ComponentRepository.SetConfiguredFactAsync) resolves linkage the identical
			// way rather than forking its own copy. Guarding on IsNullOrWhiteSpace (issue
			// #995) still happens inside the shared resolver.
			(Guid? catalogComponentId, string? warning) = await CatalogLinkageResolver
				.ResolveAsync(catalog, item.CatalogComponentKey, item.ExactVersion, cancellationToken)
				.ConfigureAwait(false);

			resolved.Add(item with { CatalogComponentId = catalogComponentId });
			if (warning is not null)
			{
				warnings.Add(warning);
			}
		}

		return (resolved, warnings);
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
		// Issue #976: this is a value type, so it cannot go through the class-constrained
		// GetProperty<T>/UnwrapAs<T> above -- UnwrapAsStruct<T> is the same chokepoint's
		// value-type sibling, with the identical silent-null-on-absent-or-mismatch
		// degradation convention.
		bool? maintenanceMode = PowerShellValueUnwrap.UnwrapAsStruct<bool>(psObject.Properties["MaintenanceMode"]?.Value);

		// Issue #974: the host's semantic vSphere product version, alongside (never
		// instead of) Build -- see this type's own DiscoveredInventoryItem doc comment.
		// Same top-level-property read as Type/MoRef/Name/Build above -- GetProperty
		// now routes through #975's PowerShellValueUnwrap chokepoint, and a dedicated
		// boundary test (DiscoveryVersionBoundaryTests, driving the real executor)
		// proves this field survives non-null.
		string? version = GetProperty<string>(psObject, "Version");

		return new DiscoveredInventoryItem(type!, moRef, name, parentMoRef, build, maintenanceMode, version);
	}

	private static T? GetProperty<T>(System.Management.Automation.PSObject psObject, string name)
		where T : class
	{
		// Issue #975's chokepoint (PowerShellValueUnwrap): every top-level pipeline
		// output object is already unwrapped by PowerShellExecutor.Unwrap before
		// TryParseItem ever sees it, so this read was never actually exposed to #972's
		// nested-property hazard -- routing it through UnwrapAs anyway costs nothing
		// (idempotent on an already-unwrapped value) and keeps every PSObject property
		// read in this codebase going through the same one chokepoint.
		return PowerShellValueUnwrap.UnwrapAs<T>(psObject.Properties[name]?.Value);
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
