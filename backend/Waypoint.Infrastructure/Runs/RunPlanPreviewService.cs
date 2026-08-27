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

using Waypoint.Core.Components;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issues #733/#734 remainder ("`/runs/plan-preview`'s mandatory discovery refresh"
/// per docs/api-contract.md, and PR #819's planned <c>POST /runs/plan-preview</c>):
/// runs the SAME compile→resolve pipeline <see cref="RunCreationService.CreateScanRunAsync"/>
/// uses (<see cref="ScopeResolutionService"/> then <see cref="ScanPlannerService"/>, then
/// read-only credential-gap evaluation) entirely IN MEMORY and returns the would-be
/// <see cref="ScanPlan"/> -- no run row, no <c>run_scope_snapshots</c> row, no
/// <c>scan_plans</c>/<c>scan_plan_items</c> rows, no <c>job_credential_bindings</c>, no
/// job fan-out. This class owns no write path at all: every dependency it holds is used
/// read-only (<see cref="ScopeResolutionService.ResolveAsync"/> and
/// <see cref="ScanPlannerService.CompileAsync"/> already perform no persistence
/// themselves -- see their own doc comments -- so preview simply stops one step short of
/// where <see cref="RunCreationService.CreateScanRunAsync"/> starts writing).
///
/// Digest parity (issue #734 AC-4, this issue's own required reading): preview calls the
/// identical <see cref="ScanPlannerService.CompileAsync(Guid?, IReadOnlyList{Guid}, System.Threading.CancellationToken)"/>
/// entry point create uses (with <c>runId: null</c>, exactly as create does before it has
/// a run id), so <see cref="ScanPlan.PlanDigest"/> for the same resolved component set is
/// byte-for-byte identical between a preview call and the subsequent create call -- the
/// digest function depends only on schema version, resolved component ids, and accepted
/// items, never on wall-clock time or a run id. A credential gap demotes the SAME plan
/// items via the SAME <see cref="DemoteForCredentialGaps"/> logic
/// <see cref="RunCreationService"/> applies post-resolution, so an identical credential
/// state between preview and create also preserves parity for the demoted digest.
///
/// This is a THIN, additive sibling of <see cref="RunCreationService"/> -- it does not
/// modify that type, <see cref="ScanPlannerService"/>, or credential-resolution internals
/// (those stay owned by the #735 lane); it only calls their existing public surface.
/// </summary>
public sealed class RunPlanPreviewService
{
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;
	private readonly ScopeResolutionService _scopeResolution;
	private readonly ScanPlannerService _planner;
	private readonly IComponentRepository _components;
	private readonly TargetCredentialBindingRepository _bindings;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;

	public RunPlanPreviewService(
		SiteRepository sites,
		TargetRepository targets,
		ScopeResolutionService scopeResolution,
		ScanPlannerService planner,
		IComponentRepository components,
		TargetCredentialBindingRepository bindings,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials)
	{
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(scopeResolution);
		ArgumentNullException.ThrowIfNull(planner);
		ArgumentNullException.ThrowIfNull(components);
		ArgumentNullException.ThrowIfNull(bindings);
		ArgumentNullException.ThrowIfNull(credentials);
		_sites = sites;
		_targets = targets;
		_scopeResolution = scopeResolution;
		_planner = planner;
		_components = components;
		_bindings = bindings;
		_credentials = credentials;
	}

