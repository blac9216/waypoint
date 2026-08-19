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

using Waypoint.Core.Authorization;

namespace Waypoint.Core.Users;

/// <summary>
/// A persisted <c>users</c> row (migration 0031, issue #512). <see cref="Role"/> is a
/// mirror of the caller's most recently seen OIDC role claim -- ADR-0004 makes
/// Keycloak group membership authoritative, so this backend never treats
/// <see cref="Role"/> as independently writable; it is refreshed on every upsert (see
/// <see cref="IUserDirectory.RecordSeenAsync"/>). <see cref="SiteScopeJson"/> is the
/// one field <c>PUT /users/{id}</c> actually edits -- an app-local restriction with no
/// IdP equivalent.
/// </summary>
public sealed record UserRecord(
	Guid Id,
	string OidcSub,
	string Username,
	WaypointRole Role,
	string SiteScopeJson,
	string AuthMethod,
	DateTimeOffset LastSeenAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
