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
/// Storage for the durable item identity table (migration 0133, issue #1396, epic
/// #1185). One implementation
/// (<c>Waypoint.Infrastructure.ContentLibraries.ContentLibraryItemRepository</c>, plain
/// Npgsql). This repository is DB-row bookkeeping only -- it never touches the
/// filesystem or the VCSP wire documents; <see cref="IContentLibraryItemService"/> is
/// the layer that combines this with <see cref="IContentLibraryWriter"/> and disk I/O.
/// </summary>
public interface IContentLibraryItemRepository
{
	/// <summary>
	/// Inserts one item row with <see cref="ContentLibraryItem.Version"/> 1.
	/// <paramref name="directoryName"/> is caller-derived (always the new item's own id
	/// in <c>"N"</c> format -- see <see cref="ContentLibraryItem"/>'s remarks), not
	/// generated here, so the caller can create the matching disk directory with the
	/// same name before or after this call.
	/// </summary>
	Task<(ContentLibraryItemAddOutcome Outcome, ContentLibraryItem? Item)> AddAsync(
		Guid id, Guid libraryId, string directoryName, string name, string type, string description,
		IReadOnlyList<ContentLibraryItemFileWrite> files, CancellationToken cancellationToken);

	/// <summary>Looks up one item by id, scoped to a library so a caller cannot accidentally act on another library's item via a route mismatch.</summary>
	Task<ContentLibraryItem?> GetAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken);

	/// <summary>Every item in the library, in no particular order -- the full desired-state set <see cref="IContentLibraryWriter.WriteAsync"/> requires on every call.</summary>
	Task<IReadOnlyList<ContentLibraryItem>> ListAsync(Guid libraryId, CancellationToken cancellationToken);

	/// <summary>
	/// Updates name/type/description/files in place and increments <see cref="ContentLibraryItem.Version"/>
	/// by 1 -- <see cref="ContentLibraryItem.Id"/> and <see cref="ContentLibraryItem.DirectoryName"/>
	/// never change (issue #1396 AC: an update keeps the item's identity, it never
	/// creates a new one).
	/// </summary>
	Task<ContentLibraryItemUpdateOutcome> UpdateAsync(
		Guid libraryId, Guid itemId, string name, string type, string description,
		IReadOnlyList<ContentLibraryItemFileWrite> files, CancellationToken cancellationToken);

	/// <summary>
	/// Deletes one item row. The FK from <c>content_library_item_folders.item_id</c>
	/// (migration 0133) is <c>ON DELETE CASCADE</c>, so any folder assignment for this
	/// item is dropped automatically -- this call never needs to touch that table
	/// itself.
	/// </summary>
	Task<ContentLibraryItemRemoveOutcome> RemoveAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken);
}
