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

/// <summary>Which never-auto-removed category a <see cref="ReviewListEntry"/> belongs to.</summary>
public enum ReviewListEntryKind
{
	/// <summary>A file present on the depot with no matching catalog entry (<c>unknown_catalog_files</c>, migration 0100, issue #1488) -- no <c>depot_artifacts</c> row exists for it at all.</summary>
	Orphan,

	/// <summary>A catalogued, tracked <c>depot_artifacts</c> row a caller has identified as outside every subscription's scope (<c>download_out_of_scope_content</c>, migration 0128, this issue).</summary>
	OutOfScope,
}

/// <summary>
/// One review-list row -- either an <see cref="ReviewListEntryKind.Orphan"/> (no
/// <see cref="DepotArtifactId"/>, since orphans have no <c>depot_artifacts</c> row)
/// or an <see cref="ReviewListEntryKind.OutOfScope"/> entry (always has one).
/// </summary>
public sealed record ReviewListEntry(
	ReviewListEntryKind Kind,
	Guid? DepotArtifactId,
	string RelativePath,
	long? SizeBytes,
	string? Reason,
	DateTimeOffset FirstSeenAt,
	DateTimeOffset LastSeenAt);

/// <summary>
/// The review-list mechanism (issue #1440, epic #1182, split from design record
/// #1047, approved design #16 section 2: "orphans and out-of-scope content are
/// never auto-removed -- surfaced for explicit deletion only"). One implementation
/// (<c>Waypoint.Infrastructure.Downloads.ReviewListService</c>). Enumerates the
/// union of two never-auto-removed categories:
/// <list type="bullet">
/// <item>Orphans -- <see cref="Waypoint.Core.Catalog.IUnknownCatalogFileRepository"/>'s
/// existing, already-shipped storage (migration 0100, issue #1488), which itself has
/// no delete/remove method (design decision Q11: alert instead of drop).</item>
/// <item>Out-of-scope content -- <c>download_out_of_scope_content</c> (migration
/// 0128, this issue), reported via <see cref="ReportOutOfScopeAsync"/>.</item>
/// </list>
/// NO method on this interface, nor anything it depends on, performs a deletion --
/// the structural guarantee issue #1440's Risk note calls out ("consider a
/// structural test... rather than only behavioral tests"), proven by
/// <c>ReviewListServiceTests</c> via reflection over this type's dependency graph.
/// The deletion action itself is a future, explicit, Admin-gated endpoint --
/// issue #1453's API surface, not this type's.
///
/// Out-of-scope discovery itself needs a real Subscription entity that does not
/// exist yet (#1421 is still open), the same deferred-discovery seam
/// <c>RetentionSweepService</c>'s own doc comment documents for its candidate
/// list: <see cref="ReportOutOfScopeAsync"/> takes the depot-artifact id as an
/// explicit caller-supplied input rather than querying for out-of-scope content
/// itself, keeping the never-auto-removed guarantee reviewable today, independent
/// of that still-missing discovery mechanism. A real subscription-scope evaluator
/// calling this method is deferred to #1421 landing.
/// </summary>
public interface IReviewListService
{
	/// <summary>
	/// Every review-list entry -- every <see cref="Waypoint.Core.Catalog.UnknownCatalogFile"/>
	/// row plus every <c>download_out_of_scope_content</c> row, newest-last-seen
	/// first within each kind. Pure read; has no side effects (in particular, never
	/// raises an alert -- alerting happens once, on first report, in
	/// <see cref="ReportOutOfScopeAsync"/> and in
	/// <see cref="Waypoint.Core.Catalog.IUnknownCatalogFileRepository.RecordSeenAsync"/>,
	/// not on every read).
	/// </summary>
	Task<IReadOnlyList<ReviewListEntry>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Records <paramref name="depotArtifactId"/> as out-of-scope content for
	/// <paramref name="reason"/>. Insert-or-touch-last-seen, mirroring
	/// <see cref="Waypoint.Core.Catalog.IUnknownCatalogFileRepository.RecordSeenAsync"/>'s
	/// own idempotency contract: a repeat report of an already-known
	/// <paramref name="depotArtifactId"/> refreshes <c>last_seen_at</c> (and
	/// <paramref name="reason"/>) without raising a second alert; a genuinely new
	/// one raises <see cref="Waypoint.Core.Jobs.JobEventTypes.SystemNotice"/>
	/// (issue #1440 AC: "New review-list entries raise an alert").
	/// </summary>
	Task ReportOutOfScopeAsync(Guid depotArtifactId, string reason, CancellationToken cancellationToken);
}
