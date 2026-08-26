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

namespace Waypoint.Core.Components;

/// <summary>
/// The closed set of reasons a requested component/target never reaches the resolved,
/// runnable set (issue #733 AC "Stale or removed selections fail with actionable
/// refresh guidance rather than silently widening scope"). Every value maps to a safe,
/// non-secret, actionable message -- never a bare boolean.
/// </summary>
public static class ScopeOmissionReasons
{
	/// <summary>An explicitly requested component id does not exist at all under the requested target(s) -- never discovered, or discovered under a different target.</summary>
	public const string ComponentNotFound = "component_not_found";

	/// <summary>An explicitly requested component id exists but belongs to a target outside the requested scope (a cross-site/cross-target id supplied by mistake or a stale client cache).</summary>
	public const string ComponentNotInScope = "component_not_in_scope";

	/// <summary>The component was previously discovered but a later refresh no longer observes it (ADR-0023 lifecycle "absent") -- present in history, not currently reachable.</summary>
	public const string ComponentAbsent = "component_absent";

	/// <summary>The component has been continuously absent past the retirement threshold and left active selection (ADR-0023: "leaves normal active selection").</summary>
	public const string ComponentRetired = "component_retired";

	/// <summary>The component's configured/discovered exact-version facts disagree and no interactive Cyber+ initiator resolved it for this run (ADR-0023 "an interactive Cyber-or-higher initiator chooses one for this run").</summary>
	public const string FactConflict = "fact_conflict";

	/// <summary>No catalog execution profile matches the component's resolved exact version/linkage (<see cref="ComponentCapabilityMatcher"/> fails closed) -- content may not be staged/activated, or the product/version is genuinely unsupported.</summary>
	public const string CatalogIncompatible = "catalog_incompatible";

	/// <summary>A requested top-level target id does not exist, or does not belong to the requested site.</summary>
	public const string TargetNotFound = "target_not_found";

	public static readonly IReadOnlyCollection<string> All =
	[
		ComponentNotFound, ComponentNotInScope, ComponentAbsent, ComponentRetired, FactConflict, CatalogIncompatible, TargetNotFound,
	];
}

/// <summary>
/// One component/target the requested scope named or implied that did not make it into
/// the resolved runnable set, with the exact reason (ADR-0023: "never silently dropped
/// ... or described as successful coverage"). <see cref="ComponentId"/> is null only for
/// <see cref="ScopeOmissionReasons.TargetNotFound"/>, where no component identity exists
/// to report.
/// </summary>
public sealed record ScopeOmission(Guid? ComponentId, Guid? TargetId, string Reason, string Detail);

/// <summary>
/// The result of resolving one <see cref="Waypoint.Core.Jobs.TargetScopeRequest"/>
/// against live component identity (issue #733, ADR-0023 §3 "Scope, readiness, and
/// conflicts"). <see cref="ResolvedComponentIds"/> is the exact, deterministic,
/// stable-identity set a plan/run may act on -- never a name, address, or tree
/// position. Ordered by component id for determinism (callers needing display order
/// re-sort; this is the persisted/audited order).
/// </summary>
public sealed record ResolvedTargetScope(
	string Mode,
	IReadOnlyList<Guid> ResolvedComponentIds,
	IReadOnlyList<ScopeOmission> Omissions)
{
	/// <summary>
	/// True when refresh/resolution validated at least one runnable component OR the
	/// requested scope was legitimately empty from the start (an explicit empty list is
	/// a valid, honest "scan nothing" request, distinct from every requested item being
	/// rejected). <see cref="Waypoint.Infrastructure.Runs.ScopeResolutionService"/>'s
	/// caller uses this to distinguish "zero-runnable-component" (ADR-0023: initiation
	/// fails) from an honest empty explicit selection some future caller might
	/// legitimately submit for a preview-only call.
	/// </summary>
	public bool HasAnyResolvedComponent => ResolvedComponentIds.Count > 0;
}
