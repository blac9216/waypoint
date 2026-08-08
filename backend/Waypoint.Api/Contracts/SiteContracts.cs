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
using Waypoint.Core.Sites;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for a site (docs/api-contract.md `/sites`: name, description,
/// stigman_override?). <see cref="StigmanOverride"/> rides as a raw JSON string, not a
/// nested object -- the same convention <c>RunResponse.Scope</c> and
/// <c>CatalogArtifactResponse.Metadata</c> already use for a JSONB column on the wire.
/// </summary>
public sealed record SiteResponse(
	[property: JsonPropertyName("id")]
	Guid Id,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("description")]
	string? Description,

	[property: JsonPropertyName("stigman_override")]
	string? StigmanOverride,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static SiteResponse FromDomain(Site site)
	{
		ArgumentNullException.ThrowIfNull(site);
		return new SiteResponse(site.Id, site.Name, site.Description, site.StigmanOverrideJson, site.CreatedAt, site.UpdatedAt);
	}
}

/// <summary>Request body for <c>POST /api/v1/sites</c>.</summary>
public sealed record SiteCreateBody(
	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("description")]
	string? Description,

	[property: JsonPropertyName("stigman_override")]
	string? StigmanOverride);

/// <summary>
/// Request body for <c>PUT /api/v1/sites/{id}</c>. <see cref="ClearDescription"/> and
/// <see cref="ClearStigmanOverride"/> let a caller explicitly null out an optional
/// field -- otherwise a PUT can only ever add/replace, never remove, an optional value
/// (a null JSON field is indistinguishable from "not supplied" once bound).
/// </summary>
public sealed record SiteUpdateBody(
	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("description")]
	string? Description,

	[property: JsonPropertyName("clear_description")]
	bool ClearDescription,

	[property: JsonPropertyName("stigman_override")]
	string? StigmanOverride,

	[property: JsonPropertyName("clear_stigman_override")]
	bool ClearStigmanOverride);
