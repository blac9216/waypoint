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
	/// Where the backend fetches the discovery document/JWKS from
	/// (<c>{Authority}/.well-known/openid-configuration</c>) — the backend's own
	/// container-network view of Keycloak, e.g.
	/// <c>http://keycloak:8080/auth/realms/waypoint</c>. Required for OIDC to be usable;
	/// when unset the JwtBearer handler fails closed (every bearer request is rejected)
	/// rather than silently accepting unvalidated tokens.
	///
	/// Issue #536: deliberately NOT also used for issuer (<c>iss</c>) validation, unlike
	/// <c>AddJwtBearer</c>'s default (which derives <c>ValidIssuer</c> from this same
	/// value) — see <see cref="ValidIssuer"/>/<see cref="ValidIssuers"/> for why that
	/// default cannot work here: this address is reachable only from <c>backend</c>,
	/// never from the browser that actually mints tokens.
	/// </summary>
	public string? Authority { get; set; }

	/// <summary>
	/// The single browser-visible HTTPS origin for this appliance, for example
	/// <c>https://waypoint.example.internal</c> or
	/// <c>https://waypoint.example.internal:8443</c>. It must not contain user info,
	/// a path, query, fragment, or trailing slash. When set, the token issuer is
	/// derived as <c>{PublicUrl}/auth/realms/waypoint</c>; callers must not maintain a
	/// second copy of that public identity in <see cref="ValidIssuer"/>.
	/// </summary>
	public string? PublicUrl { get; set; }

	/// <summary>
	/// The single canonical issuer string a real token's <c>iss</c> claim must match
	/// (issue #536). Distinct from <see cref="Authority"/>: that address is the
	/// backend's internal, container-network view of Keycloak, used only to fetch the
	/// discovery document/JWKS quickly and without a TLS trust round trip; a real
	/// browser-obtained token's issuer instead reflects Keycloak's
	/// <c>KC_HOSTNAME</c>-pinned canonical public identity (deploy/README.md
	/// "Keycloak"), which is what this property should be set to. Required in every
	/// real deployment (an unset value plus an empty <see cref="ValidIssuers"/> fails
	/// closed — <c>AddJwtBearer</c> falls back to validating against
	/// <see cref="Authority"/>, which a browser-minted token's <c>iss</c> can never
	/// match, so login stays broken rather than silently accepting the wrong issuer).
	/// Program.cs wires <c>MetadataAddress</c> from <see cref="Authority"/> and
	/// <c>TokenValidationParameters.ValidIssuer</c>/<c>ValidIssuers</c> from this pair
	/// independently, so discovery and issuer validation can point at two different
	/// URLs. Prefer this single-value property; use <see cref="ValidIssuers"/> only for
	/// a deployment that must accept more than one issuer string at once.
	/// </summary>
	public string? ValidIssuer { get; set; }

	/// <summary>
	/// Accepts more than one issuer string at once (issue #536) — e.g. a transition
	/// between two edge hostnames. Checked alongside <see cref="ValidIssuer"/> when
	/// both are set; most deployments need only the single-value property above.
	/// </summary>
	public IReadOnlyList<string> ValidIssuers { get; set; } = Array.Empty<string>();

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

	/// <summary>
	/// The browser-facing issuer URL the SPA drives its authorization-code (+PKCE)
	/// redirect and discovery-document fetch against (issue #534). Deliberately
	/// distinct from <see cref="Authority"/>: that value is the backend's
	/// container-network view of Keycloak (e.g. <c>http://keycloak:8080/realms/waypoint</c>
	/// inside the compose network) and is never reachable from a browser. The browser
	/// instead goes through nginx's same-origin <c>/auth/</c> proxy
	/// (<c>deploy/nginx/conf.d/default.conf</c>, issue #28) to Keycloak, so the default
	/// here is the relative path <c>/auth/realms/waypoint</c> — no host/port baked in,
	/// which is what lets one image serve any operator's hostname (air-gapped
	/// instances have no fixed public hostname). Exposed read-only via
	/// <c>GET /api/v1/auth/config</c> (anonymous) so the SPA never hardcodes it.
	/// </summary>
	public string PublicAuthority { get; set; } = "/auth/realms/waypoint";

	/// <summary>
	/// The public (PKCE, no-secret) OIDC client id the SPA authenticates as — distinct
	/// from <see cref="Audience"/>/<c>waypoint-backend</c>, the confidential client the
	/// ASP.NET Core backend itself was validated against before #534. See
	/// <c>deploy/keycloak/realm/waypoint-realm.json</c>'s <c>waypoint-frontend</c>
	/// client (issue #534): <c>publicClient: true</c>, PKCE required, no
	/// <c>clientAuthenticatorType</c>/secret. Its tokens still carry
	/// <c>aud: waypoint-backend</c> (an audience protocol mapper on the client), so
	/// this backend's existing <see cref="Audience"/> check needs no change.
	/// </summary>
	public string PublicClientId { get; set; } = "waypoint-frontend";

	/// <summary>Returns the issuer derived from <see cref="PublicUrl"/>, or null when
	/// the canonical URL has not been configured.</summary>
	public string? DerivedIssuer => string.IsNullOrWhiteSpace(PublicUrl)
		? null
		: $"{PublicUrl}/auth/realms/waypoint";

	/// <summary>Validates the canonical public URL without resolving DNS or making a
	/// network request.</summary>
	public static bool IsValidPublicUrl(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)
			|| value.EndsWith('/')
			|| !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
		{
			return false;
		}

		return uri.Scheme == Uri.UriSchemeHttps
			&& !string.IsNullOrWhiteSpace(uri.Host)
			&& string.IsNullOrEmpty(uri.UserInfo)
			&& uri.AbsolutePath == "/"
			&& string.IsNullOrEmpty(uri.Query)
			&& string.IsNullOrEmpty(uri.Fragment);
	}
}
