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

namespace Waypoint.Core.ContentLibraries;

/// <summary>One folder together with every item Id assigned directly to it (never a descendant's items).</summary>
public sealed record ContentLibraryFolderWithItems(ContentLibraryFolder Folder, IReadOnlyList<Guid> ItemIds);

/// <summary>
/// Storage for the DB-only virtual folder tree over a content library's items
/// (migration 0113, issue #1389, epic #1185). One implementation
/// (<c>Waypoint.Infrastructure.ContentLibraries.ContentLibraryFolderRepository</c>,
/// plain Npgsql). Every structural mutation of one library's tree (create, rename,
/// move, delete, item assignment) runs inside a transaction that locks every folder
/// row of that library with <c>SELECT ... FOR UPDATE</c> before validating and
/// applying the change, the same "lock, then validate against the locked state"
/// shape <c>ConfigDocRepository</c>/<c>CapacityLeasePoolRepository</c> use elsewhere
/// in this codebase -- a move's cycle check (walking the proposed new parent's
/// ancestor chain) is only safe to trust if no concurrent move can restructure that
/// chain underneath it.
/// </summary>
public interface IContentLibraryFolderRepository
{
	/// <summary>Creates one folder. Nothing on disk is touched -- this is DB metadata only.</summary>
	Task<(ContentLibraryFolderCreateOutcome Outcome, ContentLibraryFolder? Folder)> CreateAsync(
		Guid libraryId, Guid? parentFolderId, string name, CancellationToken cancellationToken);

	/// <summary>Every folder in the library, flat (the API composes the tree from <see cref="ContentLibraryFolder.ParentFolderId"/>), each with its own directly-assigned item Ids.</summary>
	Task<IReadOnlyList<ContentLibraryFolderWithItems>> ListWithItemsAsync(Guid libraryId, CancellationToken cancellationToken);

	/// <summary>Looks up one folder by id, for the API's route-scoped library-ownership check before <see cref="UpdateAsync"/>/<see cref="DeleteAsync"/>.</summary>
	Task<ContentLibraryFolder?> GetAsync(Guid folderId, CancellationToken cancellationToken);

	/// <summary>
	/// Renames and/or moves a folder in one call -- both <paramref name="name"/> and
	/// <paramref name="parentFolderId"/> are always applied (a full replace of the
	/// folder's mutable fields, not a partial patch); callers wanting to change only
	/// one field must supply the other's current value. <paramref name="parentFolderId"/>
	/// of <c>null</c> moves the folder to the library root. Rejects a move that would
	/// place the folder under itself or one of its own descendants.
	/// </summary>
	Task<ContentLibraryFolderUpdateOutcome> UpdateAsync(
		Guid folderId, string name, Guid? parentFolderId, CancellationToken cancellationToken);

	/// <summary>Deletes a folder only when it has no child folder and no directly-assigned item (issue #1389 AC).</summary>
	Task<ContentLibraryFolderDeleteOutcome> DeleteAsync(Guid folderId, CancellationToken cancellationToken);

	/// <summary>
	/// Assigns <paramref name="itemId"/> to <paramref name="folderId"/>, replacing any
	/// prior assignment (single-parent: one folder per item). <paramref name="folderId"/>
	/// of <c>null</c> removes any existing assignment, returning the item to the
	/// library root -- there is no items table yet (#1396), so <paramref name="itemId"/>
	/// is accepted as given and not itself validated against anything.
	/// </summary>
	Task<ContentLibraryItemAssignmentOutcome> AssignItemAsync(
		Guid libraryId, Guid itemId, Guid? folderId, CancellationToken cancellationToken);
}
