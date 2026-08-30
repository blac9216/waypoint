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

namespace Waypoint.Core.Scans;

/// <summary>
/// The closed component-result-attempt status vocabulary -- migration 0063's
/// <c>component_results_status_check</c>, duplicated here the same way
/// <see cref="Waypoint.Core.Jobs.JobUploadStatuses"/> mirrors migration 0018's
/// upload_status CHECK. <see cref="ComponentResultStatusConstraintDriftTests"/> pins
/// this against the migration's actual constraint text.
/// </summary>
public static class ComponentResultStatuses
{
	public const string Completed = "completed";
	public const string ExecutionError = "execution_error";
	public const string Skipped = "skipped";

	/// <summary>
	/// Issue #1140 (migration 0081): the attempt finished (the HDF parsed) but
	/// evaluated ZERO controls -- zero passed/open findings, with at least one
	/// <see cref="ComponentFindingStatuses.NotReviewed"/>/<see cref="ComponentFindingStatuses.Skipped"/>/
	/// <see cref="ComponentFindingStatuses.ExecutionError"/> finding or none at all
	/// (exactly <see cref="ComponentResultRecord.EvaluatedZeroControls"/>'s predicate,
	/// the same one <c>IComponentResultRepository.GetRunRollupAsync</c>'s
	/// <c>evaluated_zero_component_count</c> FILTER already used at read time --
	/// this is that same signal, now recorded on the row itself at write time
	/// instead of only inferred later). A genuinely all-<see cref="ComponentFindingStatuses.NotApplicable"/>
	/// component is never reclassified here -- N/A is a determinate outcome, not a
	/// failure to evaluate.
	/// </summary>
	public const string CompletedZeroControls = "completed_zero_controls";

	public static readonly IReadOnlyList<string> All = [Completed, ExecutionError, Skipped, CompletedZeroControls];
}

/// <summary>
/// The closed per-finding status vocabulary -- migration 0063's
/// <c>component_result_findings_status_check</c>. Epic #726 §6: "Failed, skipped,
/// excluded, not-applicable, open, and passed states are not conflated" --
/// <see cref="NotReviewed"/> is the exact-once "applicable control that cannot
/// execute" state (never omitted, never <see cref="NotApplicable"/>).
/// </summary>
public static class ComponentFindingStatuses
{
	public const string Passed = "passed";
	public const string Failed = "failed";
	public const string NotApplicable = "not_applicable";
	public const string NotReviewed = "not_reviewed";
	public const string ExecutionError = "execution_error";
	public const string Skipped = "skipped";

	public static readonly IReadOnlyList<string> All =
		[Passed, Failed, NotApplicable, NotReviewed, ExecutionError, Skipped];

	/// <summary>Statuses that count as an OPEN finding for CAT severity rollups (mirrors <see cref="HdfSeverityCounter"/>'s open predicate, generalized to the full status vocabulary).</summary>
	public static bool IsOpen(string status) => string.Equals(status, Failed, StringComparison.Ordinal);
}

/// <summary>The closed CAT-severity vocabulary -- migration 0063's <c>component_result_findings_severity_check</c>.</summary>
public static class ComponentFindingSeverities
{
	public const string CatI = "cat_i";
	public const string CatII = "cat_ii";
	public const string CatIII = "cat_iii";

	public static readonly IReadOnlyList<string> All = [CatI, CatII, CatIII];
}

/// <summary>The closed artifact-kind vocabulary -- migration 0063's <c>component_result_artifacts_kind_check</c>.</summary>
public static class ComponentResultArtifactKinds
{
	public const string HdfRaw = "hdf_raw";
	public const string HdfAttested = "hdf_attested";
	public const string Ckl = "ckl";
	public const string Summary = "summary";
	public const string Log = "log";

	public static readonly IReadOnlyList<string> All = [HdfRaw, HdfAttested, Ckl, Summary, Log];
}

/// <summary>One parsed/synthesized control-level finding, ready to persist as a <c>component_result_findings</c> row.</summary>
public sealed record ComponentResultFinding(
	string ControlId,
	string? RuleId,
	string? Title,
	string Severity,
	string Status,
	string? Evidence);

