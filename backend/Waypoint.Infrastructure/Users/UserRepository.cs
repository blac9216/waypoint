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

using Npgsql;
using Waypoint.Core.Authorization;
using Waypoint.Core.Users;

namespace Waypoint.Infrastructure.Users;

/// <inheritdoc cref="IUserDirectory"/>
public sealed class UserRepository : IUserDirectory
{
	private const string ProjectionSql = """
		SELECT id, oidc_sub, username, role, site_scope::text, auth_method, last_seen_at, created_at, updated_at
		FROM users
		""";

	private readonly string _connectionString;

	public UserRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task RecordSeenAsync(
		string oidcSub, string username, WaypointRole role, string authMethod,
		TimeSpan minimumSeenInterval, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// The throttle lives in the WHERE clause of the ON CONFLICT branch itself: a
		// row seen within minimumSeenInterval is left with last_seen_at untouched
		// (though username/role/auth_method still refresh -- those are cheap and
		// correctness-relevant on every request, unlike the timestamp write). This is
		// one round trip regardless of whether the throttle fires, rather than a
		// SELECT-then-maybe-UPDATE that would race under concurrent requests from the
		// same principal.
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO users (oidc_sub, username, role, auth_method, last_seen_at)
			VALUES ($1, $2, $3, $4, now())
			ON CONFLICT (oidc_sub) DO UPDATE SET
				username = EXCLUDED.username,
				role = EXCLUDED.role,
				auth_method = EXCLUDED.auth_method,
				last_seen_at = CASE
					WHEN users.last_seen_at < now() - $5::interval THEN now()
					ELSE users.last_seen_at
				END
			""", connection);
		command.Parameters.AddWithValue(oidcSub);
		command.Parameters.AddWithValue(username);
		command.Parameters.AddWithValue(role.ToString());
		command.Parameters.AddWithValue(authMethod);
		command.Parameters.AddWithValue(minimumSeenInterval);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY username", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<UserRecord> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<UserRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<Guid?> CreateAsync(
		string oidcSub, string username, WaypointRole role, string siteScopeJson, string authMethod,
		CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO users (oidc_sub, username, role, site_scope, auth_method)
			VALUES ($1, $2, $3, $4::jsonb, $5)
			ON CONFLICT (oidc_sub) DO NOTHING
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(oidcSub);
		command.Parameters.AddWithValue(username);
		command.Parameters.AddWithValue(role.ToString());
		command.Parameters.AddWithValue(siteScopeJson);
		command.Parameters.AddWithValue(authMethod);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid id ? id : null;
	}

	public async Task<bool> UpdateSiteScopeAsync(Guid id, string siteScopeJson, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE users SET site_scope = $2::jsonb WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(siteScopeJson);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null;
	}

	private static UserRecord Map(NpgsqlDataReader reader)
	{
		return new UserRecord(
			Id: reader.GetGuid(0),
			OidcSub: reader.GetString(1),
			Username: reader.GetString(2),
			Role: Enum.Parse<WaypointRole>(reader.GetString(3)),
			SiteScopeJson: reader.GetString(4),
			AuthMethod: reader.GetString(5),
			LastSeenAt: reader.GetFieldValue<DateTimeOffset>(6),
			CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
			UpdatedAt: reader.GetFieldValue<DateTimeOffset>(8));
	}
}
