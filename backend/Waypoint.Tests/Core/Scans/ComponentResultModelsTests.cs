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

using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Issue #1132: <see cref="RunResultRollupRow.EvaluatedZeroComponentCount"/> and its
/// boolean form <see cref="RunResultRollupRow.EvaluatedZeroControls"/> -- the
/// "ran, evaluated nothing" signal <c>GET /runs/{id}/component-results/summary</c>
/// reports. The count is produced PER COMPONENT by the rollup SQL, never re-derived
/// from this row's summed counts; these tests pin that the boolean follows the count
/// even when the sums look healthy (the mixed-bucket case).
/// </summary>
public sealed class ComponentResultModelsTests
{
	[Fact]
	public void EvaluatedZeroControls_AllNotReviewedNoPassedNoOpen_IsTrue()
	{
		// Round-12 shape: a bucket of components that all ran but every control
		// came back Not_Reviewed -- zero passed, zero open, at least one not-reviewed.
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 2, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 0, NotApplicableCount: 0, NotReviewedCount: 138, SkippedCount: 0,
			EvaluatedZeroComponentCount: 2);

		Assert.True(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_GenuineAllNotApplicable_IsFalse()
	{
		// A component whose controls are legitimately all not-applicable is NOT an
		// execution failure -- must not be conflated with the round-12 shape.
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 0, NotApplicableCount: 42, NotReviewedCount: 0, SkippedCount: 0,
			EvaluatedZeroComponentCount: 0);

		Assert.False(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_GenuineClean_IsFalse()
	{
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 10, NotApplicableCount: 1, NotReviewedCount: 0, SkippedCount: 0,
			EvaluatedZeroComponentCount: 0);

		Assert.False(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_HasOpenFindingsAlongsideNotReviewed_IsFalse()
	{
		// Some controls DID evaluate (one failed) -- not "evaluated nothing".
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 1, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 0, NotApplicableCount: 0, NotReviewedCount: 5, SkippedCount: 0,
			EvaluatedZeroComponentCount: 0);

		Assert.False(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_MixedBucket_HealthyLookingAggregate_IsTrue()
	{
		// The round-2 blind spot: 3 completed components, ONE of which evaluated
		// nothing (138 not_reviewed) while the other two evaluated normally. Summed,
		// the bucket looks healthy -- 90 passed, 4 open -- so any aggregate-only
		// predicate reads "fully evaluated". The per-component count is what tells
		// the truth.
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 3, CatIOpen: 2, CatIIOpen: 1, CatIIIOpen: 1,
			PassedCount: 90, NotApplicableCount: 0, NotReviewedCount: 138, SkippedCount: 0,
			EvaluatedZeroComponentCount: 1);

		Assert.True(row.EvaluatedZeroControls);
		Assert.Equal(1, row.EvaluatedZeroComponentCount);
	}

	/// <summary>Issue #1144: <see cref="ComponentResultRecord.ExecutionErrorCount"/> sums exactly the findings mapped to <see cref="ComponentFindingStatuses.ExecutionError"/>, the same "count of this status among Findings" shape every other computed count on the record already follows.</summary>
	[Fact]
	public void ComponentResultRecord_ExecutionErrorCount_CountsOnlyExecutionErrorFindings()
	{
		ComponentResultRecord record = new(
			RunId: Guid.NewGuid(), JobId: Guid.NewGuid(), ScanPlanItemId: Guid.NewGuid(), ComponentId: Guid.NewGuid(),
			AttemptNumber: 1, Status: ComponentResultStatuses.Completed, Detail: null,
			Findings:
			[
				new ComponentResultFinding("SV-1", null, null, ComponentFindingSeverities.CatI, ComponentFindingStatuses.ExecutionError, "invented error"),
				new ComponentResultFinding("SV-2", null, null, ComponentFindingSeverities.CatII, ComponentFindingStatuses.ExecutionError, "invented error"),
				new ComponentResultFinding("SV-3", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.Passed, null),
				new ComponentResultFinding("SV-4", null, null, ComponentFindingSeverities.CatI, ComponentFindingStatuses.Failed, "invented failure"),
			],
			Artifacts: []);

		Assert.Equal(2, record.ExecutionErrorCount);
		// Not folded into any other column: the errored findings are counted only by
		// ExecutionErrorCount, the genuinely-passed one only by PassedCount, the
		// genuinely-failed one only by CatIOpen -- no double counting.
		Assert.Equal(1, record.CatIOpen);
		Assert.Equal(1, record.PassedCount);
	}

	/// <summary>A component whose findings are ALL execution_error must still surface a non-zero rollup count -- issue #1144's core acceptance criterion, pinned at the model level.</summary>
	[Fact]
	public void RunResultRollupRow_AllExecutionErrorComponent_ExecutionErrorCountIsVisible()
	{
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 0, NotApplicableCount: 0, NotReviewedCount: 0, SkippedCount: 0,
			EvaluatedZeroComponentCount: 1, ExecutionErrorCount: 5);

		Assert.Equal(5, row.ExecutionErrorCount);
		Assert.True(row.EvaluatedZeroControls);
	}

	/// <summary>
	/// Issue #1140: <see cref="ComponentResultRecord.EvaluatedZeroControls"/> (and its
	/// static <see cref="ComponentResultRecord.EvaluatedZeroControlsFor"/> form, which
	/// <see cref="Waypoint.Infrastructure.Runs.ComponentResultRecordingService"/> calls
	/// at WRITE time) must apply the EXACT SAME predicate
	/// <see cref="IComponentResultRepository.GetRunRollupAsync"/>'s SQL FILTER already
	/// uses at read time -- one definition, not two hand-copied ones.
	/// </summary>
	[Theory]
	[InlineData(ComponentFindingStatuses.NotReviewed, true)]
	[InlineData(ComponentFindingStatuses.Skipped, true)]
	[InlineData(ComponentFindingStatuses.ExecutionError, true)]
	[InlineData(ComponentFindingStatuses.Passed, false)]
	public void ComponentResultRecord_EvaluatedZeroControls_MatchesRollupPredicate(string singleFindingStatus, bool expected)
	{
		ComponentResultRecord record = new(
			RunId: Guid.NewGuid(), JobId: Guid.NewGuid(), ScanPlanItemId: Guid.NewGuid(), ComponentId: Guid.NewGuid(),
			AttemptNumber: 1, Status: ComponentResultStatuses.Completed, Detail: null,
			Findings: [new ComponentResultFinding("SV-1", null, null, ComponentFindingSeverities.CatIII, singleFindingStatus, null)],
			Artifacts: []);

		Assert.Equal(expected, record.EvaluatedZeroControls);
	}

	/// <summary>Genuinely all-not-applicable is a determinate outcome, never reclassified as zero-evaluated -- the one carve-out both the write-time predicate and the read-time rollup FILTER share.</summary>
	[Fact]
	public void ComponentResultRecord_EvaluatedZeroControls_GenuineAllNotApplicable_IsFalse()
	{
		ComponentResultRecord record = new(
			RunId: Guid.NewGuid(), JobId: Guid.NewGuid(), ScanPlanItemId: Guid.NewGuid(), ComponentId: Guid.NewGuid(),
			AttemptNumber: 1, Status: ComponentResultStatuses.Completed, Detail: null,
			Findings: [new ComponentResultFinding("SV-1", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotApplicable, null)],
			Artifacts: []);

		Assert.False(record.EvaluatedZeroControls);
	}

	/// <summary>An execution-error finding MIXED with a not-applicable one is still zero-evaluated (issue #1144's own closed gap) -- not_applicable_count &gt; 0 alone must not mask it.</summary>
	[Fact]
	public void ComponentResultRecord_EvaluatedZeroControls_NotApplicableMixedWithExecutionError_IsTrue()
	{
		ComponentResultRecord record = new(
			RunId: Guid.NewGuid(), JobId: Guid.NewGuid(), ScanPlanItemId: Guid.NewGuid(), ComponentId: Guid.NewGuid(),
			AttemptNumber: 1, Status: ComponentResultStatuses.Completed, Detail: null,
			Findings:
			[
				new ComponentResultFinding("SV-1", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotApplicable, null),
				new ComponentResultFinding("SV-2", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.ExecutionError, "invented error"),
			],
			Artifacts: []);

		Assert.True(record.EvaluatedZeroControls);
	}
}
