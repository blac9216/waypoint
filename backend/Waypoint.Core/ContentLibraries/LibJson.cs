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
/// Wire shape of a VCSP library's <c>lib.json</c> (issue #1393, research #1032 Q1),
/// pinned against the VMware reference publisher (<c>make_vcsp_2022.py</c>) and a live
/// probe of a vendor-published library -- NOT ported from the sibling repo's
/// <c>Repair-ContentLibrary</c>, which gets two of these fields backwards (see
/// <see cref="VersionRole"/> and epic #1185 issue #37's closing comment).
/// </summary>
/// <param name="VcspVersion">Always the literal string <c>"2"</c> -- the 2015 v1 wire format is dead.</param>
/// <param name="Version">
/// <see cref="VersionRole"/>. The library CHANGE COUNTER a subscriber polls: it must
/// increment on every write that adds, removes, or changes the content of any item.
/// A numeric string (e.g. <c>"164"</c>), not an integer, per the reference publisher
/// and the live-probed vendor document.
/// </param>
/// <param name="ContentVersion">
/// Distinct from <see cref="Version"/> and NEVER incremented by this writer -- research
/// #1032 confirmed it stuck at <c>"1"</c> on the vendor's own library months apart
/// while <see cref="Version"/> advanced from 148 to 164. The reference publisher
/// hardcodes it. This field is not a sync signal; do not wire anything to it.
/// </param>
/// <param name="Name">The library's operator-chosen display name.</param>
/// <param name="Id">Stable, <c>urn:uuid:</c>-prefixed identity, generated once and preserved across every subsequent write.</param>
/// <param name="Created">ISO-8601 creation timestamp, generated once and preserved across every subsequent write.</param>
/// <param name="ItemsHref">Always the literal string <c>"items.json"</c> -- relative to the directory holding this document.</param>
/// <param name="Capabilities">
/// Load-bearing (research #1032 Q1): a capability-less <c>lib.json</c> makes vCenter
/// report <c>vcsp_library_not_found</c> rather than a schema error. Must always be
/// present with both transfer directions.
/// </param>
public sealed record LibJson(
	string VcspVersion,
	string Version,
	string ContentVersion,
	string Name,
	string Id,
	string Created,
	string ItemsHref,
	LibCapabilitiesJson Capabilities)
{
	/// <summary>
	/// Documents the exact bug this record's shape exists to avoid repeating: the
	/// sibling's <c>Repair-ContentLibrary</c> bumps <see cref="ContentVersion"/> and
	/// pins <see cref="Version"/> at <c>"2"</c> -- the inverse of the real contract.
	/// Ported as-is, subscribed vCenters would never notice a library update. Referenced
	/// from XML doc comments only; carries no runtime behavior.
	/// </summary>
	public const string VersionRole = "lib.json.version is the change counter; contentVersion never moves (research #1032).";
}

/// <summary>
/// The transfer-mode advertisement every <see cref="LibJson"/> must carry. A static
/// file server only ever needs to advertise plain HTTP GET in both directions.
/// </summary>
public sealed record LibCapabilitiesJson(IReadOnlyList<string> TransferIn, IReadOnlyList<string> TransferOut)
{
	/// <summary>The single transfer mode this writer ever advertises or needs.</summary>
	public static readonly LibCapabilitiesJson HttpGetOnly = new(["httpGet"], ["httpGet"]);
}
