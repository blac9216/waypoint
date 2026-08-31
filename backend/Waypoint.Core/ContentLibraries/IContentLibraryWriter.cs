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
/// Writes the VCSP protocol documents (<c>lib.json</c>, <c>items.json</c>, and every
/// item's own <c>item.json</c>) for one <see cref="ContentLibrary"/> into its disk path
/// (issue #1393, epic #1185, design record #16 section 6, research #1032). This is the
/// protocol-correctness layer only: it never moves item file bytes (that is item CRUD,
/// #1396) and never touches <see cref="ContentLibrary"/> rows or directories
/// themselves (that is the registry, #1391) -- it consumes both as given and emits
/// exactly the three JSON document shapes vCenter's VCSP subscription client parses.
/// One implementation, <c>Waypoint.Infrastructure.ContentLibraries.VcspContentLibraryWriter</c>.
/// </summary>
public interface IContentLibraryWriter
{
	/// <summary>
	/// Rewrites every VCSP document for <paramref name="library"/> so it reflects
	/// exactly <paramref name="items"/> -- a full-library rewrite, not an incremental
	/// patch. Diffs the desired state against whatever this writer previously left on
	/// disk (or a fresh library if nothing is there yet) to decide the two version
	/// counters correctly (research #1032, NOT the sibling's inverted behavior):
	/// <c>lib.json.version</c> increments once if any item was added, removed, or had
	/// its content change; each item's own <c>version</c> increments only when that
	/// specific item's content changed, while its <c>id</c>/<c>created</c> stay put.
	/// <c>lib.json.contentVersion</c> never moves.
	/// <para>
	/// Every document this call produces -- each item's <c>item.json</c>,
	/// <c>items.json</c>, and finally <c>lib.json</c> -- is written via a same-directory
	/// temp file plus atomic rename, in that order, so a concurrent VCSP subscriber
	/// never observes a partially written document, and never observes a bumped
	/// <c>lib.json.version</c> before the <c>items.json</c>/<c>item.json</c> it points
	/// at are already fully in place.
	/// </para>
	/// </summary>
	/// <param name="library">The library whose disk path is rewritten.</param>
	/// <param name="items">The library's complete desired item set -- items previously published but omitted here keep whatever files/`item.json` are already on disk (deleting an item's directory is item CRUD's job, #1396) but are dropped from `items.json`/`lib.json`'s item list.</param>
	/// <param name="cancellationToken">Cancelling before a document's rename leaves that document exactly as it was found -- see the type-level remarks on atomicity.</param>
	Task WriteAsync(ContentLibrary library, IReadOnlyList<ContentLibraryItemWrite> items, CancellationToken cancellationToken);
}
