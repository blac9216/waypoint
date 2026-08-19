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

namespace Waypoint.Core.Authorization;

/// <summary>
/// Claim type constants shared by every authentication scheme (local session today,
/// Keycloak OIDC from issue #29 on). Keeping the claim type here — rather than
/// hardcoding a string at each call site — is part of the swappable-auth seam: a new
/// identity provider only has to populate these claims the same way.
/// </summary>
public static class WaypointClaimTypes
{
	/// <summary>The authenticated principal's <see cref="WaypointRole"/>, serialized as its enum name.</summary>
	public const string Role = "waypoint:role";

	/// <summary>
	/// The authenticated principal's stable, provider-issued subject identifier --
	/// issue #512's <c>users.oidc_sub</c> key. For OIDC this is the token's own `sub`
	/// claim (OidcClaimsMappingOptionsSetup copies it here verbatim, distinct from the
	/// human-readable <see cref="System.Security.Claims.ClaimTypes.Name"/> claim it
	/// also sets); LocalSessionAuthenticationHandler synthesizes
	/// <c>"local:{username}"</c> since the dev-flag local-auth path (ADR-0004 rollout
	/// note) has no real OIDC subject to key on.
	/// </summary>
	public const string Subject = "waypoint:sub";
}
