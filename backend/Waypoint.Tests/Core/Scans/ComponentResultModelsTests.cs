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
/// Issue #1132: <see cref="RunResultRollupRow.EvaluatedZeroControls"/> -- the
/// run-rollup-level "ran, evaluated nothing" signal, aggregated from the same
/// per-component counts <c>GET /runs/{id}/component-results/summary</c> already sums.
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
			PassedCount: 0, NotApplicableCount: 0, NotReviewedCount: 138, SkippedCount: 0);

		Assert.True(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_GenuineAllNotApplicable_IsFalse()
	{
		// A component whose controls are legitimately all not-applicable is NOT an
		// execution failure -- must not be conflated with the round-12 shape.
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 0, NotApplicableCount: 42, NotReviewedCount: 0, SkippedCount: 0);

		Assert.False(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_GenuineClean_IsFalse()
	{
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 10, NotApplicableCount: 1, NotReviewedCount: 0, SkippedCount: 0);

		Assert.False(row.EvaluatedZeroControls);
	}

	[Fact]
	public void EvaluatedZeroControls_HasOpenFindingsAlongsideNotReviewed_IsFalse()
	{
		// Some controls DID evaluate (one failed) -- not "evaluated nothing".
		RunResultRollupRow row = new(
			Status: "completed", ComponentCount: 1, CatIOpen: 1, CatIIOpen: 0, CatIIIOpen: 0,
			PassedCount: 0, NotApplicableCount: 0, NotReviewedCount: 5, SkippedCount: 0);

		Assert.False(row.EvaluatedZeroControls);
	}
}
