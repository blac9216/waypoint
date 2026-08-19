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

namespace Waypoint.Core.Configuration;

/// <summary>
/// Configuration for JWT bearer validation against Keycloak (ADR-0004). The backend is
/// a plain OIDC relying party: it validates issuer/audience/signature via the issuer's
/// discovery document and maps the realm's <c>role</c> claim (a single group-membership
/// name — see <c>deploy/keycloak/realm/waypoint-realm.json</c>'s
/// <c>oidc-group-membership-mapper</c>) to <see cref="Authorization.WaypointClaimTypes.Role"/>.
/// Bound from the <c>Oidc</c> section.
/// </summary>
public sealed class OidcAuthOptions
{
	public const string SectionName = "Oidc";

	/// <summary>
	/// The realm's issuer URL (e.g. <c>https://keycloak.example.internal/realms/waypoint</c>),
	/// used both for token issuer validation and to locate the discovery document
	/// (<c>{Authority}/.well-known/openid-configuration</c>). Required for OIDC to be
	/// usable; when unset the JwtBearer handler fails closed (every bearer request is
	/// rejected) rather than silently accepting unvalidated tokens.
	/// </summary>
	public string? Authority { get; set; }

	/// <summary>
	/// The OIDC client/audience this backend validates tokens for — the confidential
	/// client id in <c>deploy/keycloak/realm/waypoint-realm.json</c>
	/// (<c>waypoint-backend</c>).
	/// </summary>
	public string Audience { get; set; } = "waypoint-backend";

	/// <summary>
	/// Whether the JwtBearer handler requires HTTPS metadata (discovery document,
	/// JWKS) from <see cref="Authority"/>. True in every real deployment (TLS is
	/// terminated at nginx, ADR-0003); false only for local/dev stacks that talk to
	/// Keycloak over plain HTTP inside the compose network.
	/// </summary>
	public bool RequireHttpsMetadata { get; set; } = true;

	/// <summary>
	/// The token claim carrying the realm's role-group name (see the realm's
	/// <c>waypoint-role</c> protocol mapper, <c>claim.name: "role"</c>). Mapped to
	/// <see cref="Authorization.WaypointClaimTypes.Role"/> after signature/issuer
	/// validation succeeds.
	/// </summary>
	public string RoleClaimType { get; set; } = "role";
}
