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

using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Components;
using Waypoint.Core.Jobs;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Resolves one <see cref="TargetScopeRequest"/>'s tri-state parent selection into an
/// explicit, deterministic, stable-component identity set (issue #733, epic #726 Wave
/// 2, ADR-0023 §3 "Scope, readiness, and conflicts"). This is the domain
/// join-and-validate step between the wire request and
/// <see cref="Waypoint.Core.Jobs.IRunScopeSnapshotRepository"/>'s persisted
/// requested/resolved audit snapshot -- it performs no persistence itself.
///
/// Validation performed here (issue #733 scope): ownership (every named target/
/// component belongs to the requested site), removal/retirement lifecycle state
/// (ADR-0023: absent/retired components are explicit coverage omissions, never
/// silently dropped or silently included), catalog compatibility via the merged
/// capability matcher (<see cref="ComponentCapabilityMatcher"/>, PR #839), and the
/// fact-conflict readiness gate (ADR-0023: only an interactive Cyber+ initiator may
/// resolve a conflict, and only for the current run -- there is no persisted
/// resolution to consult here, so a conflicted component is always an omission in
/// this slice; the interactive resolution flow itself is `/runs/plan-preview`
/// integration, explicitly out of this slice per issue #733's "NOT this slice: ...
/// plan-preview integration with discovery refresh"). Maintenance mode is
/// deliberately NOT checked -- ADR-0023 "Maintenance mode is informational and does
/// not exclude otherwise selected work," so this resolver never reads or filters on
/// it.
///
/// Deterministic: for the same persisted component/catalog state, resolving the same
/// request twice always yields the same <see cref="ResolvedTargetScope"/> (component
/// ids ordinally sorted; no wall-clock or random ordering anywhere in this class).
/// </summary>
public sealed class ScopeResolutionService
{
	private readonly TargetRepository _targets;
	private readonly IComponentRepository _components;
	private readonly ICatalogRepository _catalog;

	public ScopeResolutionService(TargetRepository targets, IComponentRepository components, ICatalogRepository catalog)
	{
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(components);
		ArgumentNullException.ThrowIfNull(catalog);
		_targets = targets;
		_components = components;
		_catalog = catalog;
	}

