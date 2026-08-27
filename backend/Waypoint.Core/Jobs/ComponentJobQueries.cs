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

namespace Waypoint.Core.Jobs;

/// <summary>
/// Issue #757 (epic #726 §7, ADR-0024 "At 10,000 or more component jobs it obtains
/// server-side grouped counts and bounded cursor-paged/searchable rows"): the
/// run-scoped component-job read surface behind the Live Run state board's grouped
/// priority/kind/state counters and virtualized, searchable component list. Both
/// queries are pure SQL <c>GROUP BY</c>/keyset-paged reads over the existing
/// <c>jobs</c> table (LEFT JOIN <c>scan_plan_items</c> for the frozen catalog
/// <c>selector_kind</c> -- the closed component-kind vocabulary, migration 0050's
/// <c>CatalogSelectorKinds</c>) -- there is no in-memory aggregation of a run's full
/// job set anywhere on this path, which is the whole point: a 10,000+-job run must
/// never require pulling every row into API or browser memory just to answer "how
/// many are queued at priority 3."
///
/// A job with no <c>scan_plan_item_id</c> (the legacy per-target scan fan-out, or a
/// non-scan job type) reports <see cref="ComponentJobRow.ComponentKind"/> as
/// <see cref="UnknownComponentKind"/> rather than being silently dropped from counts
/// or the paged list -- ADR-0024's scale contract is additive over the existing job
/// engine, not a second one that only understands narrowed component jobs.
/// </summary>
public static class ComponentKindVocabulary
{
	/// <summary>
	/// Reported <see cref="ComponentJobRow.ComponentKind"/> for a job with no
	/// <c>scan_plan_item_id</c> (legacy per-target fan-out, or a non-scan job type) --
	/// distinct from any real <c>CatalogSelectorKinds</c> value so a caller can tell
	/// "this job predates/bypasses component-granular planning" from "this job's
	/// component kind is genuinely unset."
	/// </summary>
	public const string Unknown = "unknown";
}

/// <summary>
/// Optional filters shared by <see cref="IComponentJobRepository.GetGroupedCountsAsync"/>
/// and <see cref="IComponentJobRepository.ListComponentJobsAsync"/> -- every field is an
/// allow-list (null/empty means "no filter"), same convention as
/// <see cref="RunHistoryQuery"/>/<see cref="JobEventHistoryQuery"/>.
/// </summary>
public sealed record ComponentJobFilter(
	IReadOnlyList<string>? States,
	IReadOnlyList<short>? Priorities,
	IReadOnlyList<string>? ComponentKinds,
	string? Search);

/// <summary>
/// One grouped-count row: the number of a run's component jobs sharing this exact
/// (priority, component_kind, state) triple. The state board sums/filters these
/// client-side to render per-priority totals and per-state breakdowns without ever
/// requesting individual job rows for the count view.
/// </summary>
public sealed record ComponentJobCountRow(short Priority, string ComponentKind, string State, long Count);

/// <summary>
/// One page of a run's component jobs for the virtualized, searchable list. Mirrors
/// <see cref="JobSummary"/>'s job fields plus the frozen <c>selector_kind</c>
/// (<see cref="ComponentKind"/>) grouped counts key on.
/// </summary>
public sealed record ComponentJobRow(
	Guid Id,
	string JobType,
	string? TargetId,
	string? TargetName,
	string State,
	string? Stage,
	short Priority,
	string ComponentKind,
	int AttemptCount,
	string? CreatedAt,
	string? StartedAt,
	string? FinishedAt);

/// <summary>
/// Keyset cursor position for <see cref="IComponentJobRepository.ListComponentJobsAsync"/>
/// -- <c>(priority, created_at, id)</c>, matching the list's stable
/// <c>ORDER BY priority, created_at, id</c> (same "the tie-break column travels in the
/// cursor too" rule <see cref="RunHistoryCursor"/>'s doc comment establishes, extended
/// with the extra <c>priority</c> leg since this list's primary sort key is priority,
/// not <c>created_at</c>).
/// </summary>
public sealed record ComponentJobCursorPosition(short Priority, DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Page request for <see cref="IComponentJobRepository.ListComponentJobsAsync"/>.
/// <see cref="After"/> is the decoded cursor from a previous page's
/// <see cref="ComponentJobPage.NextCursor"/> (null for the first page).
/// </summary>
public sealed record ComponentJobListQuery(
	Guid RunId,
	ComponentJobFilter Filter,
	ComponentJobCursorPosition? After,
	int Limit);

/// <summary>
/// One page of <see cref="IComponentJobRepository.ListComponentJobsAsync"/> results.
/// <see cref="NextCursor"/> is null exactly when this page reached the end of the
/// filtered set -- never a silent truncation, same contract every other paged reader
/// in this codebase uses.
/// </summary>
public sealed record ComponentJobPage(IReadOnlyList<ComponentJobRow> Items, ComponentJobCursorPosition? NextCursor);

/// <summary>
/// The run-scoped component-job read surface (issue #757). One implementation,
/// <c>Waypoint.Infrastructure.Jobs.JobQueueRepository</c>, alongside
/// <see cref="IJobControlRepository"/> -- these reads need no control-plane
/// write access and are split into their own interface only so a future runner-side
/// read path (if one is ever needed) does not have to depend on the control/write
/// surface to get it.
/// </summary>
public interface IComponentJobRepository
{
	/// <summary>
	/// Server-side <c>GROUP BY priority, component_kind, state</c> counts for a run's
	/// component jobs, honoring <see cref="ComponentJobFilter.Search"/> when set (same
	/// <c>target_name ILIKE</c> predicate <see cref="ListComponentJobsAsync"/> uses,
	/// so the state board's counters and the list they gate always agree on the
	/// candidate row set). No pagination -- the catalog's priority/kind/state
	/// vocabularies are all small closed sets, so the result set is bounded by
	/// vocabulary size, never by job count, no matter how many jobs a run has.
	/// </summary>
	Task<IReadOnlyList<ComponentJobCountRow>> GetGroupedCountsAsync(Guid runId, ComponentJobFilter filter, CancellationToken cancellationToken);

	/// <summary>
	/// Cursor-paged, filtered, searchable component-job rows for a run, ordered
	/// <c>priority, created_at, id</c> (ascending) -- stable under concurrent inserts
	/// because every leg of the tie-break is monotonic/unique. <see cref="ComponentJobFilter.Search"/>
	/// matches <c>target_name ILIKE '%term%'</c> (the same human-readable identity the
	/// board already displays -- issue #757 does not add a separate search index in
	/// this slice; see the PR body for the stated remainder if `target_name` search
	/// proves too slow at real 10,000+ scale).
	/// </summary>
	Task<ComponentJobPage> ListComponentJobsAsync(ComponentJobListQuery query, CancellationToken cancellationToken);
}
