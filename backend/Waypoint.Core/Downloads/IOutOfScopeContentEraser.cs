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
/// The one seam allowed to remove a <c>download_out_of_scope_content</c> row --
/// deliberately NOT a method on <see cref="IReviewListService"/> itself, which
/// structurally cannot delete (its never-deletes guarantee is asserted by
/// <c>ReviewListServiceTests.Interface_HasNoDeleteOrRemoveOrPurgeMethod</c> and is
/// deliberate: nothing on the automatic sweep/report path may ever delete).
///
/// Exists for one caller: <c>Waypoint.Infrastructure.Downloads.RetentionSweepService</c>'s
/// own purge path, which calls <see cref="EraseAsync"/> once a
/// <c>depot_artifacts</c> row's content has actually been purged, so a
/// <c>download_out_of_scope_content</c> row naming that same artifact does not
/// survive the purge and leave the admin review list offering a "delete out-of-scope
/// content" action against content that no longer exists (issue #1862).
///
/// Deliberately distinct from <see cref="IReviewListDeletionService"/>, which is the
/// explicit, Admin-actor-gated removal path reachable from the API surface and
/// itself calls back into <see cref="IRetentionSweepService.PurgeImmediatelyAsync"/>
/// to do the purge -- wiring <c>RetentionSweepService</c> through that service
/// instead would be a circular dependency, since <c>RetentionSweepService</c> IS
/// <see cref="IRetentionSweepService"/>'s own implementation. One implementation
/// (<c>Waypoint.Infrastructure.Downloads.ReviewListService</c>, the same type that
/// owns the table via <see cref="IReviewListService"/>).
/// </summary>
public interface IOutOfScopeContentEraser
{
	/// <summary>
	/// Removes <paramref name="depotArtifactId"/>'s <c>download_out_of_scope_content</c>
	/// row, if any -- a no-op, not an error, when there is none (the common case: most
	/// purged rows were never out-of-scope-reported at all). Callers invoke this only
	/// AFTER the underlying depot content has itself been purged.
	/// </summary>
	Task EraseAsync(Guid depotArtifactId, CancellationToken cancellationToken);
}
