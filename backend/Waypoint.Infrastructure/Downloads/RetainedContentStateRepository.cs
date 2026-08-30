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
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO download_retained_content_state (depot_artifact_id, policy_id, state)
			VALUES ($1, $2, $3)
			ON CONFLICT (depot_artifact_id) DO UPDATE SET depot_artifact_id = EXCLUDED.depot_artifact_id
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		command.Parameters.AddWithValue((object?)policyId ?? DBNull.Value);
		command.Parameters.AddWithValue(RetainedContentStates.Tracked);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
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

		RetainedContentState current = await LoadForUpdateAsync(connection, id, cancellationToken).ConfigureAwait(false);
		if (!RetainedContentStateTransitions.CanTransition(current.State, toState))
		{
			throw new InvalidOperationException(
				$"Illegal retained-content-state transition '{current.State}' -> '{toState}' for id {id}.");
		}

		await using NpgsqlCommand command = new(
			"""
			UPDATE download_retained_content_state SET
				state = $1,
				grace_started_at = CASE WHEN $1 = 'grace' THEN now() ELSE grace_started_at END,
				purged_at = CASE WHEN $1 = 'purged' THEN now() ELSE purged_at END
			WHERE id = $2
			""", connection);
		command.Parameters.AddWithValue(toState);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task PinAsync(Guid id, string pinnedBy, string? note, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pinnedBy);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		RetainedContentState current = await LoadForUpdateAsync(connection, id, cancellationToken).ConfigureAwait(false);
		if (!RetainedContentStateTransitions.CanPin(current.State))
		{
			throw new InvalidOperationException(
				$"Cannot pin retained content in state '{current.State}' for id {id}.");
		}

		await using NpgsqlCommand command = new(
			"""
			UPDATE download_retained_content_state SET
				state = $1,
				pinned_by = $2,
				pinned_at = now(),
				pin_note = $3
			WHERE id = $4
			""", connection);
		command.Parameters.AddWithValue(RetainedContentStates.Pinned);
		command.Parameters.AddWithValue(pinnedBy);
		command.Parameters.AddWithValue((object?)note ?? DBNull.Value);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

	private static async Task<RetainedContentState> LoadForUpdateAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
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
