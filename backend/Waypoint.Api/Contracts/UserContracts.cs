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

namespace Waypoint.Api.Contracts;

/// <summary>
/// Wire shape for a <c>users</c> row. <see cref="Role"/> is a read-only mirror of the
/// caller's IdP-derived role claim (ADR-0004) -- see <c>UserRecord</c>'s doc comment;
/// there is no writable role field anywhere on this contract by design.
/// </summary>
public sealed record UserResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("oidc_sub")]
	string OidcSub,

	[property: JsonPropertyName("username")]
	string Username,

	[property: JsonPropertyName("role")]
	string Role,

	[property: JsonPropertyName("site_scope")]
	string SiteScope,

	[property: JsonPropertyName("auth_method")]
	string AuthMethod,

	[property: JsonPropertyName("last_seen_at")]
	string LastSeenAt,

	[property: JsonPropertyName("created_at")]
	string CreatedAt,

	[property: JsonPropertyName("updated_at")]
	string UpdatedAt);

/// <summary>
/// Request body for <c>POST /api/v1/users</c> -- pre-provisions a row for a caller who
/// has not yet authenticated (see <c>IUserDirectory.CreateAsync</c>'s doc comment).
/// <see cref="Role"/> only seeds the mirror; it is overwritten by the subject's first
/// real login.
/// </summary>
public sealed record UserCreateRequest(
	[property: JsonPropertyName("oidc_sub")]
	string OidcSub,

	[property: JsonPropertyName("username")]
	string Username,

	[property: JsonPropertyName("role")]
	string Role,

	[property: JsonPropertyName("site_scope")]
	string? SiteScope);

/// <summary>
/// Request body for <c>PUT /api/v1/users/{id}</c>. Only <see cref="SiteScope"/> is
/// written -- a request that also sets a <c>role</c> field is not accepted by this
/// record at all (deliberately absent), since role edits happen in the IdP (ADR-0004;
/// see <c>UserRecord</c>'s doc comment).
/// </summary>
public sealed record UserUpdateRequest(
	[property: JsonPropertyName("site_scope")]
	string SiteScope);
