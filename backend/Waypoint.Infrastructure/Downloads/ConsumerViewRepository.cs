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

	// Advisory-lock key serialising default-view TRANSFERS against each other (issue
	// #1464 AC "exactly one default at any time"; PR #1816 round 2 Spec finding 1). A
	// transfer is demote-incumbent-then-promote-target inside one transaction; two
	// concurrent transfers that interleaved their scans could each miss the other's
	// not-yet-committed promotion and race the partial unique index into a 23505.
	// Taking this transaction-scoped advisory lock first makes transfers strictly
	// serial, so the 23505 path stays a genuine belt-and-braces backstop (a raw
	// writer that never took the lock) rather than a routine outcome. Same
	// pg_advisory_*-lock idiom as NpgsqlSchemaMigrator's own migration lock; the key
	// is this table's owning issue number and is not shared with any other call site.
	private const long DefaultTransferLockKey = 1464L;

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

		// Creating a row with is_default = true is a TRANSFER of the default, not a
		// conflict (issue #1464 AC 2 "exactly one view CAN BE MARKED as the default at
		// any time"; PR #1816 round 2 Spec finding 1): inside ONE transaction the
		// incumbent default is demoted and the new row is inserted already-default, so
		// the table is never observable by another session with zero or two defaults.
		// Non-default creates need no transaction at all and keep the single-statement
		// path.
		await using NpgsqlTransaction? transaction = isDefault
			? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
			: null;
		if (transaction is not null)
		{
			await LockDefaultTransferAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
			await DemoteCurrentDefaultAsync(connection, transaction, exceptId: null, cancellationToken).ConfigureAwait(false);
		}

		await using NpgsqlCommand command = new(
			$"""
			INSERT INTO consumer_views (name, platforms, is_default)
			VALUES ($1, $2, $3)
			RETURNING id, name, platforms, is_default, created_at, updated_at
			""", connection, transaction);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(platforms.ToArray());
		command.Parameters.AddWithValue(isDefault);

		try
		{
			ConsumerView created;
			await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
			{
				await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
				created = Map(reader);
			}

			// Nothing is committed until here, so a failed insert (duplicate name,
			// cancellation) rolls the demotion back with it -- the incumbent keeps the
			// default rather than the table being left with none.
			if (transaction is not null)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			}

			return created;
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

		// is_default: true is a TRANSFER of the default to this row (issue #1464 AC 2;
		// PR #1816 round 2 Spec finding 1) -- inside ONE transaction the incumbent
		// default (if any, and if it is not this row already) is demoted first and this
		// row is promoted second, so no other session ever observes zero or two
		// defaults. Any other write (is_default false or unspecified) needs no
		// transaction and keeps the single-statement path below.
		await using NpgsqlTransaction? transaction = isDefault == true
			? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
			: null;
		if (transaction is not null)
		{
			await LockDefaultTransferAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
			await DemoteCurrentDefaultAsync(connection, transaction, exceptId: id, cancellationToken).ConfigureAwait(false);
		}

		// Atomic, race-safe refusal (issue #1464 AC "exactly one default at any
		// time"): the WHERE clause excludes this row from the update when it is
		// CURRENTLY the default (is_default) and the requested new value would clear
		// it (COALESCE($3, is_default) = false -- COALESCE means an unspecified
		// isDefault ($3 IS NULL) always resolves to the row's own current value, so a
		// write that never touches is_default can never trip this). It never blocks a
		// promotion ($3 = true), which is the transfer path above. A row that exists
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
			""", connection, transaction);
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
			ConsumerView? updated = null;
			await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
			{
				if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					updated = Map(reader);
				}
			}

			if (updated is not null)
			{
				if (transaction is not null)
				{
					await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				}

				return updated;
			}

			// On the transfer path a missing row means the caller asked to promote an
			// id that does not exist: roll the demotion back rather than leaving the
			// table with no default at all, then fall through to the 404 return below.
			if (transaction is not null)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
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

	/// <summary>
	/// Serialises default-view transfers (see <see cref="DefaultTransferLockKey"/>).
	/// Transaction-scoped, so it is released by the COMMIT/ROLLBACK that follows it and
	/// can never be leaked by an early return or a thrown exception.
	/// </summary>
	private static async Task LockDefaultTransferAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("SELECT pg_advisory_xact_lock($1)", connection, transaction);
		command.Parameters.AddWithValue(DefaultTransferLockKey);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Clears <c>is_default</c> on whichever row currently holds it, skipping
	/// <paramref name="exceptId"/> (the row about to be promoted, so re-promoting the
	/// existing default is a no-op rather than a demote-then-promote of the same row).
	/// Only ever called inside the caller's transfer transaction: the zero-default
	/// state it creates exists solely between this statement and the promotion that
	/// follows, and is never visible to another session.
	/// </summary>
	private static async Task DemoteCurrentDefaultAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? exceptId, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"UPDATE consumer_views SET is_default = false WHERE is_default AND ($1 IS NULL OR id <> $1)",
			connection,
			transaction);
		command.Parameters.Add(new NpgsqlParameter
		{
			Value = (object?)exceptId ?? DBNull.Value,
			NpgsqlDbType = NpgsqlDbType.Uuid,
		});
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
