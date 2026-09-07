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
using NpgsqlTypes;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IConsumerViewRepository"/>
public sealed class ConsumerViewRepository : IConsumerViewRepository
{
	// Migration 0131's two named constraints this repository translates on conflict:
	// idx_consumer_views_name_unique (duplicate name) and
	// idx_consumer_views_default_unique (a second is_default = true row).
	private const string NameUniqueIndex = "idx_consumer_views_name_unique";
	private const string DefaultUniqueIndex = "idx_consumer_views_default_unique";

	private const string ProjectionSql = """
		SELECT id, name, platforms, is_default, created_at, updated_at
		FROM consumer_views
		""";

	private readonly string _connectionString;

	public ConsumerViewRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<ConsumerView> CreateAsync(
		string name, IReadOnlyList<string> platforms, bool isDefault, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(platforms);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"""
			INSERT INTO consumer_views (name, platforms, is_default)
			VALUES ($1, $2, $3)
			RETURNING id, name, platforms, is_default, created_at, updated_at
			""", connection);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(platforms.ToArray());
		command.Parameters.AddWithValue(isDefault);

		try
		{
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			return Map(reader);
		}
		catch (PostgresException ex) when (ex.SqlState == "23505")
		{
			throw TranslateUniqueViolation(ex, name);
		}
	}

	public async Task<ConsumerView?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<ConsumerView>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY created_at DESC, id", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<ConsumerView> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<ConsumerView?> UpdateAsync(
		Guid id,
		string? name,
		IReadOnlyList<string>? platforms,
		bool? isDefault,
		CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Atomic, race-safe refusal (issue #1464 AC "exactly one default at any
		// time"): the WHERE clause excludes this row from the update when it is
		// CURRENTLY the default (is_default) and the requested new value would clear
		// it (COALESCE($3, is_default) = false -- COALESCE means an unspecified
		// isDefault ($3 IS NULL) always resolves to the row's own current value, so a
		// write that never touches is_default can never trip this). A row that exists
		// but does not match is indistinguishable at this point from a nonexistent
		// row; ExistsAsync below disambiguates only when needed.
		await using NpgsqlCommand command = new(
			$"""
			UPDATE consumer_views SET
				name = COALESCE($1, name),
				platforms = COALESCE($2, platforms),
				is_default = COALESCE($3, is_default)
			WHERE id = $4 AND NOT (is_default AND COALESCE($3, is_default) = false)
			RETURNING id, name, platforms, is_default, created_at, updated_at
			""", connection);
		command.Parameters.AddWithValue((object?)name ?? DBNull.Value);
		command.Parameters.Add(new NpgsqlParameter
		{
			Value = (object?)platforms?.ToArray() ?? DBNull.Value,
			NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
		});
		command.Parameters.AddWithValue((object?)isDefault ?? DBNull.Value);
		command.Parameters.AddWithValue(id);

		try
		{
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return Map(reader);
			}
		}
		catch (PostgresException ex) when (ex.SqlState == "23505")
		{
			throw TranslateUniqueViolation(ex, name);
		}

		// No row was updated: either id does not exist (existing 404 behaviour,
		// returns null) or the row exists and the refusal above blocked the write
		// (throw so the controller can map it to 409 default_required).
		if (isDefault == false && await ExistsAsync(connection, id, cancellationToken).ConfigureAwait(false))
		{
			throw new ConsumerViewSoleDefaultException(
				"This consumer view is the sole default and cannot have is_default cleared. Mark a different view as the default first.");
		}

		return null;
	}

	public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Atomic, race-safe refusal (issue #1464 AC): never delete a row that is
		// CURRENTLY the default -- a single statement, so a concurrent delete of the
		// same sole default row cannot both succeed.
		await using NpgsqlCommand command = new(
			"DELETE FROM consumer_views WHERE id = $1 AND NOT is_default", connection);
		command.Parameters.AddWithValue(id);
		int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		if (affected > 0)
		{
			return true;
		}

		// Nothing was deleted: either id does not exist (existing 404 behaviour,
		// returns false) or the row exists and is the default (throw for 409).
		if (await ExistsAsync(connection, id, cancellationToken).ConfigureAwait(false))
		{
			throw new ConsumerViewSoleDefaultException(
				"This consumer view is the sole default and cannot be deleted. Mark a different view as the default first.");
		}

		return false;
	}

	private static async Task<bool> ExistsAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("SELECT 1 FROM consumer_views WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null;
	}

	private static Exception TranslateUniqueViolation(PostgresException ex, string? name)
	{
		if (ex.ConstraintName == DefaultUniqueIndex)
		{
			return new ConsumerViewDefaultConflictException();
		}

		if (ex.ConstraintName == NameUniqueIndex)
		{
			return new ConsumerViewNameConflictException(name ?? string.Empty);
		}

		return ex;
	}

	private static ConsumerView Map(NpgsqlDataReader reader)
	{
		return new ConsumerView(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetFieldValue<string[]>(2),
			reader.GetBoolean(3),
			reader.GetFieldValue<DateTimeOffset>(4),
			reader.GetFieldValue<DateTimeOffset>(5));
	}
}
