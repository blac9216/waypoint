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

using System.Net;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Errors;

namespace Waypoint.Core.Scans;

/// <summary>
/// Thrown when <see cref="ScanPlannerService"/> encounters corrupt/inconsistent catalog
/// state it cannot validate (a "should-never-happen" data-integrity violation, e.g. an
/// SRG execution profile whose active baseline unexpectedly carries a benchmark
/// revision) while compiling a plan. This is NOT an operator-fixable per-component gap
/// (those are <see cref="ScanPlanSkip"/> rows -- see <see cref="ScanPlanSkipReasons"/>);
/// epic #726 §3/§5 never sanctions silently dropping a component on a data-integrity
/// violation while its siblings proceed, so this fails the WHOLE plan compilation closed.
///
/// It is an <see cref="ApiException"/> so the API layer's error middleware surfaces it
/// as a documented <c>plan_integrity_failure</c> response (a 500-class error -- corrupt
/// catalog state is a system-integrity fault an operator cannot fix by adjusting the
/// request, distinct from the 400-class request-shape rejections this path otherwise
/// raises) rather than an unmapped 500 that leaks a stack trace. Raised strictly BEFORE
/// any run/plan/job row is created (<see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>
/// compiles the plan as a pre-creation validation step), so the plan fails closed with
/// no partial persistence.
/// </summary>
public sealed class ScanPlanIntegrityException : ApiException
{
	public const string ErrorCode = "plan_integrity_failure";

	public ScanPlanIntegrityException(Guid componentId, string detail)
		: base(
			HttpStatusCode.InternalServerError,
			ErrorCode,
			"The scan plan could not be compiled because of inconsistent catalog state.",
			detail)
	{
		ComponentId = componentId;
	}

	/// <summary>The component whose catalog/baseline state was found inconsistent.</summary>
	public Guid ComponentId { get; }
}

/// <summary>
/// The current plan schema version this codebase writes and accepts (issue #734 AC
/// "Plan schema is versioned and rejects unknown versions fail-closed"). Bump this,
/// and add explicit migration/rejection handling, before changing
/// <see cref="ScanPlanItem"/>'s or <see cref="ScanPlan"/>'s persisted shape in any way
/// that would change <see cref="ScanPlanDigest"/>'s output for the same inputs -- a
/// silent shape change would break issue #734 AC-4 (preview/create digest parity)
/// across a deploy boundary.
/// </summary>
public static class ScanPlanSchema
{
	public const int CurrentVersion = 1;
}

/// <summary>
/// The closed set of reasons an execution item candidate never becomes an accepted
/// <see cref="ScanPlanItem"/> (issue #734 AC "Validation reports every incompatible
/// endpoint/profile/transport/credential/input/benchmark gap").
///
/// <b>Every value in this set is "architecturally skippable per epic #726 §3/§5":</b> an
/// operator-fixable gap in an otherwise-consistent catalog (an unsupported capability, a
/// component with no active baseline yet, an unmapped benchmark, a missing
/// input/credential). ADR-0023 ("Missing facts/baselines skip only the affected
/// component") and ADR-0024 ("A missing, incompatible, or ambiguous credential affects
/// only components requiring that purpose... The run is incomplete, not rejected
/// wholesale") sanction skip-and-continue for exactly these states: the affected
/// component is recorded as a skip and its siblings still plan.
///
/// A <b>planner-integrity failure</b> -- corrupt/inconsistent catalog state that the
/// planner could not validate (e.g. an SRG execution profile whose active baseline
/// unexpectedly carries a benchmark revision) -- is deliberately NOT in this set. Epic
/// §3/§5 never sanctions silently dropping a component on a data-integrity violation
/// while its siblings proceed; doing so would silently narrow a run's coverage instead
/// of surfacing corruption. Such a state fails the WHOLE plan compilation closed via
/// <see cref="ScanPlanIntegrityException"/> (no run/plan/job rows persisted) rather than
/// becoming a skip row. See <see cref="ScanPlannerService"/>'s doc comment for the full
/// skip-vs-fail reconciliation.
/// </summary>
public static class ScanPlanSkipReasons
{
	/// <summary>The component has no catalog-compatible execution profile at all (mirrors <see cref="Waypoint.Core.Components.ScopeOmissionReasons.CatalogIncompatible"/>, re-evaluated at plan time in case catalog state changed between scope resolution and planning).</summary>
	public const string Unsupported = "unsupported";

	/// <summary>The component's compatible execution profile has no active baseline (ADR-0023 "Each component is ready only with one exact catalog product-version entry and exactly one active, approved compatible baseline").</summary>
	public const string NoActiveBaseline = "no_active_baseline";

	/// <summary>A STIG execution profile's benchmark reference has no corresponding imported <c>benchmark_revisions</c> row, or no current component-to-benchmark-revision mapping exists (ADR-0022/#730).</summary>
	public const string UnmappedBenchmark = "unmapped_benchmark";

