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
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IRetainedContentStateRepository"/>
public sealed class RetainedContentStateRepository : IRetainedContentStateRepository
{
	private const string ProjectionSql = """
		SELECT id, depot_artifact_id, policy_id, state, grace_started_at,
		       pinned_by, pinned_at, pin_note, purged_at, created_at, updated_at
		FROM download_retained_content_state
		""";

	private readonly string _connectionString;

	public RetainedContentStateRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid> EnsureTrackedAsync(Guid depotArtifactId, Guid? policyId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Genuinely idempotent on the conflict path: the WHERE clause only fires the
		// UPDATE (and therefore the set_updated_at trigger) when a non-null policyId
		// actually differs from the row's current one -- a repeat call with the same
		// (or no) policyId touches nothing and updated_at does not move. A differing
		// non-null policyId is an explicit, documented decision to adopt it (the
		// #1436 sweep re-evaluating with a freshly resolved policy is the caller this
		// serves), not a silent discard.
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO download_retained_content_state (depot_artifact_id, policy_id, state)
			VALUES ($1, $2, $3)
			ON CONFLICT (depot_artifact_id) DO UPDATE SET policy_id = $2
			WHERE $2 IS NOT NULL
			  AND download_retained_content_state.policy_id IS DISTINCT FROM $2
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		command.Parameters.AddWithValue((object?)policyId ?? DBNull.Value);
		command.Parameters.AddWithValue(RetainedContentStates.Tracked);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		if (result is Guid insertedOrUpdatedId)
		{
			return insertedOrUpdatedId;
		}

		// Conflict occurred but the WHERE clause suppressed the UPDATE (idempotent
		// no-op case) -- ON CONFLICT ... WHERE that evaluates false returns no row,
		// so the existing row's id is fetched explicitly rather than treated as
		// "not found".
		await using NpgsqlCommand select = new(
			"SELECT id FROM download_retained_content_state WHERE depot_artifact_id = $1", connection);
		select.Parameters.AddWithValue(depotArtifactId);
		object? existingId = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)existingId!;
	}

	public async Task<RetainedContentState?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<RetainedContentState?> GetByDepotArtifactIdAsync(Guid depotArtifactId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE depot_artifact_id = $1", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task TransitionAsync(Guid id, string toState, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(toState);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		RetainedContentState current = await LoadForUpdateAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
		if (!RetainedContentStateTransitions.CanTransition(current.State, toState))
		{
			throw new InvalidOperationException(
				$"Illegal retained-content-state transition '{current.State}' -> '{toState}' for id {id}.");
		}

		await using (NpgsqlCommand command = new(
			"""
			UPDATE download_retained_content_state SET
				state = $1,
				grace_started_at = CASE WHEN $1 = 'grace' THEN now() ELSE grace_started_at END,
				purged_at = CASE WHEN $1 = 'purged' THEN now() ELSE purged_at END
			WHERE id = $2
			""", connection, transaction))
		{
			command.Parameters.AddWithValue(toState);
			command.Parameters.AddWithValue(id);
			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task PinAsync(Guid id, string pinnedBy, string? note, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pinnedBy);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		RetainedContentState current = await LoadForUpdateAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
		if (!RetainedContentStateTransitions.CanPin(current.State))
		{
			throw new InvalidOperationException(
				$"Cannot pin retained content in state '{current.State}' for id {id}.");
		}

		await using (NpgsqlCommand command = new(
			"""
			UPDATE download_retained_content_state SET
				state = $1,
				pinned_by = $2,
				pinned_at = now(),
				pin_note = $3
			WHERE id = $4
			""", connection, transaction))
		{
			command.Parameters.AddWithValue(RetainedContentStates.Pinned);
			command.Parameters.AddWithValue(pinnedBy);
			command.Parameters.AddWithValue((object?)note ?? DBNull.Value);
			command.Parameters.AddWithValue(id);
			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<RetainedContentState>> ListByStateAsync(string state, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(state);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE state = $1 ORDER BY created_at", connection);
		command.Parameters.AddWithValue(state);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<RetainedContentState> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}
		return items;
	}

	/// <summary>
	/// Loads the row identified by <paramref name="id"/> with <c>SELECT ... FOR
	/// UPDATE</c>, taking a row-level exclusive lock that is held until
	/// <paramref name="transaction"/> commits or rolls back. Makes the
	/// <c>TransitionAsync</c>/<c>PinAsync</c> read-check-write sequence atomic against
	/// a concurrent caller doing the same thing (the #1436 sweep racing a #1453 pin
	/// request is exactly this pair): a second transaction's <c>FOR UPDATE</c> on the
	/// same row blocks until the first commits, then observes the first's write and
	/// re-evaluates <see cref="RetainedContentStateTransitions"/> against the
	/// up-to-date state -- so a write can never land on top of a state the writer
	/// never actually observed (in particular, never past <c>purged</c>).
	/// </summary>
	private static async Task<RetainedContentState> LoadForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1 FOR UPDATE", connection, transaction);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			throw new InvalidOperationException($"No retained-content-state row with id {id}.");
		}
		return Map(reader);
	}

	private static RetainedContentState Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetGuid(1),
		reader.IsDBNull(2) ? null : reader.GetGuid(2),
		reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
		reader.IsDBNull(5) ? null : reader.GetString(5),
		reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
		reader.IsDBNull(7) ? null : reader.GetString(7),
		reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
		reader.GetFieldValue<DateTimeOffset>(9),
		reader.GetFieldValue<DateTimeOffset>(10));
}
