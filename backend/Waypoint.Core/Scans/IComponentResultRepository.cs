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
/// Storage for the immutable <c>component_results</c>/<c>component_result_findings</c>/
/// <c>component_result_artifacts</c> tables (migration 0063, issue #745). There is no
/// UPDATE or DELETE method anywhere in this interface -- a result is a historical fact
/// about one job attempt, written exactly once.
/// </summary>
public interface IComponentResultRepository
{
	/// <summary>
	/// Persists <paramref name="record"/> and its findings/artifacts in one transaction.
	/// <see cref="ComponentResultRecord.AttemptNumber"/> is caller-assigned (mirrors
	/// migration 0062's upload_attempts convention: COUNT(*) + 1 under the same
	/// connection, benign races only produce a harmless gap/duplicate ordinal, never a
	/// lost row -- <c>component_results_unique_job_attempt</c> is the actual safety net
	/// for a genuine duplicate write).
	/// </summary>
	Task RecordAsync(ComponentResultRecord record, CancellationToken cancellationToken);

	/// <summary>The next 1-based attempt_number for <paramref name="jobId"/> (COUNT(*) + 1).</summary>
	Task<int> NextAttemptNumberAsync(Guid jobId, CancellationToken cancellationToken);

	/// <summary>The frozen <c>component_id</c> a <c>scan_plan_items</c> row names, or null when the item does not exist (defensive -- should not happen for a job carrying a live <c>scan_plan_item_id</c> FK).</summary>
	Task<Guid?> GetComponentIdForPlanItemAsync(Guid scanPlanItemId, CancellationToken cancellationToken);

	/// <summary>
	/// The run-level rollup for <c>GET /runs/{id}/component-results/summary</c>: counts
	/// by component-result status (latest attempt per scan_plan_item only) plus the
	/// plan's total accepted item count for coverage math. Pure SQL GROUP BY -- never
	/// loads the full result/finding set into memory (issue #941's grouped-counts
	/// idiom).
	/// </summary>
	Task<RunResultRollup> GetRunRollupAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// A page of a job's LATEST-attempt findings (issue #745), paged by the bounded
	/// <c>limit</c>/<c>offset</c> idiom (a single attempt's finding count is bounded by
	/// one benchmark's control count, never an unboundedly growing history -- unlike
	/// <c>/runs/{id}/events/history</c>'s cursor paging). <see cref="ComponentResultFindingsPage.Result"/>
	/// is null when the job has no recorded attempt at all (honest-empty, distinct from
	/// "attempt exists, zero findings").
	/// </summary>
	Task<ComponentResultFindingsPage> GetLatestFindingsAsync(Guid jobId, int limit, int offset, CancellationToken cancellationToken);

	/// <summary>
	/// Artifact metadata (kind/path/digest/size) for a job's LATEST attempt (issue
	/// #745). Never streams bytes -- this is a metadata-only read; byte download stays
	/// on the existing <c>GET /jobs/{id}/artifacts/{kind}</c> route. Unpaged: bounded by
	/// the closed <see cref="ComponentResultArtifactKinds"/> vocabulary (5 kinds max per
	/// attempt).
	/// </summary>
	Task<ComponentResultArtifactsList> GetLatestArtifactsAsync(Guid jobId, CancellationToken cancellationToken);
}