	/// <summary>
	/// Resolves <paramref name="request"/> against <paramref name="siteId"/>'s targets
	/// and their components. <see cref="TargetScopeRequest.Mode"/> must already be a
	/// member of <see cref="TargetScopeModes"/> -- the caller (request-shape
	/// validation, e.g. <c>RunsController</c>) rejects an unrecognized mode as a 400
	/// before this is ever called, matching every other malformed-request-shape check
	/// in this codebase.
	/// </summary>
	public async Task<ResolvedTargetScope> ResolveAsync(Guid siteId, TargetScopeRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		return string.Equals(request.Mode, TargetScopeModes.Explicit, StringComparison.Ordinal)
			? await ResolveExplicitAsync(siteId, request.ComponentIds ?? [], cancellationToken).ConfigureAwait(false)
			: await ResolveAllAsync(siteId, request.TargetIds, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Explicit mode: resolves EXACTLY the named component ids. Never widens (ADR-0023:
	/// "explicit scope never widens") and never falls back to "all" on an empty list
	/// (issue #733 AC "No scan silently falls back from an empty explicit selection to
	/// the whole site") -- an empty <paramref name="componentIds"/> resolves to zero
	/// components with zero omissions, an intentional empty plan, not an error and not
	/// a wider one.
	/// </summary>
	private async Task<ResolvedTargetScope> ResolveExplicitAsync(Guid siteId, IReadOnlyList<Guid> componentIds, CancellationToken cancellationToken)
	{
		List<Guid> resolved = [];
		List<ScopeOmission> omissions = [];

		// Cache targets already looked up by id within this call -- a request naming
		// several components under the same target should not re-fetch that target
		// once per component.
		Dictionary<Guid, Target?> targetCache = [];

		foreach (Guid componentId in componentIds.Distinct())
		{
			Component? component = await _components.GetAsync(componentId, cancellationToken).ConfigureAwait(false);
			if (component is null)
			{
				omissions.Add(new ScopeOmission(
					componentId, null, ScopeOmissionReasons.ComponentNotFound,
					$"Component '{componentId}' does not exist. Refresh the component list and re-select before submitting the scan."));
				continue;
			}

			if (!targetCache.TryGetValue(component.ParentTargetId, out Target? owningTarget))
			{
				owningTarget = await _targets.GetAsync(component.ParentTargetId, cancellationToken).ConfigureAwait(false);
				targetCache[component.ParentTargetId] = owningTarget;
			}

			if (owningTarget is null || owningTarget.SiteId != siteId)
			{
				omissions.Add(new ScopeOmission(
					componentId, component.ParentTargetId, ScopeOmissionReasons.ComponentNotInScope,
					$"Component '{componentId}' does not belong to site '{siteId}'. Refresh and re-select from this site's inventory."));
				continue;
			}

			await EvaluateComponentAsync(component, resolved, omissions, cancellationToken).ConfigureAwait(false);
		}

		resolved.Sort();
		return new ResolvedTargetScope(TargetScopeModes.Explicit, resolved, omissions);
	}

	/// <summary>
	/// "All" mode: expands to every catalog-compatible, non-retired component beneath
	/// every named top-level target (or every target under the site when
	/// <paramref name="targetIds"/> is null/empty -- the whole-site case). ADR-0023:
	/// "'all' includes newly discovered compatible components on those boundaries" --
	/// this always re-reads the live component set (never a stale cache), so a
	/// component discovered after the caller last loaded the UI is included
	/// automatically. Absent/retired/conflicted/incompatible components under an
	/// in-scope target are still explicit <see cref="ScopeOmission"/>s, never silently
	/// skipped without a trace (ADR-0023 "never silently dropped ... only excluded").
	/// </summary>
	private async Task<ResolvedTargetScope> ResolveAllAsync(Guid siteId, IReadOnlyList<Guid>? targetIds, CancellationToken cancellationToken)
	{
		List<Guid> resolved = [];
		List<ScopeOmission> omissions = [];

		IReadOnlyList<Target> targets;
		if (targetIds is null || targetIds.Count == 0)
		{
			targets = await _targets.ListAllForSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
		}
		else
		{
			List<Target> resolvedTargets = [];
			foreach (Guid targetId in targetIds.Distinct())
			{
				Target? target = await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
				if (target is null || target.SiteId != siteId)
				{
					omissions.Add(new ScopeOmission(
						null, targetId, ScopeOmissionReasons.TargetNotFound,
						$"Target '{targetId}' does not exist under site '{siteId}'. Refresh the target list and re-select before submitting the scan."));
					continue;
				}

				resolvedTargets.Add(target);
			}

			targets = resolvedTargets;
		}

		foreach (Target target in targets)
		{
			IReadOnlyList<Component> components = await _components.ListForTargetAsync(target.Id, includeRetired: true, cancellationToken).ConfigureAwait(false);
			foreach (Component component in components)
			{
				await EvaluateComponentAsync(component, resolved, omissions, cancellationToken).ConfigureAwait(false);
			}
		}

		resolved.Sort();
		return new ResolvedTargetScope(TargetScopeModes.All, resolved, omissions);
	}

	/// <summary>
	/// Shared readiness/compatibility gate for one already-in-scope component: lifecycle
	/// (absent/retired), fact conflict, then catalog compatibility via the merged
	/// capability matcher (PR #839's <see cref="ComponentCapabilityMatcher"/>) -- the
	/// same order ADR-0023 lists them ("Unsupported, conflicted, unreachable, absent,
	/// retired, ... remain explicit coverage omissions"). A component that passes every
	/// gate is appended to <paramref name="resolved"/>; otherwise exactly one
	/// <see cref="ScopeOmission"/> is appended, never both and never neither.
	/// </summary>
	private async Task EvaluateComponentAsync(
		Component component, List<Guid> resolved, List<ScopeOmission> omissions, CancellationToken cancellationToken)
	{
		if (component.Lifecycle == ComponentLifecycleStates.Retired)
		{
			omissions.Add(new ScopeOmission(
				component.Id, component.ParentTargetId, ScopeOmissionReasons.ComponentRetired,
				$"Component '{component.Id}' ({component.DisplayName}) is retired and excluded from selection. An Admin may purge it or it may reconnect on rediscovery."));
			return;
		}

		if (component.Lifecycle == ComponentLifecycleStates.Absent)
		{
			omissions.Add(new ScopeOmission(
				component.Id, component.ParentTargetId, ScopeOmissionReasons.ComponentAbsent,
				$"Component '{component.Id}' ({component.DisplayName}) was not observed by the most recent successful discovery refresh. Refresh inventory and re-select if it should still be scanned."));
			return;
		}

		if (component.FactConflict)
		{
			omissions.Add(new ScopeOmission(
				component.Id, component.ParentTargetId, ScopeOmissionReasons.FactConflict,
				$"Component '{component.Id}' ({component.DisplayName}) has a configured/discovered product-version conflict. An interactive Cyber-or-higher initiator must resolve it for this run (POST /runs/plan-preview); a scheduled run would skip it."));
			return;
		}

		Guid? linkedProductVersionId = null;
		string? linkedProductVersionKey = null;
		IReadOnlyList<CatalogExecutionProfileDetail> candidateProfiles = [];
		if (component.CatalogComponentId is { } catalogComponentId)
		{
			candidateProfiles = await _catalog.ListExecutionProfilesByComponentAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
			if (candidateProfiles.Count > 0)
			{
				linkedProductVersionId = candidateProfiles[0].ProductVersion.Id;
				linkedProductVersionKey = candidateProfiles[0].ProductVersion.VersionKey;
			}
		}

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, linkedProductVersionId, linkedProductVersionKey, candidateProfiles);
		if (!match.IsCompatible)
		{
			omissions.Add(new ScopeOmission(
				component.Id, component.ParentTargetId, ScopeOmissionReasons.CatalogIncompatible,
				string.Join(" ", match.IncompatibilityReasons)));
			return;
		}

		resolved.Add(component.Id);
	}
}
