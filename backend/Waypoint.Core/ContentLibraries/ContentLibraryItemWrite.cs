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
/// One file <see cref="IContentLibraryWriter"/> is told to describe for an item.
/// Carries no bytes and moves nothing on disk -- the file is assumed already present
/// at <c>&lt;library disk path&gt;/&lt;item directory&gt;/&lt;Name&gt;</c> by whatever
/// wrote it there (item CRUD, issue #1396); this writer only emits the JSON metadata
/// describing it.
/// </summary>
/// <param name="Name">Bare filename (no directory component).</param>
/// <param name="Size">Exact byte length.</param>
/// <param name="ContentHash">
/// Caller-supplied content digest (e.g. the file's own SHA-256, matching decision 8's
/// always-self-hash rule) used ONLY to detect whether the item changed since the last
/// write -- never emitted verbatim on the wire. See <see cref="ItemFileJson.Etag"/> for
/// what actually goes in <c>item.json</c>.
/// </param>
public sealed record ContentLibraryItemFileWrite(string Name, long Size, string ContentHash);

/// <summary>
/// One item <see cref="IContentLibraryWriter.WriteAsync"/> is told to publish. The full
/// set passed on each call is the library's ENTIRE desired item state -- this writer
/// always does a full-library rewrite, computing version/etag deltas against whatever
/// it finds already on disk (see <c>VcspContentLibraryWriter</c>), never an incremental
/// patch. An item CRUD caller (#1396) supplies this by re-listing every item it wants
/// published, including ones unchanged since the last write.
/// </summary>
/// <param name="Id">Stable identity across writes -- carrying the SAME <see cref="Id"/> on a later call is what lets this writer recognize "this is the same item, has its content changed" rather than treating it as a delete-then-add.</param>
/// <param name="DirectoryName">
/// Single path segment, the item's own directory directly under the library's disk
/// root (research #1032: flat, one level -- never nested). Validated the same way
/// <c>ContentLibraryRepository.ResolveDiskPath</c> validates a library name: rejects
/// <c>.</c>/<c>..</c>/separators/absolute paths before it is ever combined with a real
/// filesystem path.
/// </param>
/// <param name="Name">Operator-facing item name, written verbatim into <see cref="ItemJson.Name"/>.</param>
/// <param name="Type">One of <see cref="ContentLibraryItemTypes.All"/>.</param>
/// <param name="Description">Free-form; empty string when the caller supplies none.</param>
/// <param name="Files">Every file the item carries. An item with zero files is rejected -- an empty item directory has nothing for a subscriber to fetch and is very likely a caller bug, not a legitimate item.</param>
public sealed record ContentLibraryItemWrite(
	Guid Id,
	string DirectoryName,
	string Name,
	string Type,
	string Description,
	IReadOnlyList<ContentLibraryItemFileWrite> Files);
