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

/// <summary>
/// One node of the DB-only virtual folder tree over a <see cref="ContentLibrary"/>'s
/// items (migration 0113, issue #1389, epic #1185). Never touches disk -- disk layout
/// is owned entirely by <see cref="IContentLibraryWriter"/>/item CRUD (#1396).
/// </summary>
/// <param name="Id">Stable identity, never reused.</param>
/// <param name="LibraryId">The owning <see cref="ContentLibrary"/>.</param>
/// <param name="ParentFolderId">Null for a root folder.</param>
/// <param name="Name">Unique among siblings (root folders are their own sibling group -- see migration 0113's header for why NULL-parent uniqueness needed its own index).</param>
/// <param name="CreatedAt">Row insert time.</param>
public sealed record ContentLibraryFolder(Guid Id, Guid LibraryId, Guid? ParentFolderId, string Name, DateTimeOffset CreatedAt);

/// <summary>Outcome of <see cref="IContentLibraryFolderRepository.CreateAsync"/>.</summary>
public enum ContentLibraryFolderCreateOutcome
{
	Created,
	LibraryNotFound,

	/// <summary><c>ParentFolderId</c> does not exist, or exists in a different library.</summary>
	ParentNotFound,

	/// <summary>Another folder already carries this name among the same siblings.</summary>
	NameTaken,
}

/// <summary>Outcome of <see cref="IContentLibraryFolderRepository.UpdateAsync"/> (rename and/or move).</summary>
public enum ContentLibraryFolderUpdateOutcome
{
	Updated,
	NotFound,

	/// <summary>The requested new parent does not exist, or exists in a different library.</summary>
	ParentNotFound,

	/// <summary>Another folder already carries this name among the new siblings.</summary>
	NameTaken,

	/// <summary>The requested move would place the folder under itself or one of its own descendants.</summary>
	CycleRejected,
}

/// <summary>Outcome of <see cref="IContentLibraryFolderRepository.DeleteAsync"/>.</summary>
public enum ContentLibraryFolderDeleteOutcome
{
	Deleted,
	NotFound,

	/// <summary>The folder still has at least one child folder or assigned item (issue #1389 AC: non-empty delete is rejected, not silently cascaded).</summary>
	NotEmpty,
}

/// <summary>Outcome of <see cref="IContentLibraryFolderRepository.AssignItemAsync"/>.</summary>
public enum ContentLibraryItemAssignmentOutcome
{
	Assigned,

	/// <summary>The item had a folder assignment which was removed (<c>folderId: null</c>), returning it to the library root.</summary>
	Unassigned,

	LibraryNotFound,

	/// <summary>The requested folder does not exist, or exists in a different library.</summary>
	FolderNotFound,

	/// <summary>
	/// The requested item does not exist in this library (migration 0133, issue #1396:
	/// <c>content_library_item_folders.item_id</c> now carries a real FK onto
	/// <c>content_library_items</c>, closing the gap 0113 shipped with none). Only
	/// reachable on a non-null <c>folderId</c> assign -- unassigning (<c>folderId: null</c>)
	/// never needs the item to exist, since it only ever deletes a row that may or may
	/// not be there.
	/// </summary>
	ItemNotFound,
}
