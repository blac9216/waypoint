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

using Waypoint.Core.StigManager;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for the global/resolved STIG Manager connection (docs/api-contract.md
/// `/stigman`, `/sites/{id}/stigman`). Field names rely on the process-wide
/// <c>JsonNamingPolicy.SnakeCaseLower</c> (<c>WaypointJsonOptions</c>), the same
/// convention <c>CredentialResponse</c> uses -- no per-property
/// <c>[JsonPropertyName]</c> needed. Carries no secret: <see cref="CredentialId"/>
/// names the credential store row, never a value.
/// </summary>
public sealed record StigManagerConnectionResponse(
	string Endpoint,
	string Authority,
	string Collection,
	string ClientId,
	string Scope,
	Guid? CredentialId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt)
{
	public static StigManagerConnectionResponse FromDomain(StigManagerConnection connection)
	{
		ArgumentNullException.ThrowIfNull(connection);
		return new StigManagerConnectionResponse(
			connection.Endpoint, connection.Authority, connection.Collection, connection.ClientId, connection.Scope,
			connection.CredentialId, connection.CreatedAt, connection.UpdatedAt);
	}
}

/// <summary>Request body for <c>PUT /api/v1/stigman</c>.</summary>
public sealed record StigManagerConnectionBody(
	string? Endpoint,
	string? Authority,
	string? Collection,
	string? ClientId,
	string? Scope,
	Guid? CredentialId);

/// <summary>
/// Response body for the per-site resolved connection (<c>GET /sites/{id}/stigman</c>):
/// the effective values plus which layer supplied them, so #312's Config tab can render
/// "inherited from global" vs "overridden for this site" the way the Sites sidebar
/// already does for <c>stigman_override</c> presence (PR #263).
/// </summary>
public sealed record ResolvedStigManagerConnectionResponse(
	string Endpoint,
	string Authority,
	string Collection,
	string ClientId,
	string Scope,
	Guid? CredentialId,
	string Source)
{
	public static ResolvedStigManagerConnectionResponse FromDomain(ResolvedStigManagerConnection connection)
	{
		ArgumentNullException.ThrowIfNull(connection);
		string source = connection.Source == StigManagerConnectionSource.SiteOverride ? "site_override" : "global";
		return new ResolvedStigManagerConnectionResponse(
			connection.Endpoint, connection.Authority, connection.Collection, connection.ClientId, connection.Scope,
			connection.CredentialId, source);
	}
}