	/// <summary>
	/// Previews a scan run's would-be plan for <paramref name="scopeJson"/> -- the same
	/// <c>scope</c> shape <c>POST /runs</c> accepts for a scan, restricted to the
	/// <c>target_scope</c> shape (issue #733/#734): <c>site_id</c> is required,
	/// <c>target_scope</c> is required (preview never selects a profile -- ADR-0022 §7 --
	/// so the legacy <c>target_ids</c>-only/<c>profile_id</c> shape has nothing to
	/// preview). Mirrors every pre-creation validation
	/// <see cref="RunCreationService.CreateScanRunAsync"/> performs up through plan
	/// compilation, but returns the plan instead of persisting anything.
	/// </summary>
	public async Task<RunPlanPreviewResult> PreviewAsync(
		string scopeJson,
		IReadOnlyList<RunCredentialOverride>? credentialOverrides,
		IReadOnlyList<RunAdHocCredential>? adHocCredentials,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(scopeJson);

		ScanScope scope;
		try
		{
			scope = ScanScopeParser.Parse(scopeJson);
		}
		catch (FormatException exception)
		{
			throw ApiException.Validation("scope is not valid.", exception.Message);
		}

		if (scope.SiteId is not { } siteId)
		{
			throw ApiException.Validation(
				"scope.site_id is required to preview a scan plan.",
				"Set \"scope\": { \"site_id\": \"<uuid>\", \"target_scope\": { ... } } in the request body.");
		}

		if (scope.ProfileId is not null)
		{
			throw ApiException.Validation(
				"scope.profile_id is not accepted by plan preview.",
				"Plan preview never selects a profile (ADR-0022 §7 \"Start a Scan ... never selects a profile\"); omit \"profile_id\" and describe the asset selection via \"target_scope\".");
		}

		if (scope.TargetScope is not { } targetScopeRequest)
		{
			throw ApiException.Validation(
				"scope.target_scope is required to preview a scan plan.",
				"Set \"scope\": { \"site_id\": \"<uuid>\", \"target_scope\": { \"mode\": \"all\"|\"explicit\", ... } } in the request body.");
		}

		if (!TargetScopeModes.IsValid(targetScopeRequest.Mode))
		{
			throw ApiException.Validation(
				"scope.target_scope.mode is not valid.",
				$"\"scope.target_scope.mode\" must be one of: {string.Join(", ", new[] { TargetScopeModes.All, TargetScopeModes.Explicit })}.");
		}

		Site? site = await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
		if (site is null)
		{
			throw ApiException.NotFound("Site not found.", $"Site '{siteId}' does not exist.");
		}

		ResolvedTargetScope resolvedTargetScope =
			await _scopeResolution.ResolveAsync(siteId, targetScopeRequest, cancellationToken).ConfigureAwait(false);

		ScanPlan plan = await _planner.CompileAsync(null, resolvedTargetScope.ResolvedComponentIds, cancellationToken).ConfigureAwait(false);

		// Issue #736's per-plan-item purpose requirements, evaluated read-only against
		// each item's owning target's current bindings/overrides -- the exact same
		// (target, purpose) precedence RunCreationService.ResolveCredentialBindingsAsync
		// applies (ad hoc > saved override > target binding; preview has no run-level
		// legacy credential_id or inline ad hoc "my credentials" tier, since those are
		// create-time-only secrets, never resolved in advance of a run existing), except
		// a gap here is only ever surfaced, never thrown -- a preview's whole point is to
		// show the caller every gap before they commit to creating the run.
		Dictionary<Guid, PlanTargetRequirement> requirementsByTarget =
			await PlanCredentialRequirements.GroupByTargetAsync(plan, _components, cancellationToken).ConfigureAwait(false);

		(IReadOnlyList<CredentialBindingGap> gaps, ScanPlan demotedPlan) = requirementsByTarget.Count == 0
			? ([], plan)
			: await EvaluateCredentialCoverageAsync(plan, requirementsByTarget, credentialOverrides, adHocCredentials, cancellationToken)
				.ConfigureAwait(false);

		return new RunPlanPreviewResult(resolvedTargetScope, demotedPlan, gaps);
	}

	/// <summary>
	/// Read-only counterpart of <see cref="RunCreationService.ResolveCredentialBindingsAsync"/>
	/// scoped to plan-driven targets only (preview has no legacy target_ids/profile_id
	/// path): resolves each plan-driven target's required purposes against its bindings/
	/// overrides, collecting every gap rather than throwing, then demotes exactly the plan
	/// items whose required purpose did not resolve -- the same demotion
	/// <see cref="RunCreationService.DemotePlanItemsWithUnresolvedCredentials"/> performs
	/// post-resolution on create, reused here via <see cref="DemoteForCredentialGaps"/> so
	/// preview's returned plan (and its digest, once credentials resolve identically at
	/// create time) matches what create would actually persist.
	/// </summary>
	private async Task<(IReadOnlyList<CredentialBindingGap> Gaps, ScanPlan Plan)> EvaluateCredentialCoverageAsync(
		ScanPlan plan,
		Dictionary<Guid, PlanTargetRequirement> requirementsByTarget,
		IReadOnlyList<RunCredentialOverride>? overrides,
		IReadOnlyList<RunAdHocCredential>? adHocCredentials,
		CancellationToken cancellationToken)
	{
		IReadOnlyDictionary<Guid, IReadOnlyList<TargetCredentialBinding>> bindingsByTarget =
			await _bindings.ListForTargetsAsync([.. requirementsByTarget.Keys], cancellationToken).ConfigureAwait(false);

		Dictionary<(Guid TargetId, string Purpose), Guid> acceptedOverrides = [];
		foreach (RunCredentialOverride @override in overrides ?? [])
		{
			if (requirementsByTarget.ContainsKey(@override.TargetId))
			{
				acceptedOverrides[(@override.TargetId, @override.Purpose)] = @override.CredentialId;
			}
		}

		HashSet<(Guid TargetId, string Purpose)> acceptedAdHoc = [];
		foreach (RunAdHocCredential adHoc in adHocCredentials ?? [])
		{
			if (requirementsByTarget.ContainsKey(adHoc.TargetId))
			{
				acceptedAdHoc.Add((adHoc.TargetId, adHoc.Purpose));
			}
		}

		List<CredentialBindingGap> gaps = [];
		foreach ((Guid targetId, PlanTargetRequirement requirement) in requirementsByTarget)
		{
			IReadOnlyList<TargetCredentialBinding> targetBindings =
				bindingsByTarget.TryGetValue(targetId, out IReadOnlyList<TargetCredentialBinding>? found) ? found : [];

			foreach (string purpose in requirement.RequiredPurposes)
			{
				if (acceptedAdHoc.Contains((targetId, purpose)))
				{
					continue;
				}

				if (acceptedOverrides.TryGetValue((targetId, purpose), out Guid overrideCredentialId))
				{
					Waypoint.Core.Secrets.CredentialResponse? overrideCredential =
						await _credentials.GetAsync(overrideCredentialId, cancellationToken).ConfigureAwait(false);
					if (overrideCredential is null)
					{
						gaps.Add(new CredentialBindingGap(targetId, null, purpose, CredentialBindingGapReasons.CredentialNotFound, overrideCredentialId));
					}
					else if (!Waypoint.Core.Secrets.CredentialPurposeMatrix.IsCompatible(purpose, overrideCredential.CredentialType))
					{
						gaps.Add(new CredentialBindingGap(targetId, null, purpose, CredentialBindingGapReasons.IncompatibleCredentialType, overrideCredentialId));
					}

					continue;
				}

				TargetCredentialBinding? binding = targetBindings
					.FirstOrDefault(b => string.Equals(b.Purpose, purpose, StringComparison.Ordinal));
				if (binding is null)
				{
					gaps.Add(new CredentialBindingGap(targetId, null, purpose, CredentialBindingGapReasons.MissingBinding));
				}
			}
		}

		ScanPlan demoted = DemoteForCredentialGaps(plan, requirementsByTarget, gaps);
		return (gaps, demoted);
	}

