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

namespace Waypoint.Api.Contracts;

/// <summary>Request body for <c>POST /api/v1/auth/login</c>.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Response body for a successful login.</summary>
public sealed record LoginResponse(string Token, string Role, DateTimeOffset ExpiresAt);

/// <summary>Response body for <c>GET /api/v1/auth/me</c>.</summary>
public sealed record CurrentUserResponse(string Username, string Role);

/// <summary>
/// Response body for <c>GET /api/v1/auth/config</c> (issue #534) — anonymous, so the
/// SPA can decide how to sign in before it has a token. <c>local_auth_enabled</c> is
/// the frontend's feature-detect for the dev-flag username/password form (mirrors
/// <c>LocalAuth:Enabled</c>; do not hardcode this client-side). <c>oidc_authority</c>
/// and <c>oidc_client_id</c> are what the SPA needs to build the authorization-code
/// (+PKCE) redirect and fetch the discovery document — see
/// <see cref="Waypoint.Core.Configuration.OidcAuthOptions.PublicAuthority"/>'s doc
/// comment for why this is a same-origin relative path, not the backend's own
/// (container-network) <c>Authority</c>.
/// </summary>
public sealed record AuthConfigResponse(bool LocalAuthEnabled, string OidcAuthority, string OidcClientId);
