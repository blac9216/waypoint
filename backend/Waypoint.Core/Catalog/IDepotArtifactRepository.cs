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

using Waypoint.Core.Pagination;

namespace Waypoint.Core.Catalog;

/// <summary>
/// Storage for the depot catalog (issue #193, epic #9 slice 1). One implementation
/// (<c>Waypoint.Infrastructure.Catalog.DepotArtifactRepository</c>, plain Npgsql --
/// same "no ORM for this layer" convention as <c>CredentialRepository</c> and
/// <c>JobQueueRepository</c>). The <c>catalog-index</c> handler that populates this
/// store via the real depot tool lands with issue #194; this slice proves the store
/// and the REST surface with invented fixtures.
/// </summary>
public interface IDepotArtifactRepository
{
	/// <summary>
	/// Inserts or updates by <see cref="DepotArtifactUpsert.RelativePath"/> -- migration
	/// 0100's (issue #1488) catalog-identity column -- the same identity twice yields
	/// one row with the newer payload (issue #193 acceptance criterion). Returns the
	/// row's id.
	/// </summary>
	Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken);

	/// <summary>
	/// Single-row lookup by id. Added for issue #1436 (the retention sweep), which
	/// resolves a <c>download_retained_content_state.depot_artifact_id</c> row to its
	/// <see cref="DepotArtifact.ExternalId"/> (the depot-relative path a purge deletes)
	/// -- no prior caller needed a by-id lookup, only the filtered/paginated
	/// <see cref="ListAsync"/>. Returns <c>null</c> when no row has that id.
	/// </summary>
	Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>
	/// Filtered, paginated list plus the filtered total (for <c>X-Total-Count</c> --
	/// the count reflects the filter, not the whole table, per every other paginated
	/// resource in this codebase).
	/// </summary>
	Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(
		DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #1784/#1818/#1804 reconciliation, batched: <paramref name="renames"/> is
	/// every candidate legacy-identity -&gt; new-identity pair a connected pull derives
	/// for the artifacts it just parsed (its keys are the pre-#1784 bare-fileName
	/// identity, its values the new depot-relative identity -- see
	/// <c>CatalogPullJobHandler</c>, the one caller). Issues a BOUNDED number of
	/// statements regardless of how many pairs are passed (#1818: the per-artifact
	/// shape this replaced issued one round trip per artifact on every pull, 1291 on
	/// the owner's live stack, even though the guard that triggered it fires on every
	/// artifact's identity SHAPE, not on whether a legacy row actually exists) --
	/// first one query for which of the candidate FROM identities actually have a
	/// non-superseded row (almost always none, once a stack's first post-#1784 pull
	/// has run), returning immediately with 0 if none do, then a bounded number of
	/// set-based statements that act only on the FROM identities that query found.
	/// For each such row: RENAMES it onto its TO identity in place if the TO identity
	/// has no row yet (never deletes -- design #16 section 2's never-auto-remove
	/// policy, the same policy <see cref="Waypoint.Core.Downloads.IReviewListService"/>'s
	/// own doc comment states and
	/// <c>ReviewListServiceTests.Interface_HasNoDeleteOrRemoveOrPurgeMethod</c>
	/// enforces structurally against every interface in this repository's dependency
	/// graph, this one included); or, if the TO identity already has a row (issue
	/// #1804's collision case -- the presence sweep created it before this pull ever
	/// ran), folds the legacy row's still-valid facts into the surviving TO row
	/// (COALESCE-only, never clobbering a fact the TO row already has -- the same
	/// convention <see cref="UpsertAsync"/> uses) and marks the legacy row
	/// <c>superseded_at</c> (migration 0134) rather than deleting it, matching the
	/// same never-auto-remove policy. Returns the total number of legacy rows
	/// reconciled (renamed or merged-and-superseded), across all pairs. Called BEFORE
	/// <see cref="UpsertAsync"/> so a rename has a row to act on before that upsert
	/// creates one at the TO identity itself.
	/// </summary>
	Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken);
}

/// <summary>
/// Storage for <see cref="UnknownCatalogFile"/> rows (migration 0100, issue #1488).
/// Insert-or-touch-last-seen only, by design (Q11: alert instead of drop) -- there is
/// deliberately no delete/remove method on this interface, enforced by
/// <c>UnknownCatalogFileRepositoryTests.Repository_HasNoDeleteOrRemoveMethod</c>.
/// Populated by the real presence sweep in #1503/#1512; this slice only proves the
/// storage shape.
/// </summary>
public interface IUnknownCatalogFileRepository
{
	/// <summary>
	/// Inserts a new unknown-file row, or -- if <paramref name="relativePath"/> was
	/// already seen -- updates its <c>size_bytes</c> and advances
	/// <c>last_seen_at</c> to now, leaving <c>first_seen_at</c> untouched. Returns the
	/// row's id.
	/// </summary>
	Task<Guid> RecordSeenAsync(string relativePath, long? sizeBytes, CancellationToken cancellationToken);

	/// <summary>Every unknown file ever seen, newest-last-seen first. No filter -- the read side is small and #1495's job.</summary>
	Task<IReadOnlyList<UnknownCatalogFile>> ListAsync(CancellationToken cancellationToken);
}
