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
using Npgsql;
using Waypoint.Core.Serialization;
using Waypoint.Core.StigManager;

namespace Waypoint.Infrastructure.StigManager;

/// <summary>
/// Storage for the global STIG Manager connection (<c>stigman_connections</c>, migration
/// 0017) plus resolution against a site's <c>stigman_override</c> JSONB column (migration
/// 0009). Plain Npgsql, no ORM -- matches <c>SiteRepository</c>/<c>CredentialRepository</c>.
/// The override JSON is written and read with <see cref="WaypointJsonOptions.Default"/>
/// (snake_case) -- the same options the wire contract uses -- so the shape a caller PUTs
/// via <c>StigManagerConnectionBody</c> is exactly the shape stored and read back here.
/// </summary>
public sealed class StigManagerRepository
{
	private readonly string _connectionString;

	public StigManagerRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>Returns null when no global connection has ever been configured.</summary>
	public async Task<StigManagerConnection?> GetGlobalAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return await GetGlobalAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<StigManagerConnection?> GetGlobalAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"""
			SELECT endpoint, authority, collection, client_id, scope, credential_id, created_at, updated_at
			FROM stigman_connections
			WHERE id = 1
			""", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new StigManagerConnection(
			reader.GetString(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.IsDBNull(5) ? null : reader.GetGuid(5),
			reader.GetFieldValue<DateTimeOffset>(6),
			reader.GetFieldValue<DateTimeOffset>(7));
	}

	/// <summary>Upserts the singleton global connection row.</summary>
	public async Task<StigManagerConnection> PutGlobalAsync(
		string endpoint, string authority, string collection, string clientId, string scope, Guid? credentialId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO stigman_connections (id, endpoint, authority, collection, client_id, scope, credential_id)
			VALUES (1, $1, $2, $3, $4, $5, $6)
			ON CONFLICT (id) DO UPDATE SET
				endpoint = EXCLUDED.endpoint,
				authority = EXCLUDED.authority,
				collection = EXCLUDED.collection,
				client_id = EXCLUDED.client_id,
				scope = EXCLUDED.scope,
				credential_id = EXCLUDED.credential_id
			""", connection);
		command.Parameters.AddWithValue(endpoint);
		command.Parameters.AddWithValue(authority);
		command.Parameters.AddWithValue(collection);
		command.Parameters.AddWithValue(clientId);
		command.Parameters.AddWithValue(scope);
		command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		return (await GetGlobalAsync(connection, cancellationToken).ConfigureAwait(false))!;
	}

	/// <summary>
	/// Resolves the effective connection for a site: <c>sites.stigman_override</c> wins
	/// over the global row when present (docs/domain-model.md three-layer "most
	/// specific wins" convention, applied here to two layers since there is no
	/// target-level STIG Manager override). Returns null when neither the site's
	/// override nor a global connection is configured.
	/// </summary>
	public async Task<ResolvedStigManagerConnection?> ResolveForSiteAsync(Guid siteId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand overrideCommand = new("SELECT stigman_override::text FROM sites WHERE id = $1", connection);
		overrideCommand.Parameters.AddWithValue(siteId);
		object? overrideResult = await overrideCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

		if (overrideResult is string overrideJson)
		{
			StigManagerOverrideJson? parsed = JsonSerializer.Deserialize<StigManagerOverrideJson>(overrideJson, WaypointJsonOptions.Default);
			if (parsed is { Endpoint: not null, Authority: not null, Collection: not null, ClientId: not null })
			{
				return new ResolvedStigManagerConnection(
					parsed.Endpoint, parsed.Authority, parsed.Collection, parsed.ClientId,
					parsed.Scope ?? "openid stig-manager:stig:read", parsed.CredentialId, StigManagerConnectionSource.SiteOverride);
			}
		}

		StigManagerConnection? global = await GetGlobalAsync(connection, cancellationToken).ConfigureAwait(false);
		return global is null
			? null
			: new ResolvedStigManagerConnection(
				global.Endpoint, global.Authority, global.Collection, global.ClientId, global.Scope, global.CredentialId,
				StigManagerConnectionSource.Global);
	}

	/// <summary>Wire shape of <c>sites.stigman_override</c>. Matches <see cref="StigManagerConnectionRequest"/>'s field set; a partially-filled override (e.g. missing endpoint) is treated as absent so resolution falls through to the global connection.</summary>
	private sealed record StigManagerOverrideJson(
		string? Endpoint, string? Authority, string? Collection, string? ClientId, string? Scope, Guid? CredentialId);
}
