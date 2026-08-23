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

namespace Waypoint.Core.Catalog;

/// <summary>
/// The closed set of presence states the Library "Repository" tab renders (issue #36,
/// docs/api-contract.md "Library & content library": "Presence model per mode:
/// `present`|`superseded`|`in_depot`(connected)|`missing`(air-gapped, vs last bundle
/// manifest)"). Evaluated by <see cref="LibraryPresenceEvaluator"/> from the existing
/// <c>depot_artifacts</c> catalog -- there is no separate library store (deliberately;
/// this issue reuses the catalog rather than inventing a parallel one).
/// </summary>
public static class LibraryPresenceStates
{
	/// <summary>On disk and the newest present version of its product.</summary>
	public const string Present = "present";

	/// <summary>On disk, but a newer version of the same product is also present -- kept for reference, not the version a fresh deployment should use.</summary>
	public const string Superseded = "superseded";

	/// <summary>Not on disk, but indexed/entitled at the depot -- connected mode only.</summary>
	public const string InDepot = "in_depot";

	/// <summary>
	/// Not on disk in air-gapped mode. Named "missing" to match the contract, but the
	/// evaluation this issue ships is "not `present` in <c>depot_artifacts</c>", which is
	/// the same set an air-gapped instance's last imported bundle would have populated
	/// (issue #566/bundle-import lands the manifest-diff machinery itself; no `bundles`
	/// table exists on `main` yet -- see <see cref="LibraryPresenceEvaluator"/>'s doc
	/// comment for the exact scope line).
	/// </summary>
	public const string Missing = "missing";
}

/// <summary>
/// One row of <c>GET /library/items</c> (issue #36): a depot artifact re-presented with
/// its mode-aware presence, product-family grouping, and provenance. Built entirely from
/// <see cref="DepotArtifact"/> -- no new persistence.
/// </summary>
public sealed record LibraryItem(
	Guid Id,
	string ExternalId,
	string? Product,
	string? Version,
	string Status,
	string Presence,
	long? SizeBytes,
	string Provenance,
	DateTimeOffset IndexedAt,
	DateTimeOffset UpdatedAt);

/// <summary>One product family's presence rollup for the Library rail (prototype screen 7's "PRODUCT FAMILIES" list).</summary>
public sealed record LibraryFamily(string Name, int PresentCount, int MissingCount);