/// <summary>One artifact reference (kind/path/digest/size), ready to persist as a <c>component_result_artifacts</c> row.</summary>
public sealed record ComponentResultArtifact(
	string Kind,
	string Path,
	string Digest,
	long SizeBytes);

/// <summary>
/// The full immutable result of one job attempt against one scan plan item --
/// everything <see cref="ComponentResultRepository"/> needs to write one
/// <c>component_results</c> row plus its findings/artifacts in one transaction.
/// </summary>
public sealed record ComponentResultRecord(
	Guid RunId,
	Guid JobId,
	Guid ScanPlanItemId,
	Guid ComponentId,
	int AttemptNumber,
	string Status,
	string? Detail,
	IReadOnlyList<ComponentResultFinding> Findings,
	IReadOnlyList<ComponentResultArtifact> Artifacts)
{
	public int CatIOpen => Findings.Count(f => ComponentFindingStatuses.IsOpen(f.Status) && f.Severity == ComponentFindingSeverities.CatI);
	public int CatIIOpen => Findings.Count(f => ComponentFindingStatuses.IsOpen(f.Status) && f.Severity == ComponentFindingSeverities.CatII);
	public int CatIIIOpen => Findings.Count(f => ComponentFindingStatuses.IsOpen(f.Status) && f.Severity == ComponentFindingSeverities.CatIII);
	public int PassedCount => Findings.Count(f => f.Status == ComponentFindingStatuses.Passed);
	public int NotApplicableCount => Findings.Count(f => f.Status == ComponentFindingStatuses.NotApplicable);
	public int NotReviewedCount => Findings.Count(f => f.Status == ComponentFindingStatuses.NotReviewed);
	public int SkippedCount => Findings.Count(f => f.Status == ComponentFindingStatuses.Skipped);

	/// <summary>
	/// Issue #1144: findings mapped to <see cref="ComponentFindingStatuses.ExecutionError"/>
	/// -- migration 0080's sixth and final per-finding-status count column. Before this,
	/// an execution-error finding landed in no <c>component_results</c> column at all,
	/// so a component whose controls ALL errored read as all-zero on the run rollup.
	/// </summary>
	public int ExecutionErrorCount => Findings.Count(f => f.Status == ComponentFindingStatuses.ExecutionError);

	/// <summary>
	/// Issue #1140: true when this attempt's findings produced NO verdict -- zero
	/// passed and zero open (failed) findings, with at least one
	/// <see cref="ComponentFindingStatuses.NotReviewed"/>/<see cref="ComponentFindingStatuses.Skipped"/>/
	/// <see cref="ComponentFindingStatuses.ExecutionError"/> finding or none at all.
	/// The EXACT predicate <see cref="IComponentResultRepository.GetRunRollupAsync"/>'s
	/// SQL <c>evaluated_zero_component_count</c> FILTER already applies at read time
	/// (see that method's own doc comment) -- kept as one C# definition here so
	/// <see cref="Waypoint.Infrastructure.Runs.ComponentResultRecordingService"/> can
	/// apply it at WRITE time (<see cref="ComponentResultStatuses.CompletedZeroControls"/>)
	/// without a second, hand-copied predicate that could drift from the read side.
	/// Deliberately excludes a genuinely all-<see cref="ComponentFindingStatuses.NotApplicable"/>
	/// component: N/A is a determinate outcome, not a failure to evaluate.
	/// </summary>
	public bool EvaluatedZeroControls => EvaluatedZeroControlsFor(Findings);

	/// <summary>
	/// The predicate <see cref="EvaluatedZeroControls"/> applies, exposed as a static
	/// helper so <see cref="Waypoint.Infrastructure.Runs.ComponentResultRecordingService"/>
	/// can classify a candidate finding list at write time (issue #1140) without
	/// constructing a throwaway record first.
	/// </summary>
	public static bool EvaluatedZeroControlsFor(IReadOnlyList<ComponentResultFinding> findings)
	{
		int open = findings.Count(f => ComponentFindingStatuses.IsOpen(f.Status));
		int passed = findings.Count(f => f.Status == ComponentFindingStatuses.Passed);
		int notReviewed = findings.Count(f => f.Status == ComponentFindingStatuses.NotReviewed);
		int skipped = findings.Count(f => f.Status == ComponentFindingStatuses.Skipped);
		int executionError = findings.Count(f => f.Status == ComponentFindingStatuses.ExecutionError);
		int notApplicable = findings.Count(f => f.Status == ComponentFindingStatuses.NotApplicable);

		return open + passed == 0 && (notReviewed > 0 || skipped > 0 || executionError > 0 || notApplicable == 0);
	}
}

