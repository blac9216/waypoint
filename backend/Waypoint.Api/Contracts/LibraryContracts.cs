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

using System.Text.Json.Serialization;
using Waypoint.Core.Catalog;

namespace Waypoint.Api.Contracts;

/// <summary>Response body for one row of <c>GET /api/v1/library/items</c> (issue #36).</summary>
public sealed record LibraryItemResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("external_id")]
	string ExternalId,

	[property: JsonPropertyName("product")]
	string? Product,

	[property: JsonPropertyName("version")]
	string? Version,

	[property: JsonPropertyName("status")]
	string Status,

	[property: JsonPropertyName("presence")]
	string Presence,

	[property: JsonPropertyName("size_bytes")]
	long? SizeBytes,

	[property: JsonPropertyName("provenance")]
	string Provenance,

	[property: JsonPropertyName("indexed_at")]
	DateTimeOffset IndexedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static LibraryItemResponse FromDomain(LibraryItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		return new LibraryItemResponse(
			item.Id.ToString(),
			item.ExternalId,
			item.Product,
			item.Version,
			item.Status,
			item.Presence,
			item.SizeBytes,
			item.Provenance,
			item.IndexedAt,
			item.UpdatedAt);
	}
}

/// <summary>Response body for one row of the "PRODUCT FAMILIES" rail (prototype screen 7).</summary>
public sealed record LibraryFamilyResponse(
	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("present_count")]
	int PresentCount,

	[property: JsonPropertyName("missing_count")]
	int MissingCount)
{
	public static LibraryFamilyResponse FromDomain(LibraryFamily family)
	{
		ArgumentNullException.ThrowIfNull(family);
		return new LibraryFamilyResponse(family.Name, family.PresentCount, family.MissingCount);
	}
}

/// <summary>
/// The full <c>GET /api/v1/library/items</c> envelope: items plus the family rail and the
/// resolved mode the presence values were evaluated against (so the frontend never has to
/// re-derive "was this connected or air-gapped" from anything but this response).
/// </summary>
public sealed record LibraryItemsResponse(
	[property: JsonPropertyName("mode")]
	string Mode,

	[property: JsonPropertyName("items")]
	IReadOnlyList<LibraryItemResponse> Items,

	[property: JsonPropertyName("families")]
	IReadOnlyList<LibraryFamilyResponse> Families);

/// <summary>
/// One line of the air-gapped "Export request manifest" want-list (docs/api-contract.md
/// `/library/request-manifest`) -- machine-readable and consumable by a connected
/// instance (e.g. to pre-seed a `/downloads` queue or a future `/bundles/export`
/// selection). Deliberately narrow: just enough to identify the artifact and why it's
/// wanted, nothing this appliance cannot honestly know without a manifest-import store.
/// </summary>
public sealed record LibraryRequestManifestEntry(
	[property: JsonPropertyName("external_id")]
	string ExternalId,

	[property: JsonPropertyName("product")]
	string? Product,

	[property: JsonPropertyName("version")]
	string? Version,

	[property: JsonPropertyName("reason")]
	string Reason);

/// <summary>Response body for <c>GET /api/v1/library/request-manifest</c>.</summary>
public sealed record LibraryRequestManifestResponse(
	[property: JsonPropertyName("generated_at")]
	DateTimeOffset GeneratedAt,

	[property: JsonPropertyName("appliance_mode")]
	string ApplianceMode,

	[property: JsonPropertyName("wanted")]
	IReadOnlyList<LibraryRequestManifestEntry> Wanted);
