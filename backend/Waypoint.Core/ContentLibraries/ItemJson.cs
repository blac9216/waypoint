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
/// Closed VCSP wire vocabulary for <see cref="ItemJson.Type"/> (research #1032 Q3).
/// There is deliberately no <c>vcsp.vm-template</c> -- native VM templates have no
/// file-based VCSP equivalent and can never be served by a third-party publisher; that
/// is a permanent parity boundary, not an omission.
/// </summary>
public static class ContentLibraryItemTypes
{
	public const string Ovf = "vcsp.ovf";
	public const string Iso = "vcsp.iso";
	public const string Other = "vcsp.other";

	public static readonly IReadOnlyList<string> All = [Ovf, Iso, Other];
}

/// <summary>
/// Wire shape of one item, written both as an entry inside <c>items.json</c> and
/// standalone as <c>&lt;item-dir&gt;/item.json</c> -- the reference publisher writes
/// the identical object both places, and this writer does the same (issue #1393).
/// </summary>
/// <param name="Created">Immutable once assigned -- stable across every republish as long as <see cref="Id"/> is unchanged, even when <see cref="Version"/> advances.</param>
/// <param name="Description">Free-form; empty string when the caller supplies none.</param>
/// <param name="Version">
/// Item-local content counter (distinct from <see cref="LibJson.Version"/>): increments
/// only when this item's own file content changes, and only then -- an unrelated
/// change elsewhere in the library never bumps it. <see cref="Id"/> and
/// <see cref="Created"/> stay put across the bump.
/// </param>
/// <param name="Id"><c>urn:uuid:</c>-prefixed, immutable identity. A subscriber treats a changed <see cref="Id"/> as delete-then-add, so this writer never reissues one for an item it has already published.</param>
/// <param name="Name">Operator-facing item name.</param>
/// <param name="SelfHref">Library-root-relative path to this item's own <c>item.json</c> (research #1032: NOT a bare filename -- the sibling's defect this writer does not repeat).</param>
/// <param name="Files">One entry per file the item carries.</param>
/// <param name="Type">One of <see cref="ContentLibraryItemTypes.All"/>.</param>
/// <param name="Properties">Always an empty object -- reserved by the wire format, unused by this writer.</param>
public sealed record ItemJson(
	string Created,
	string Description,
	string Version,
	string Id,
	string Name,
	string SelfHref,
	IReadOnlyList<ItemFileJson> Files,
	string Type,
	IReadOnlyDictionary<string, object> Properties);

/// <summary>
/// One file inside an item's <see cref="ItemJson.Files"/> list.
/// </summary>
/// <param name="Name">Bare filename (no directory component).</param>
/// <param name="Size">Exact byte length -- research #1032 Q3: the subscriber uses this for progress/provisioning and a mismatch against the served <c>Content-Length</c> is a sync failure.</param>
/// <param name="Etag">
/// Opaque per-item change token (research #1032 "etag semantics"): NOT a per-file
/// content hash and unrelated to the HTTP <c>ETag</c> header -- the reference
/// publisher and the vendor's own library both give every file in an item the SAME
/// value (an item-directory-level digest). This writer follows that convention with a
/// SHA-256-derived token instead of the reference's MD5, per decision 8's
/// always-self-hash rule; the only real contract is "changes when the content changes".
/// </param>
/// <param name="Hrefs">Library-root-relative paths that resolve the file from the library root (research #1032: NOT bare filenames -- the sibling's other defect this writer does not repeat). Always exactly one entry: this writer never republishes the same file under more than one path.</param>
public sealed record ItemFileJson(string Name, long Size, string Etag, IReadOnlyList<string> Hrefs);