/// <summary>One (priority-free) row of the run rollup: counts by component-result status, CAT totals, and coverage.</summary>
public sealed record RunResultRollupRow(
	string Status,
	int ComponentCount,
	int CatIOpen,
	int CatIIOpen,
	int CatIIIOpen,
	int PassedCount,
	int NotApplicableCount,
	int NotReviewedCount,
	int SkippedCount,
	int EvaluatedZeroComponentCount,
	int ExecutionErrorCount = 0)
{
	/// <summary>
	/// Issue #1132: how many COMPONENTS in this status bucket produced NO verdict --
	/// zero passed and zero open (failed) findings. This is a COMPONENT count, unlike
	/// its neighbour <see cref="ExecutionErrorCount"/> and every other count on this
	/// record, which sum FINDINGS. Counted PER COMPONENT by
	/// <see cref="IComponentResultRepository.GetRunRollupAsync"/>'s <c>count(*)
	/// FILTER</c>, never re-derived from this row's summed counts: the sums cannot
	/// express it, because a mixed bucket (one component that evaluated nothing,
	/// others evaluated normally) aggregates to a healthy-looking
	/// <c>PassedCount &gt; 0</c> and would read as fully evaluated.
	///
	/// Counted shapes include the all-<see cref="ComponentFindingStatuses.NotReviewed"/>
	/// scan (round-12's), the all-<see cref="ComponentFindingStatuses.Skipped"/> one, an
	/// all-<see cref="ComponentFindingStatuses.ExecutionError"/> component (migration
	/// 0080's <c>execution_error_count</c>, issue #1144), and a component that
	/// produced no findings at all.
	///
	/// The one zero-verdict shape deliberately NOT counted is the genuinely, entirely
	/// <see cref="ComponentFindingStatuses.NotApplicable"/> component: "nothing here
	/// applies" is a determinate outcome, not a failure to evaluate. Issue #1144
	/// closed the one gap this used to have at this grain: a component mixing
	/// <c>not_applicable</c> with only <c>execution_error</c> findings was previously
	/// indistinguishable from a genuine all-N/A component (both looked like
	/// <c>not_applicable_count &gt; 0</c>, everything else zero) -- now that
	/// <c>execution_error_count</c> is its own column, the SQL predicate below counts
	/// it explicitly rather than relying on <c>not_applicable_count = 0</c>.
	/// </summary>
	public int EvaluatedZeroComponentCount { get; init; } = EvaluatedZeroComponentCount;

	/// <summary>
	/// Issue #1144: how many FINDINGS in this bucket mapped to
	/// <see cref="ComponentFindingStatuses.ExecutionError"/> -- the sum of
	/// <see cref="ComponentResultRecord.ExecutionErrorCount"/> across the bucket's
	/// latest-attempt component rows (<c>sum(execution_error_count)</c>), the same
	/// convention as <see cref="PassedCount"/> and every other count on this record.
	/// A FINDING count, NOT a component count: three components with one errored
	/// control each and one component with three read the same <c>3</c> here. The
	/// only per-COMPONENT number on this record is
	/// <see cref="EvaluatedZeroComponentCount"/> (plus <see cref="ComponentCount"/>);
	/// do not render this one as "N components errored".
	/// </summary>
	public int ExecutionErrorCount { get; init; } = ExecutionErrorCount;

	/// <summary>True when AT LEAST ONE component in this bucket produced no verdict (see <see cref="EvaluatedZeroComponentCount"/>) -- the boolean form of the same per-component signal.</summary>
	public bool EvaluatedZeroControls => EvaluatedZeroComponentCount > 0;
}

