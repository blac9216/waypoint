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

namespace Waypoint.Core.Downloads;

/// <summary>
/// The one, explicit, Admin-gated removal path for a <see cref="ReviewListEntry"/>
/// (issue #1453, epic #1182, approved design #16 section 2: "orphans and out-of-scope
/// content are never auto-removed -- surfaced for explicit deletion only"). Deliberately
/// NOT a method on <see cref="IReviewListService"/> itself: that interface's own
/// structural test (<c>ReviewListServiceTests.Interface_HasNoDeleteOrRemoveOrPurgeMethod</c>)
/// enforces that nothing on the automatic sweep/report path can ever delete -- this is
/// a separate, narrower seam an API-process caller reaches only through an explicit
/// human action, never from the sweep or the review-list report path. One
/// implementation (<c>Waypoint.Infrastructure.Downloads.ReviewListDeletionService</c>).
/// </summary>
public interface IReviewListDeletionService
{
	/// <summary>
	/// Deletes exactly one review-list entry and its underlying depot file. For
	/// <see cref="ReviewListEntryKind.OutOfScope"/>, <paramref name="depotArtifactId"/>
	/// is required and the deletion reuses <see cref="IRetentionSweepService.PurgeImmediatelyAsync"/>
	/// (tracking the artifact first via <see cref="IRetainedContentStateRepository.EnsureTrackedAsync"/>
	/// if it was never evaluated) so the same transition graph, path-confinement, and
	/// per-file logged deletion trail apply. For <see cref="ReviewListEntryKind.Orphan"/>,
	/// <paramref name="relativePath"/> is required (orphans have no <c>depot_artifacts</c>
	/// row to key off) and the file is deleted directly, with the same path-confinement
	/// defense and per-file log entry. Either way, the corresponding review-list row
	/// (<c>download_out_of_scope_content</c> or <c>unknown_catalog_files</c>) is removed
	/// only once the underlying file delete itself reports success (including the
	/// idempotent already-absent-file case) -- a failed delete leaves the review-list
	/// entry in place so it remains visible and retryable, the same "never lose track
	/// of an incomplete deletion" contract <see cref="IRetentionSweepService"/>'s own
	/// purge path establishes.
	/// </summary>
	Task<ReviewListDeletionOutcome> DeleteAsync(
		ReviewListEntryKind kind,
		Guid? depotArtifactId,
		string? relativePath,
		string actor,
		string? reason,
		CancellationToken cancellationToken);
}

/// <summary>One <see cref="IReviewListDeletionService.DeleteAsync"/> outcome.</summary>
public sealed record ReviewListDeletionOutcome(bool Deleted, string? Error);
