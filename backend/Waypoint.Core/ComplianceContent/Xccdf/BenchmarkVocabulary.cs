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

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>The closed benchmark-revision source vocabulary (migration 0052's <c>benchmark_revisions</c> CHECK constraint).</summary>
public static class BenchmarkSources
{
	public const string ManualUpload = "manual_upload";
	public const string StigManager = "stig_manager";

	public static readonly IReadOnlyCollection<string> All = [ManualUpload, StigManager];

	public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>
/// The closed benchmark-revision lifecycle-state vocabulary (migration 0052). Issue
/// #730 scope: this PR only ever writes <see cref="Staged"/> (a freshly parsed
/// revision). <see cref="Active"/>/<see cref="Superseded"/>/<see cref="Rejected"/> are
/// reserved for the baseline-activation pipeline (#731) so the column does not need a
/// later migration to add values a still-future consumer will set.
/// </summary>
public static class BenchmarkLifecycleStates
{
	public const string Staged = "staged";
	public const string Active = "active";
	public const string Superseded = "superseded";
	public const string Rejected = "rejected";

	public static readonly IReadOnlyCollection<string> All = [Staged, Active, Superseded, Rejected];

	public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>The closed XCCDF rule-severity vocabulary (migration 0052's <c>benchmark_rules</c> CHECK constraint) -- DISA's own low/medium/high (CAT III/II/I) spelling.</summary>
public static class BenchmarkRuleSeverities
{
	public const string Low = "low";
	public const string Medium = "medium";
	public const string High = "high";

	public static readonly IReadOnlyCollection<string> All = [Low, Medium, High];

	public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>
/// The closed component-to-benchmark-revision mapping-status vocabulary (migration
/// 0052's <c>benchmark_component_mappings</c> CHECK constraint). Issue #730 AC
/// "rule-level mapping coverage and unmatched/ambiguous rules are queryable" at the
/// mapping level: a component with no candidate is <see cref="Unmapped"/>, a component
/// with exactly one high-confidence candidate not yet confirmed by an Admin is
/// <see cref="Suggested"/>, a component with more than one plausible candidate is
/// <see cref="Ambiguous"/> and must never auto-activate, and an Admin-confirmed or
/// system-proven exact match is <see cref="Mapped"/>.
/// </summary>
public static class BenchmarkMappingStatuses
{
	public const string Mapped = "mapped";
	public const string Suggested = "suggested";
	public const string Ambiguous = "ambiguous";
	public const string Unmapped = "unmapped";

	public static readonly IReadOnlyCollection<string> All = [Mapped, Suggested, Ambiguous, Unmapped];

	public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>
/// Issue #1002: the closed set of DERIVED, read-only mapping alert/state values a
/// caller can observe on <see cref="Waypoint.Api.Contracts.BenchmarkMappingResponse"/>
/// on top of the stored <see cref="BenchmarkMappingStatuses"/> value. Neither value is
/// ever written to <c>benchmark_component_mappings</c> -- both are computed at read
/// time from the component's bound catalog content kind (<see cref="Waypoint.Core.ComplianceContent.CatalogKinds"/>)
/// and its current mapping row, never stored, never admin-settable, and never
/// auto-suggested by the mapping matcher.
/// </summary>
public static class BenchmarkMappingDerivedStates
{
	/// <summary>
	/// The component's bound catalog content kind is <c>srg</c> (migration 0050): SRG
	/// content has no XCCDF/benchmark concept at all (ADR-0022 "An SRG has no XCCDF or
	/// CKL"), so it never participates in benchmark mapping. Replaces migration 0052's
	/// admin-stated <c>is_srg_no_benchmark</c> flag, dropped by migration 0071.
	/// </summary>
	public const string NotApplicableSrg = "not_applicable_srg";

	/// <summary>
	/// The component's bound catalog content kind is <c>stig</c> and its CURRENT
	/// mapping has no benchmark revision (status is <see cref="BenchmarkMappingStatuses.Unmapped"/>,
	/// <see cref="BenchmarkMappingStatuses.Suggested"/>, or
	/// <see cref="BenchmarkMappingStatuses.Ambiguous"/>, or no mapping decision has ever
	/// been recorded at all) -- a persistent, honest, OPEN alert (issue #1002 "STIG
	/// without a benchmark = approvable, scannable, with a STANDING OPEN ALERT") until
	/// an XCCDF is mapped. Never blocks baseline activation or scan planning: issue
	/// #1021 fixed <see cref="Waypoint.Infrastructure.Runs.ScanPlannerService"/> so this
	/// state plans the STIG execution profile profile-only (<see cref="Waypoint.Core.Scans.ScanPlanItem.IsBenchmarkMissing"/>
	/// set, no benchmark revision on the item) instead of the pre-#1021
	/// <c>unmapped_benchmark</c> skip that made the component permanently unplannable.
	/// </summary>
	public const string BenchmarkMissing = "benchmark_missing";
}

/// <summary>
/// Fail-closed validation for the benchmark vocabulary, mirroring
/// <see cref="CatalogVocabularyValidator"/>'s convention: every write path validates
/// before it reaches storage so a rejection is an actionable message naming the field,
/// not a generic Postgres CHECK-violation error.
/// </summary>
public static class BenchmarkVocabularyValidator
{
	public static IReadOnlyList<string> ValidateSource(string source) =>
		BenchmarkSources.IsValid(source)
			? []
			: [$"source '{source}' is not in the closed benchmark vocabulary ({string.Join(", ", BenchmarkSources.All)})"];

	public static IReadOnlyList<string> ValidateLifecycleState(string lifecycleState) =>
		BenchmarkLifecycleStates.IsValid(lifecycleState)
			? []
			: [$"lifecycle_state '{lifecycleState}' is not in the closed benchmark vocabulary ({string.Join(", ", BenchmarkLifecycleStates.All)})"];

	public static IReadOnlyList<string> ValidateSeverity(string severity) =>
		BenchmarkRuleSeverities.IsValid(severity)
			? []
			: [$"severity '{severity}' is not in the closed benchmark vocabulary ({string.Join(", ", BenchmarkRuleSeverities.All)})"];

	public static IReadOnlyList<string> ValidateMappingStatus(string status) =>
		BenchmarkMappingStatuses.IsValid(status)
			? []
			: [$"status '{status}' is not in the closed benchmark vocabulary ({string.Join(", ", BenchmarkMappingStatuses.All)})"];
}