/// <summary>
/// The full run-level rollup -- <c>GET /runs/{id}/component-results/summary</c>.
/// <see cref="PlannedComponentCount"/> is the plan's total accepted item count
/// (scan_plan_items row count for the run) so a caller can compute coverage
/// (<c>ByStatus</c> counts vs. planned) without a second request; a plan item with NO
/// component_results row at all (never claimed, still queued/running) is coverage
/// that simply is not yet reflected -- never fabricated as any status.
/// </summary>
public sealed record RunResultRollup(
	Guid RunId,
	int PlannedComponentCount,
	IReadOnlyList<RunResultRollupRow> ByStatus);

/// <summary>
/// The immutable header of one job's latest <c>component_results</c> attempt --
/// issue #745's finding-list/artifact read surfaces are job-scoped and always resolve
/// to this attempt (highest <c>attempt_number</c> for the job), mirroring
/// <see cref="IComponentResultRepository.GetRunRollupAsync"/>'s own "the latest attempt
/// supplies the current component result" rule (ADR-0024).
/// <see cref="OutputKind"/> (issue #743) is the FROZEN plan item's catalog
/// <c>output_kind</c>, joined at read time from <c>scan_plan_items</c> -- the
/// authoritative SRG-vs-STIG signal for this result (never the target's connection
/// kind); null only if the plan row was purged out from under the result.
/// </summary>
public sealed record ComponentResultHeader(
	Guid Id,
	Guid RunId,
	Guid JobId,
	Guid ScanPlanItemId,
	Guid ComponentId,
	int AttemptNumber,
	string Status,
	string? Detail,
	string? OutputKind = null);

/// <summary>
/// Issue #743 AC "SRG results clearly state they are not DISA-published STIG results":
/// the single shared statement the read APIs attach to any result whose frozen plan
/// item's catalog <c>output_kind</c> is <see cref="Waypoint.Core.ComplianceContent.CatalogOutputKinds.Hdf"/>
/// (an SRG closure -- epic #726 §6: "SRGs remain HDF-only unless a future exact STIG
/// mapping is introduced through the content workflow"). One constant so every surface
/// says exactly the same thing; derived from the FROZEN plan-item output kind, never
/// from the target's connection kind.
/// </summary>
public static class SrgResultStatements
{
	public const string NotDisaPublished =
		"SRG-derived results: generated from vendor SRG readiness content, not DISA-published STIG results. "
		+ "No CKL or STIG Manager upload is produced for this component.";
}

/// <summary>One persisted <c>component_result_findings</c> row read back, control identity and status intact and unaltered (epic #726 §6 -- findings pass through the API exactly as recorded, never re-derived or re-bucketed).</summary>
public sealed record ComponentResultFindingRecord(
	string ControlId,
	string? RuleId,
	string? Title,
	string Severity,
	string Status,
	string? Evidence);

/// <summary>One page of a job's latest-attempt findings -- limit/offset paged (bounded, single-attempt scope; not the growing-history cursor idiom <c>/runs/{id}/events/history</c> uses).</summary>
public sealed record ComponentResultFindingsPage(
	ComponentResultHeader? Result,
	IReadOnlyList<ComponentResultFindingRecord> Items,
	int TotalCount);

/// <summary>One persisted <c>component_result_artifacts</c> row read back -- metadata only (kind/path/digest/size), never the artifact bytes themselves.</summary>
public sealed record ComponentResultArtifactRecord(
	string Kind,
	string Path,
	string Digest,
	long SizeBytes);

/// <summary>A job's latest-attempt artifact metadata list -- always small (bounded by the closed <see cref="ComponentResultArtifactKinds"/> vocabulary), so no paging.</summary>
public sealed record ComponentResultArtifactsList(
	ComponentResultHeader? Result,
	IReadOnlyList<ComponentResultArtifactRecord> Items);
