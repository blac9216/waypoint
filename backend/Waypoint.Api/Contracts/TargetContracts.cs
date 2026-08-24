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
/// carries secret material: only <see cref="CredentialId"/>/<see cref="Bindings"/> ever
/// name a credential.
///
/// <see cref="CredentialId"/> (<c>credential_ref</c>) is DEPRECATED (issue #584, ADR-0021):
/// it still round-trips and execution (RunCreationService/ScanJobHandler/etc.) still
/// reads only this field until #585 lands purpose-aware resolution, but it now always
/// mirrors the target kind's DEFAULT purpose binding in <see cref="Bindings"/>
/// (migration 0043's dual-write contract) -- new integrations should read/write
/// <see cref="Bindings"/> instead. It is kept on the wire, not removed, so no existing
/// caller breaks; removal is #585's scope.
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
	DateTimeOffset UpdatedAt,

	[property: JsonPropertyName("bindings")]
	IReadOnlyList<TargetCredentialBindingResponse> Bindings)
{
	public static TargetResponse FromDomain(Target target, IReadOnlyList<TargetCredentialBinding>? bindings = null)
	{
		ArgumentNullException.ThrowIfNull(target);
		return new TargetResponse(
			target.Id, target.SiteId, target.Kind, target.Name, target.ConnectionJson,
			target.CredentialId, target.DiscoveryStatus, target.LastRefreshed, target.CreatedAt, target.UpdatedAt,
			(bindings ?? []).Select(b => TargetCredentialBindingResponse.FromDomain(b)).ToArray());
	}
}

/// <summary>
/// One purpose -&gt; credential binding on a target (issue #584, migration 0043,
/// ADR-0021). <see cref="CredentialId"/> is a reference only -- never secret material,
/// same rule as <see cref="TargetResponse.CredentialId"/>.
/// </summary>
public sealed record TargetCredentialBindingResponse(
	[property: JsonPropertyName("purpose")]
	string Purpose,

	[property: JsonPropertyName("credential_ref")]
	Guid CredentialId,

	[property: JsonPropertyName("credential_name")]
	string? CredentialName,

	[property: JsonPropertyName("credential_type")]
	string? CredentialType,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static TargetCredentialBindingResponse FromDomain(TargetCredentialBinding binding, string? credentialName = null, string? credentialType = null)
	{
		ArgumentNullException.ThrowIfNull(binding);
		return new TargetCredentialBindingResponse(binding.Purpose, binding.CredentialId, credentialName, credentialType, binding.CreatedAt, binding.UpdatedAt);
	}
}

/// <summary>Request body for <c>PUT /api/v1/targets/{id}/credential-bindings/{purpose}</c> -- sets (creates or replaces/overrides) the binding for that purpose.</summary>
public sealed record TargetCredentialBindingSetBody(
	[property: JsonPropertyName("credential_ref")]
	Guid? CredentialId);

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

	/// <summary>
	/// Upper bound on how deep the recursive scan will walk before giving up. A secret
	/// key at any legitimate depth is far shallower than this; the cap exists only to
	/// keep a pathologically nested payload (deliberate or accidental) from exhausting
	/// the stack. The walk is iterative anyway, but bounding depth also bounds work.
	/// </summary>
	private const int MaxScanDepth = 64;

	/// <summary>
	/// Returns the first forbidden key found (for the 400 detail message), or null when
	/// the payload is clean. The scan is recursive: it walks the whole JSON tree --
	/// nested objects, array elements, objects inside arrays, arrays inside objects, to
	/// arbitrary depth -- because the "connection secrets are NEVER embedded" rule is
	/// defeated the moment a caller can hide a secret one level down
	/// (<c>{"vc":{"password":"x"}}</c>) or inside an array (<c>{"creds":[{"token":"x"}]}</c>).
	/// Matching stays exact-key, case-insensitive -- only the traversal depth changes.
	/// Implemented iteratively (explicit stack) so a deeply nested payload can't overflow
	/// the call stack; depth is additionally capped at <see cref="MaxScanDepth"/>.
	/// </summary>
	public static string? FindForbiddenKey(JsonElement? connection)
	{
		if (connection is not { } root)
		{
			return null;
		}

		// (element, depth) work items. Only objects and arrays carry children worth
		// visiting; scalars are leaves and can never *be* a key, so they're skipped.
		Stack<(JsonElement Element, int Depth)> pending = new();
		pending.Push((root, 0));

		while (pending.Count > 0)
		{
			(JsonElement element, int depth) = pending.Pop();
			if (depth > MaxScanDepth)
			{
				continue;
			}

			switch (element.ValueKind)
			{
				case JsonValueKind.Object:
					foreach (JsonProperty property in element.EnumerateObject())
					{
						foreach (string forbidden in ForbiddenKeys)
						{
							if (string.Equals(property.Name, forbidden, StringComparison.OrdinalIgnoreCase))
							{
								return property.Name;
							}
						}

						pending.Push((property.Value, depth + 1));
					}

					break;

				case JsonValueKind.Array:
					foreach (JsonElement item in element.EnumerateArray())
					{
						pending.Push((item, depth + 1));
					}

					break;
			}
		}

		return null;
	}
}
