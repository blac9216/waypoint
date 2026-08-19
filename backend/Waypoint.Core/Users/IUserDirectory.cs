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
/// Storage for <c>users</c> (migration 0031, issue #512). One implementation
/// (<c>Waypoint.Infrastructure.Users.UserRepository</c>, plain Npgsql -- same "no ORM
/// for this layer" convention as every other repository in this codebase).
/// Control-plane only: no runner ever authenticates a caller or touches this table
/// (mirrors migration 0030's <c>schedules</c> precedent -- see 0031's header comment).
/// </summary>
public interface IUserDirectory
{
	/// <summary>
	/// Upserts the row for <paramref name="oidcSub"/> keyed on <see cref="UserRecord.OidcSub"/>:
	/// creates it on first sight, otherwise refreshes <see cref="UserRecord.Username"/>,
	/// <see cref="UserRecord.Role"/> (the IdP-derived mirror -- see
	/// <see cref="UserRecord"/>'s doc comment), and <see cref="UserRecord.AuthMethod"/>
	/// from the caller's current claims. <see cref="UserRecord.LastSeenAt"/> is only
	/// advanced when the existing value is already older than
	/// <paramref name="minimumSeenInterval"/> -- called on every authenticated request
	/// (<c>UserUpsertMiddleware</c>), so without this throttle a busy caller would issue
	/// a write on every single request. <see cref="UserRecord.SiteScopeJson"/> is
	/// intentionally never touched here -- it is the one field only
	/// <c>PUT /users/{id}</c> writes.
	/// </summary>
	Task RecordSeenAsync(
		string oidcSub, string username, WaypointRole role, string authMethod,
		TimeSpan minimumSeenInterval, CancellationToken cancellationToken);

	Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken cancellationToken);

	Task<UserRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>
	/// Pre-provisions a row for an <paramref name="oidcSub"/> that has not yet
	/// authenticated -- e.g. an Admin setting <paramref name="siteScopeJson"/> ahead of
	/// a new hire's first login, per <c>docs/api-contract.md</c> `/users` listing POST
	/// alongside GET/PUT. <paramref name="role"/> only seeds the mirror; the first real
	/// login's <see cref="RecordSeenAsync"/> call immediately overwrites it from the
	/// IdP's own claim, exactly as it would for any other row (see
	/// <see cref="UserRecord"/>'s doc comment -- role is never independently authoritative
	/// here). Returns <c>null</c> if <paramref name="oidcSub"/> already has a row.
	/// </summary>
	Task<Guid?> CreateAsync(
		string oidcSub, string username, WaypointRole role, string siteScopeJson, string authMethod,
		CancellationToken cancellationToken);

	/// <summary>
	/// Sets <see cref="UserRecord.SiteScopeJson"/> -- the only field <c>PUT /users/{id}</c>
	/// accepts (see <see cref="UserRecord"/>'s doc comment on why <c>role</c> is not
	/// writable here). Returns <c>false</c> if no row with <paramref name="id"/> exists.
	/// </summary>
	Task<bool> UpdateSiteScopeAsync(Guid id, string siteScopeJson, CancellationToken cancellationToken);
}
