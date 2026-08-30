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
/// component with no active baseline yet, a missing input/credential). ADR-0023
/// ("Missing facts/baselines skip only the affected component") and ADR-0024 ("A
/// missing, incompatible, or ambiguous credential affects only components requiring
/// that purpose... The run is incomplete, not rejected wholesale") sanction
/// skip-and-continue for exactly these states: the affected component is recorded as a
/// skip and its siblings still plan.
///
/// An unmapped STIG benchmark is deliberately NOT one of these skippable gaps (issue
/// #1021 retired <see cref="UnmappedBenchmark"/> as a producible reason): per the owner
/// correction on #730 and the decided lifecycle in #1002, the XCCDF is optional
/// CKL-metadata enrichment, never an execution gate, so that state plans as an accepted
/// item with <see cref="ScanPlanItem.IsBenchmarkMissing"/> set instead.
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

	/// <summary>
	/// RETIRED as a producible skip reason (issue #1021): a STIG execution profile's
	/// active baseline with no mapped benchmark revision no longer skips. Per the owner
	/// correction on #730 (2026-08-28) and the decided lifecycle in #1002, execution
	/// requires only the approved profile baseline -- the XCCDF is optional CKL-metadata
	/// enrichment, never an execution gate -- so <see cref="ScanPlannerService"/> now
	/// plans this state as an accepted <see cref="ScanPlanItem"/> with
	/// <see cref="ScanPlanItem.IsBenchmarkMissing"/> set instead of this skip. The
	/// constant is retained (never removed, and deliberately excluded from
	/// <see cref="All"/>) only so historical <c>scan_plans.skips_json</c> rows persisted
	/// before this fix (ADR-0023 "immutable plans") still deserialize and display
	/// honestly; no code path can produce a new row with this reason anymore.
	/// </summary>
	public const string UnmappedBenchmark = "unmapped_benchmark";

	/// <summary>
	/// A declared Input marked required (<c>catalog_declared_inputs.is_required</c>)
	/// resolved to no config document at any layer (Global/Site/Target) for the plan
	/// item's execution profile (issue #735, ADR-0024 "A missing required Input leaves
	/// the affected component job visibly skipped without an execution attempt and with a
	/// safe readiness reason"). The component is skipped -- never executed without its
	/// required environmental input -- while siblings with satisfied inputs still plan.
	/// The skip <see cref="ScanPlanSkip.Detail"/> names the input definition (a
	/// non-secret catalog identifier, not a resolved value) and remediation path.
	/// </summary>
	public const string MissingRequiredInput = "missing_required_input";

	/// <summary>
	/// Issue #1012 defense-in-depth: the resolved execution profile's own catalog
	/// transport is one of docs/compliance-parity.md's closed transport vocabulary --
	/// EVERY documented transport (<c>vmware</c>, <c>ssh</c>, <c>nsx-api</c>,
	/// <c>vcf-api</c>) implies at least one required credential purpose per the
	/// provenance matrix's Purpose column -- yet the profile's
	/// <c>catalog_credential_requirements</c> resolved to an EMPTY set. Before this
	/// issue, an importer-promoted profile could reach this state (root cause: only
	/// seed migrations wrote that table) and the planner treated the empty set as
	/// nothing-to-resolve, so <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>
	/// found no gap and the job fanned out with no credential at all, failing only at
	/// execution with no preview-time warning. This skip makes that state visible
	/// AT PLAN-COMPILE TIME, before any run/job row exists -- the round-8 report's core
	/// complaint was exactly "no preview-time gap" for this scenario. Issue #1012 also
	/// fixes the root cause (catalog promotion now derives and writes the same
	/// requirements a seed row of the identical shape would carry -- see
	/// <see cref="Waypoint.Core.ComplianceContent.CredentialRequirementDerivation"/>),
	/// so this skip is a safety net for any catalog state this fix did not anticipate
	/// (a future promotion path, a hand-edited row, a not-yet-covered shape), never the
	/// primary defense.
	/// </summary>
	public const string CredentialedTransportWithNoRequirement = "credentialed_transport_with_no_requirement";

	/// <summary>
	/// Issue #1138: two or more narrowable <c>esxi</c>/<c>vm</c> plan items on the SAME
	/// vSphere target (vCenter) resolved to the SAME component <c>DisplayName</c>.
	/// Since #1135, a narrowed vSphere job's <c>selector_name</c> is the discovered
	/// component's DisplayName (the vendor profile matches
	/// <c>Get-VMHost -Name</c>/<c>Get-VM -Name</c> on it, never the MoRef) -- but a
	/// name is unique for an ESXi host per vCenter, NOT for a VM: two VMs in different
	/// folders/datacenters of the same vCenter may share a name. When they do,
	/// <c>Get-VM -Name &lt;name&gt;</c> returns every same-named object, so each
	/// sibling narrowed job would evaluate ALL of them and results would be
	/// cross-attributed with no diagnostic -- a silent widening of an explicitly
	/// narrowed scope, the same class of contract violation ADR-0023 "explicit scope
	/// never widens" forbids. <see cref="Waypoint.Infrastructure.Runs.ScanPlannerService"/>
	/// detects the collision AFTER compiling every candidate's item (so it needs the
	/// full accepted set to compare across siblings) and demotes EVERY colliding
	/// component to this skip -- never just one side of the pair, and never a
	/// disambiguation guess -- so component identity itself (MoRef, ADR-0023) is
	/// never touched. Component identity keying and the underlying MoRef are
	/// unaffected; this is purely about the scoping VALUE's ambiguity.
	/// </summary>
	public const string AmbiguousSelectorName = "ambiguous_selector_name";

	/// <summary>
	/// Issue #1138: a narrowable <c>esxi</c>/<c>vm</c> plan item's component
	/// <c>DisplayName</c> contains whitespace or a quote character (<c>'</c> or
	/// <c>"</c>). The vendor ESX baseline content interpolates the name UNQUOTED into
	/// the PowerCLI selector (<c>Get-VMHost -Name #{vmhostName}</c>) -- a name
	/// containing whitespace breaks that interpolation (the shell/PowerShell
	/// tokenizer splits it into more than one argument), and a quote character can
	/// break out of the interpolated string entirely. This is vendor content, not
	/// Waypoint code, but it constrains what Waypoint can safely pass as
	/// <c>selector_name</c> -- Waypoint has no way to prove a given release's profile
	/// quotes the value, so any unsafe name is skipped rather than risking a broken or
	/// injected PowerCLI invocation. Independent of <see cref="AmbiguousSelectorName"/>:
	/// a component can have an unsafe name with no collision at all.
	/// </summary>
	public const string UnsafeSelectorName = "unsafe_selector_name";

	/// <summary>
	/// The closed, PRODUCIBLE set (issue #1021: <see cref="UnmappedBenchmark"/> is
	/// deliberately excluded -- it is retired/historical-only, see its own doc comment).
	/// </summary>
	public static readonly IReadOnlyCollection<string> All =
		[Unsupported, NoActiveBaseline, MissingRequiredInput, CredentialedTransportWithNoRequirement, AmbiguousSelectorName, UnsafeSelectorName];
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
/// state -- never re-derived by a later reader. <see cref="BaselineId"/> is always
/// non-null for an accepted item (an unplannable component is a <see cref="ScanPlanSkip"/>,
/// never a partially-populated item). <see cref="BenchmarkRevisionId"/> is null for an
/// SRG execution profile (no XCCDF benchmark concept, ADR-0022) -- and, since issue
/// #1021, ALSO null for a STIG execution profile whose active baseline has no benchmark
/// mapped yet (<see cref="IsBenchmarkMissing"/> distinguishes the two null cases: false
/// for SRG, true for an unmapped STIG baseline running profile-only).
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
///
/// <see cref="RequiresSudo"/>/<see cref="SudoRequiresPassword"/> (issue #743,
/// migration 0074) freeze the catalog component's declared ssh sudo policy at
/// plan-compile time -- meaningful only for
/// <see cref="Waypoint.Core.ComplianceContent.CatalogTransports.Ssh"/> items, null for
/// every other transport AND for rows persisted before this issue (execution falls
/// back to the pre-#743 credential-driven sudo behavior on null, so legacy plans
/// replay byte-identically).
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
	PlanAttestationResolution? AttestationResolution = null,
	bool? RequiresSudo = null,
	bool? SudoRequiresPassword = null)
{
	/// <summary>Never-null convenience accessor -- callers iterate this instead of null-checking <see cref="InputResolutions"/> everywhere.</summary>
	public IReadOnlyList<PlanInputResolution> InputResolutionsOrEmpty => InputResolutions ?? [];

	/// <summary>
	/// Issue #1021: true for a STIG execution profile (<see cref="OutputKind"/> ==
	/// <see cref="Waypoint.Core.ComplianceContent.CatalogOutputKinds.HdfAndCkl"/>) whose
	/// active baseline had no benchmark revision mapped at plan-compile time
	/// (<see cref="BenchmarkRevisionId"/> is null). A COMPUTED accessor, not a stored
	/// column -- it is a pure function of two fields already frozen at plan-compile time
	/// and already persisted (<c>scan_plan_items.output_kind</c>/<c>benchmark_revision_id</c>),
	/// so no migration/new column is needed and there is no way for a stored flag to ever
	/// drift out of sync with the fields it describes. False for an SRG item, whose null
	/// <see cref="BenchmarkRevisionId"/> means "no benchmark concept" rather than
	/// "benchmark missing" (ADR-0022) -- callers surface this as the #1002
	/// <c>benchmark_missing</c> non-blocking alert on the plan/scan, never as a skip.
	/// </summary>
	public bool IsBenchmarkMissing =>
		string.Equals(OutputKind, Waypoint.Core.ComplianceContent.CatalogOutputKinds.HdfAndCkl, StringComparison.Ordinal)
		&& BenchmarkRevisionId is null;
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
