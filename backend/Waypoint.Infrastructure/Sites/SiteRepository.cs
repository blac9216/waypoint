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
using Waypoint.Core.Pagination;
using Waypoint.Core.Sites;

namespace Waypoint.Infrastructure.Sites;

/// <summary>
/// Storage for sites (issue #19, epic #13): plain Npgsql, no ORM -- the same
/// convention <c>CredentialRepository</c> and <c>DepotArtifactRepository</c> already
/// establish for this layer.
/// </summary>
public sealed class SiteRepository
{
	private const string ProjectionSql = """
		SELECT id, name, description, stigman_override::text, created_at, updated_at
		FROM sites
		""";

	private readonly string _connectionString;

	public SiteRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<(IReadOnlyList<Site> Items, long TotalCount)> ListAsync(PageRequest page, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		long total;
		await using (NpgsqlCommand countCommand = new("SELECT count(*) FROM sites", connection))
		{
			total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<Site> items = [];
		await using (NpgsqlCommand listCommand = new($"{ProjectionSql} ORDER BY name LIMIT $1 OFFSET $2", connection))
		{
			listCommand.Parameters.AddWithValue(page.Limit);
			listCommand.Parameters.AddWithValue(page.Offset);
			await using NpgsqlDataReader reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				items.Add(Map(reader));
			}
		}

		return (items, total);
	}

	public async Task<Site?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>Creates a site. Returns null when the name is already taken (the caller maps that to a 409).</summary>
	public async Task<Guid?> CreateAsync(string name, string? description, string? stigmanOverrideJson, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO sites (name, description, stigman_override)
			VALUES ($1, $2, $3::jsonb)
			ON CONFLICT (name) DO NOTHING
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue((object?)description ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)stigmanOverrideJson ?? DBNull.Value);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid id ? id : null;
	}

	/// <summary>
	/// Full replacement update: a null argument leaves the corresponding column
	/// unchanged (COALESCE), matching <c>SiteUpdateRequest</c>'s "only replace what's
	/// supplied" semantics.
	/// </summary>
	public async Task<SiteWriteOutcome> UpdateAsync(
		Guid id, string? name, string? description, string? stigmanOverrideJson, bool clearDescription, bool clearStigmanOverride, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE sites SET
				name = COALESCE($2, name),
				description = CASE WHEN $4 THEN NULL ELSE COALESCE($3, description) END,
				stigman_override = CASE WHEN $6 THEN NULL ELSE COALESCE($5::jsonb, stigman_override) END
			WHERE id = $1
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue((object?)name ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)description ?? DBNull.Value);
		command.Parameters.AddWithValue(clearDescription);
		command.Parameters.AddWithValue((object?)stigmanOverrideJson ?? DBNull.Value);
		command.Parameters.AddWithValue(clearStigmanOverride);

		try
		{
			return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null
				? SiteWriteOutcome.Ok
				: SiteWriteOutcome.NotFound;
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			return SiteWriteOutcome.NameTaken;
		}
	}

	/// <summary>Deletes a site; targets cascade (migration 0008's ON DELETE CASCADE) only when the caller has confirmed via <paramref name="force"/>.</summary>
	public async Task<SiteDeleteOutcome> DeleteAsync(Guid id, bool force, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		if (!force)
		{
			await using NpgsqlCommand countCommand = new("SELECT count(*) FROM targets WHERE site_id = $1", connection);
			countCommand.Parameters.AddWithValue(id);
			long targetCount = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
			if (targetCount > 0)
			{
				return SiteDeleteOutcome.InUse;
			}
		}

		await using NpgsqlCommand deleteCommand = new("DELETE FROM sites WHERE id = $1 RETURNING id", connection);
		deleteCommand.Parameters.AddWithValue(id);
		object? result = await deleteCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null ? SiteDeleteOutcome.Deleted : SiteDeleteOutcome.NotFound;
	}

	private static Site Map(NpgsqlDataReader reader)
	{
		return new Site(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.IsDBNull(2) ? null : reader.GetString(2),
			reader.IsDBNull(3) ? null : reader.GetString(3),
			reader.GetFieldValue<DateTimeOffset>(4),
			reader.GetFieldValue<DateTimeOffset>(5));
	}
}