	/// <summary>
	/// Preview's read-only mirror of
	/// <see cref="RunCreationService.DemotePlanItemsWithUnresolvedCredentials"/>: removes
	/// every accepted item whose owning target has an unresolved required purpose and
	/// records it as an explicit <see cref="ScanPlanSkip"/>, recomputing the digest exactly
	/// the same way -- kept as a private copy (not a shared extraction) because the #735
	/// lane owns edits to <see cref="RunCreationService"/>'s credential-resolution
	/// internals; a future consolidation once that lane lands is a natural follow-up, not
	/// this issue's remainder.
	/// </summary>
	private static ScanPlan DemoteForCredentialGaps(
		ScanPlan plan,
		Dictionary<Guid, PlanTargetRequirement> requirementsByTarget,
		List<CredentialBindingGap> gaps)
	{
		if (gaps.Count == 0)
		{
			return plan;
		}

		HashSet<(Guid TargetId, string Purpose)> unresolvedPairs = [.. gaps.Select(g => (g.TargetId, g.Purpose))];

		Dictionary<Guid, Guid> targetByComponent = [];
		foreach ((Guid targetId, PlanTargetRequirement requirement) in requirementsByTarget)
		{
			foreach (ScanPlanItem item in requirement.Items)
			{
				targetByComponent[item.ComponentId] = targetId;
			}
		}

		List<ScanPlanItem> survivingItems = [];
		List<ScanPlanSkip> newSkips = [];
		foreach (ScanPlanItem item in plan.Items)
		{
			if (!targetByComponent.TryGetValue(item.ComponentId, out Guid ownerTargetId))
			{
				survivingItems.Add(item);
				continue;
			}

			string? unresolvedPurpose = item.RequiredPurposes.FirstOrDefault(purpose => unresolvedPairs.Contains((ownerTargetId, purpose)));
			if (unresolvedPurpose is null)
			{
				survivingItems.Add(item);
				continue;
			}

			CredentialBindingGap gap = gaps.First(g => g.TargetId == ownerTargetId && g.Purpose == unresolvedPurpose);
			newSkips.Add(new ScanPlanSkip(
				item.ComponentId,
				gap.Reason,
				$"Component '{item.ComponentId}' requires credential purpose '{unresolvedPurpose}', which could not resolve ({gap.Reason})."));
		}

		if (newSkips.Count == 0)
		{
			return plan;
		}

		List<ScanPlanSkip> allSkips = [.. plan.Skips, .. newSkips];
		Guid[] scopeSeed = [.. plan.Items.Select(i => i.ComponentId).Concat(plan.Skips.Select(s => s.ComponentId)).Distinct()];
		string digest = ScanPlanDigest.Compute(plan.PlanSchemaVersion, scopeSeed, survivingItems);
		string explanation = $"{survivingItems.Count} of {plan.Items.Count + plan.Skips.Count} requested component(s) accepted into the plan "
			+ $"after credential resolution; {allSkips.Count} skipped (including {newSkips.Count} for an unresolved required credential).";

		return plan with { Items = survivingItems, Skips = allSkips, PlanDigest = digest, Explanation = explanation };
	}
}

/// <summary>
/// The result of <see cref="RunPlanPreviewService.PreviewAsync"/>: the resolved scope
/// (requested-vs-resolved, with every <see cref="ScopeOmission"/>), the would-be
/// <see cref="ScanPlan"/> (post credential-gap demotion), and every credential gap found
/// -- the same three facets docs/api-contract.md's planned <c>/runs/plan-preview</c>
/// response describes ("resolved component set, per-component readiness ... any
/// fact_conflict ... required-purpose credential coverage").
/// </summary>
public sealed record RunPlanPreviewResult(
	ResolvedTargetScope ResolvedScope,
	ScanPlan Plan,
	IReadOnlyList<CredentialBindingGap> CredentialGaps);
