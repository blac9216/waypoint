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

using System.Text.Json;
using System.Text.Json.Serialization;
using Waypoint.Core.Sites;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for a target (docs/api-contract.md `/sites/{id}/targets`: kind,
/// connection.host, credential_ref, discovery_status, last_refreshed).
/// <see cref="Connection"/> rides as a raw JSON string -- same convention as
/// <see cref="SiteResponse.StigmanOverride"/> -- and, per
/// docs/domain-model.md's "service credential referenced, never embedded" rule, never
/// carries secret material: only <see cref="CredentialId"/> ever names the credential.
/// </summary>
public sealed record TargetResponse(
	[property: JsonPropertyName("id")]
	Guid Id,

	[property: JsonPropertyName("site_id")]
	Guid SiteId,

	[property: JsonPropertyName("kind")]
	string Kind,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("connection")]
	string Connection,

	[property: JsonPropertyName("credential_ref")]
	Guid? CredentialId,

	[property: JsonPropertyName("discovery_status")]
	string DiscoveryStatus,

	[property: JsonPropertyName("last_refreshed")]
	DateTimeOffset? LastRefreshed,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static TargetResponse FromDomain(Target target)
	{
		ArgumentNullException.ThrowIfNull(target);
		return new TargetResponse(
			target.Id, target.SiteId, target.Kind, target.Name, target.ConnectionJson,
			target.CredentialId, target.DiscoveryStatus, target.LastRefreshed, target.CreatedAt, target.UpdatedAt);
	}
}

/// <summary>Request body for <c>POST /api/v1/sites/{id}/targets</c>.</summary>
public sealed record TargetCreateBody(
	[property: JsonPropertyName("kind")]
	string? Kind,

	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("connection")]
	JsonElement? Connection,

	[property: JsonPropertyName("credential_ref")]
	Guid? CredentialId);

/// <summary>Request body for <c>PUT /api/v1/targets/{id}</c>. Same "supplied fields only" replacement semantics as <see cref="SiteUpdateBody"/>.</summary>
public sealed record TargetUpdateBody(
	[property: JsonPropertyName("kind")]
	string? Kind,

	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("connection")]
	JsonElement? Connection,

	[property: JsonPropertyName("credential_ref")]
	Guid? CredentialId,

	[property: JsonPropertyName("clear_credential_ref")]
	bool ClearCredential);

/// <summary>
/// Guards docs/domain-model.md's "connection secrets are NEVER embedded in the
/// target, only referenced by ID": a <c>connection</c> payload naming any
/// secret-shaped key is rejected with 400 before it ever reaches storage. This is a
/// closed-set key-name check (case-insensitive), not a content/entropy scan -- the
/// contract's rule is about which *field* a caller uses, not whether a given value
/// looks secret-ish.
/// </summary>
public static class TargetConnectionValidator
{
	private static readonly string[] ForbiddenKeys =
	[
		"password", "passwd", "secret", "token", "api_key", "apikey",
		"private_key", "privatekey", "credential", "credentials", "auth_token", "client_secret",
	];

	/// <summary>Returns the first forbidden key found (for the 400 detail message), or null when the payload is clean.</summary>
	public static string? FindForbiddenKey(JsonElement? connection)
	{
		if (connection is not { ValueKind: JsonValueKind.Object } element)
		{
			return null;
		}

		foreach (JsonProperty property in element.EnumerateObject())
		{
			foreach (string forbidden in ForbiddenKeys)
			{
				if (string.Equals(property.Name, forbidden, StringComparison.OrdinalIgnoreCase))
				{
					return property.Name;
				}
			}
		}

		return null;
	}
}
