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

/// <summary>Outcome of <see cref="IContentLibraryItemService"/> operations that can fail on a missing library or item.</summary>
public enum ContentLibraryItemOperationOutcome
{
	Succeeded,
	LibraryNotFound,
	ItemNotFound,
}

/// <summary>
/// The item CRUD operation layer (issue #1396, epic #1185): the piece that actually
/// makes a library non-empty and subscribable. Combines
/// <see cref="IContentLibraryItemRepository"/> (Waypoint's own item identity rows) with
/// <see cref="IContentLibraryWriter"/> (the VCSP wire documents, #1393) and the item's
/// file bytes on disk -- every mutation here is delegated through the writer so its
/// atomicity/version guarantees hold end to end (issue #1396's own "Risks" section).
/// One implementation (<c>Waypoint.Infrastructure.ContentLibraries.ContentLibraryItemService</c>).
/// <para>
/// Upload transport is deliberately NOT this interface's concern: every method takes
/// an already-opened <see cref="Stream"/> at the service boundary. A controller
/// resolving that stream from a multipart request, or a future depot-fed sync job
/// (#1055/#1057) resolving it from a staged file, are both equally valid callers.
/// </para>
/// </summary>
public interface IContentLibraryItemService
{
	/// <summary>
	/// Adds a new item: writes <paramref name="content"/> into a fresh, id-derived
	/// directory under the library's disk path, infers <see cref="ContentLibraryItem.Type"/>
	/// from <paramref name="fileName"/>'s extension, inserts the identity row, then
	/// re-lists the library's complete item set and republishes it through
	/// <see cref="IContentLibraryWriter"/> so <c>lib.json</c>/<c>items.json</c> reflect
	/// the addition atomically.
	/// </summary>
	Task<(ContentLibraryItemOperationOutcome Outcome, ContentLibraryItem? Item)> AddAsync(
		Guid libraryId, string fileName, Stream content, string? description, CancellationToken cancellationToken);

	/// <summary>
	/// Updates an existing item in place: reuses its existing <see cref="ContentLibraryItem.Id"/>
	/// and <see cref="ContentLibraryItem.DirectoryName"/> (issue #1396 AC -- never a new
	/// identity), overwrites its file with <paramref name="content"/>, increments
	/// <see cref="ContentLibraryItem.Version"/>, then republishes the library's complete
	/// item set the same way <see cref="AddAsync"/> does.
	/// </summary>
	Task<(ContentLibraryItemOperationOutcome Outcome, ContentLibraryItem? Item)> UpdateAsync(
		Guid libraryId, Guid itemId, string fileName, Stream content, string? description, CancellationToken cancellationToken);

	/// <summary>
	/// Removes an item: deletes its identity row (cascading its own folder assignment,
	/// migration 0133), deletes its directory from disk, then republishes the library's
	/// remaining item set so <c>lib.json.version</c> bumps and the removed item's
	/// <c>item.json</c>/<c>items.json</c> entry is gone.
	/// </summary>
	Task<ContentLibraryItemOperationOutcome> RemoveAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken);
}
