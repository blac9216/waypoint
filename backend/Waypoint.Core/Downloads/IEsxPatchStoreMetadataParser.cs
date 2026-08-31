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

/// <summary>
/// Parses the content of an ESX patch store's <c>hostupdate/</c> tree -- the
/// consolidated index, each vendor's consolidated metadata index, its metadata zips,
/// and the VIB references inside them -- into a content-identified
/// <see cref="EsxPatchStoreMetadata"/> model. This is the correctness foundation
/// split out of design record #38/epic #16 §4 (issue #1446): the DB index/discrepancy
/// job (#1447), self-healing sync (#1451), and rebuild/removal UI (#1452) all consume
/// this parser rather than re-reading the store themselves.
///
/// Research #1028 confirmed the on-disk wire format is byte-compatible from UMDS
/// 8.0.3 through VCFDT 9.1 -- one parser serves every generation -- with three
/// generation-specific traps this parser exists to handle correctly:
/// <list type="bullet">
/// <item>The store root differs by generation (<see cref="EsxPatchStoreLayout"/>);
/// both are auto-detected from <paramref name="storeRoot"/>, or a specific layout may
/// be forced when the caller already knows it.</item>
/// <item>9.1 micro-depot metadata-zip filenames are non-deterministic across syncs --
/// identity is keyed on the zip's own content (<see cref="EsxPatchStoreMetadataBundle.ContentKey"/>),
/// never on <see cref="EsxPatchStoreMetadataBundle.ZipRelativePath"/>.</item>
/// <item>The store's <c>vvs/</c> compatibility bundle and the tool's
/// <c>hardlink-hostupdate</c>/<c>symlink-hostupdate</c> staging artifacts (siblings of
/// <c>hostupdate/</c> at the patch-store root, and never valid vendor codes) are
/// outside this parser's walk and are never surfaced as bundles, warnings, or
/// discrepancies -- vvs byte variance in particular is expected and is not a
/// corruption signal (research #1028 §1; coordinate the staging-tree name with #1164,
/// which owns hardening that tree on the acquisition side -- this parser only needs
/// to never walk into it).</item>
/// </list>
/// Malformed store content (an unreadable consolidated index, an unparseable metadata
/// zip, a metadata entry whose zip is missing) never throws -- it becomes a
/// <see cref="EsxPatchStoreMetadata.Warnings"/> entry so one bad vendor directory
/// cannot abort parsing the rest of the store, mirroring <c>XccdfParser</c>'s
/// tolerant-parse convention. <see cref="EsxPatchStoreParseResult.Failed"/> is
/// reserved for store-root-level problems (root does not exist, neither known layout
/// is present) that leave no store to describe at all.
/// </summary>
public interface IEsxPatchStoreMetadataParser
{
	/// <summary>
	/// Parses the store at <paramref name="storeRoot"/>. When <paramref name="layout"/>
	/// is null, both known layouts are probed (legacy first, then the 9.1 depot-nested
	/// path) and the first one whose <c>hostupdate/</c> directory exists is used; pass
	/// an explicit value to force one layout without probing (e.g. a store already
	/// configured to a known generation).
	/// </summary>
	EsxPatchStoreParseResult Parse(string storeRoot, EsxPatchStoreLayout? layout = null);
}
