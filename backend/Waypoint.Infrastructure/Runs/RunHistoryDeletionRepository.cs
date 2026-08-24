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
using Waypoint.Core.Runs;

namespace Waypoint.Infrastructure.Runs;

/// <inheritdoc cref="IRunHistoryDeletionRepository"/>
public sealed class RunHistoryDeletionRepository : IRunHistoryDeletionRepository
{
	private readonly string _connectionString;

	public RunHistoryDeletionRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<RunHistoryDeletionTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, run_id, run_type, prior_state, actor, outcome, detail::text, occurred_at
			FROM run_history_deletion_tombstones
			WHERE run_id = $1
			""", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return ReadTombstone(reader);
	}

	public async Task<bool> IsPurgedAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT purged_at IS NOT NULL FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is true;
	}

	public async Task<RunHistoryDeletionTombstone> CompleteAsync(
		Guid runId, string runType, string actor, string priorState, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runType);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);
		ArgumentException.ThrowIfNullOrWhiteSpace(priorState);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string detailJson = "{}";

		Guid tombstoneId;
		DateTimeOffset occurredAt;
		await using (NpgsqlCommand insertTombstone = new(
			"""
			INSERT INTO run_history_deletion_tombstones (run_id, run_type, prior_state, actor, outcome, detail)
			VALUES ($1, $2, $3, $4, 'completed', $5::jsonb)
			ON CONFLICT (run_id) DO NOTHING
			RETURNING id, occurred_at
			""", connection, transaction))
		{
			insertTombstone.Parameters.AddWithValue(runId);
			insertTombstone.Parameters.AddWithValue(runType);
			insertTombstone.Parameters.AddWithValue(priorState);
			insertTombstone.Parameters.AddWithValue(actor);
			insertTombstone.Parameters.AddWithValue(detailJson);

			await using NpgsqlDataReader reader = await insertTombstone.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				tombstoneId = reader.GetGuid(0);
				occurredAt = reader.GetFieldValue<DateTimeOffset>(1);
			}
			else
			{
				// ON CONFLICT DO NOTHING with no RETURNING row: a tombstone already
				// exists (a racing/duplicate call) -- the UNIQUE constraint makes this
				// safe to no-op. Fall through and read it back below, outside the
				// transaction (no further writes needed).
				tombstoneId = Guid.Empty;
				occurredAt = default;
			}
		}

		await using (NpgsqlCommand markDeleted = new(
			"UPDATE runs SET history_deleted_at = now() WHERE id = $1 AND history_deleted_at IS NULL", connection, transaction))
		{
			markDeleted.Parameters.AddWithValue(runId);
			await markDeleted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// AC (b): sever the one cross-domain reference the issue names by name --
		// schedules.last_run_id -- the same "FK is a backstop, not the enforcement
		// point" idiom 0037/0041/0042 already established for this exact column.
		await using (NpgsqlCommand nullScheduleRef = new(
			"UPDATE schedules SET last_run_id = NULL WHERE last_run_id = $1", connection, transaction))
		{
			nullScheduleRef.Parameters.AddWithValue(runId);
			await nullScheduleRef.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (tombstoneId != Guid.Empty)
		{
			return new RunHistoryDeletionTombstone(tombstoneId, runId, runType, priorState, actor, "completed", detailJson, occurredAt);
		}

		RunHistoryDeletionTombstone? existing = await GetTombstoneAsync(runId, cancellationToken).ConfigureAwait(false);
		return existing ?? throw new InvalidOperationException(
			$"run_history_deletion_tombstones row for run '{runId}' vanished between the conflicting INSERT and the immediate re-read.");
	}

	private static RunHistoryDeletionTombstone ReadTombstone(NpgsqlDataReader reader) => new(
		Id: reader.GetGuid(0),
		RunId: reader.GetGuid(1),
		RunType: reader.GetString(2),
		PriorState: reader.GetString(3),
		Actor: reader.GetString(4),
		Outcome: reader.GetString(5),
		DetailJson: reader.GetString(6),
		OccurredAt: reader.GetFieldValue<DateTimeOffset>(7));
}
