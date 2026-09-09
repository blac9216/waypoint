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
/// One durable item row for a <see cref="ContentLibrary"/> (migration 0133, issue
/// #1396, epic #1185): the identity <see cref="IContentLibraryItemService"/> tracks so
/// it can supply <see cref="IContentLibraryWriter"/> the library's ENTIRE desired item
/// set on every write (the writer's own full-rewrite contract). This is Waypoint's own
/// record, distinct from the VCSP wire documents (<c>item.json</c>/<c>items.json</c>)
/// the writer produces from it -- <see cref="Version"/> here is a Waypoint-side
/// monotonic counter that increments on every explicit update call, not the writer's
/// own content-hash-diffed <c>item.json</c> version (see
/// <see cref="IContentLibraryItemService"/>'s remarks for why the two are allowed to
/// diverge on a no-op update).
/// </summary>
/// <param name="Id">Stable identity, reused across an update -- never reissued while the item exists.</param>
/// <param name="LibraryId">The owning <see cref="ContentLibrary"/>.</param>
/// <param name="DirectoryName">
/// Single path segment, always <c>Id</c> in <c>"N"</c> format (32 hex characters, no
/// separators) -- derived, never operator-supplied, so it needs no path-traversal
/// validation of its own the way a freely-chosen name would.
/// </param>
/// <param name="Name">Operator-facing item name, taken from the uploaded file's own name.</param>
/// <param name="Type">One of <see cref="ContentLibraryItemTypes.All"/>, inferred from <see cref="Files"/>' extension without operator input.</param>
/// <param name="Description">Free-form; empty string when the caller supplies none.</param>
/// <param name="Version">Waypoint-side counter: 1 on add, incremented by 1 on every explicit update.</param>
/// <param name="Files">This slice's scope is exactly one file per item (see migration 0133's header); the writer's own contract supports more, kept as a list for that future without a breaking schema change.</param>
/// <param name="CreatedAt">Row insert time.</param>
/// <param name="UpdatedAt">Row's own <c>updated_at</c> trigger value.</param>
public sealed record ContentLibraryItem(
	Guid Id,
	Guid LibraryId,
	string DirectoryName,
	string Name,
	string Type,
	string Description,
	long Version,
	IReadOnlyList<ContentLibraryItemFileWrite> Files,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>Outcome of <see cref="IContentLibraryItemRepository.AddAsync"/>.</summary>
public enum ContentLibraryItemAddOutcome
{
	Added,
	LibraryNotFound,
}

/// <summary>Outcome of <see cref="IContentLibraryItemRepository.UpdateAsync"/>.</summary>
public enum ContentLibraryItemUpdateOutcome
{
	Updated,

	/// <summary>No item with this id exists in this library.</summary>
	NotFound,
}

/// <summary>Outcome of <see cref="IContentLibraryItemRepository.RemoveAsync"/>.</summary>
public enum ContentLibraryItemRemoveOutcome
{
	Removed,

	/// <summary>No item with this id exists in this library.</summary>
	NotFound,
}