	public static readonly IReadOnlyCollection<string> All = [Unsupported, NoActiveBaseline, UnmappedBenchmark];
}

/// <summary>
/// One candidate component that did not become an accepted plan item, with the exact
/// reason (mirrors <see cref="Waypoint.Core.Components.ScopeOmission"/> one layer up
/// the pipeline -- scope resolution's omissions are about SELECTION eligibility;
/// this is about PLANNING readiness for an already-selected, already-eligible
/// component). Persisted verbatim in <see cref="ScanPlan.Skips"/> (migration 0057's
/// <c>skips_json</c>) so run history can show "why wasn't X scanned" without
/// re-deriving it from current catalog/baseline state.
/// </summary>
public sealed record ScanPlanSkip(Guid ComponentId, string Reason, string Detail);

/// <summary>
/// One frozen, accepted execution item (migration 0057's <c>scan_plan_items</c>;
/// ADR-0023 "Immutable plans"; ADR-0024's <c>PlannedComponentItem</c>). Every field is
/// resolved once, at plan-compile time, from data-driven catalog/baseline/component
/// state -- never re-derived by a later reader. <see cref="BaselineId"/> and
/// <see cref="BenchmarkRevisionId"/> are null for an SRG execution profile (no XCCDF
/// benchmark concept, ADR-0022) and non-null for a compatible STIG profile with an
/// active baseline (a STIG profile lacking either is a <see cref="ScanPlanSkip"/>, not
/// a partially-populated item -- see <see cref="ScanPlannerService"/>).
/// </summary>
/// <summary>
/// <see cref="InputResolutions"/> and <see cref="AttestationResolution"/> are the
/// issue #735/ADR-0024 config-resolution snapshot: resolved once at plan-compile time
/// by <see cref="Waypoint.Infrastructure.ConfigDocs.PlanConfigResolutionService"/> from
/// the Global -> Site -> Target config-doc stack keyed to
/// <see cref="CatalogExecutionProfileId"/>, and frozen here alongside every other
/// plan-item field -- reused by every attempt, never re-derived (ADR-0024 "The
/// immutable snapshot is part of the planned item's compliance definition"). Both
/// default to "no resolution attempted" (empty list / null) so a <see cref="ScanPlanItem"/>
/// built without going through the resolver (e.g. an existing unit test fixture
/// predating this issue) remains a valid, fully-constructible record.
/// </summary>
public sealed record ScanPlanItem(
	Guid ComponentId,
	Guid CatalogExecutionProfileId,
	Guid? BaselineId,
	Guid? BenchmarkRevisionId,
	string Transport,
	string SelectorKind,
	string? SelectorName,
	string ReportGroupKey,
	int Priority,
	string OutputKind,
	IReadOnlyList<string> RequiredPurposes,
	IReadOnlyList<string> DeclaredInputNames,
	IReadOnlyList<PlanInputResolution>? InputResolutions = null,
	PlanAttestationResolution? AttestationResolution = null)
{
	/// <summary>Never-null convenience accessor -- callers iterate this instead of null-checking <see cref="InputResolutions"/> everywhere.</summary>
	public IReadOnlyList<PlanInputResolution> InputResolutionsOrEmpty => InputResolutions ?? [];
}

/// <summary>
/// The complete, immutable, digest-addressed plan for one run (migration 0057's
/// <c>scan_plans</c> header + its accepted <c>scan_plan_items</c> rows). Returned by
/// both preview and create paths (issue #734 AC-4: "Preview and create use the same
/// planner and produce the same plan digest") -- <see cref="RunId"/> is null for a
/// preview that has no run yet (the planner itself never creates a run; the caller
/// decides whether/when to persist against a real run id).
/// </summary>
public sealed record ScanPlan(
	Guid? RunId,
	int PlanSchemaVersion,
	IReadOnlyList<ScanPlanItem> Items,
	IReadOnlyList<ScanPlanSkip> Skips,
	string PlanDigest,
	string Explanation)
{
	/// <summary>
	/// True when at least one item is accepted (issue #734 AC-1's all-or-nothing gate
	/// operates at this granularity, not per-item -- see
	/// <see cref="ScanPlannerService"/>'s doc comment). A plan with zero accepted items
	/// and zero skips (an empty requested/resolved scope) is still
	/// <see cref="IsRunnable"/> == false; the caller distinguishes "legitimately empty
	/// request" from "nothing survived planning" exactly as
	/// <see cref="Waypoint.Core.Components.ResolvedTargetScope.HasAnyResolvedComponent"/>
	/// already does one layer up, by checking whether the underlying resolved scope
	/// was itself intentionally empty before treating this as a rejection.
	/// </summary>
	public bool IsRunnable => Items.Count > 0;
}
