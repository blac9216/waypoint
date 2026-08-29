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

/// <inheritdoc cref="IRunPurgeRepository"/>
public sealed class RunPurgeRepository : IRunPurgeRepository
{
	private readonly string _connectionString;

	public RunPurgeRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid?> GetArtifactJobIdAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT artifact_job_id FROM run_purges WHERE run_id = $1", connection);
		command.Parameters.AddWithValue(runId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid jobId ? jobId : null;
	}

	public async Task<Guid?> FindRunIdByArtifactJobIdAsync(Guid artifactJobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT run_id FROM run_purges WHERE artifact_job_id = $1", connection);
		command.Parameters.AddWithValue(artifactJobId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid runId ? runId : null;
	}

	public async Task<RunPurgeStatus?> GetStatusAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT run_id, requested_by, requested_at, prior_state, db_phase_done,
			       artifacts_phase, artifacts_total, artifacts_deleted, last_error, completed_at
			FROM run_purges
			WHERE run_id = $1
			""", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return ReadStatus(reader);
	}

	public async Task<IReadOnlyList<Guid>> ListPendingFinalizeRunIdsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT run_id FROM run_purges WHERE db_phase_done AND artifacts_phase = 'done' ORDER BY requested_at",
			connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<Guid> runIds = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			runIds.Add(reader.GetGuid(0));
		}

		return runIds;
	}

	public async Task<RunPurgeTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, run_id, run_type, prior_state, actor, outcome, detail::text, occurred_at
			FROM run_purge_tombstones
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

	public async Task<RunPurgeStatus> CreateAsync(Guid runId, string requestedBy, string priorState, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
		ArgumentException.ThrowIfNullOrWhiteSpace(priorState);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO run_purges (run_id, requested_by, prior_state)
			VALUES ($1, $2, $3)
			ON CONFLICT (run_id) DO NOTHING
			RETURNING run_id, requested_by, requested_at, prior_state, db_phase_done,
			          artifacts_phase, artifacts_total, artifacts_deleted, last_error, completed_at
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(requestedBy);
		command.Parameters.AddWithValue(priorState);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return ReadStatus(reader);
		}

		// ON CONFLICT DO NOTHING with no RETURNING row: a row already existed. Read it
		// back rather than assume the caller's inputs match it -- see this method's
		// interface doc comment.
		RunPurgeStatus? existing = await GetStatusAsync(runId, cancellationToken).ConfigureAwait(false);
		return existing ?? throw new InvalidOperationException(
			$"run_purges row for run '{runId}' vanished between the conflicting INSERT and the immediate re-read.");
	}

	public async Task MarkDbPhaseDoneAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE run_purges SET db_phase_done = true WHERE run_id = $1", connection);
		command.Parameters.AddWithValue(runId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task MarkArtifactJobEnqueuedAsync(Guid runId, Guid jobId, int artifactsTotal, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE run_purges
			SET artifact_job_id = $2, artifacts_phase = 'running', artifacts_total = $3,
			    artifacts_deleted = 0, last_error = NULL
			WHERE run_id = $1
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(artifactsTotal);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task ReportArtifactOutcomeAsync(Guid runId, bool succeeded, int artifactsDeleted, string? lastError, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE run_purges
			SET artifacts_phase = $2, artifacts_deleted = $3, last_error = $4,
			    completed_at = CASE WHEN $2 = 'done' THEN now() ELSE completed_at END
			WHERE run_id = $1
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(succeeded ? "done" : "failed");
		command.Parameters.AddWithValue(artifactsDeleted);
		command.Parameters.AddWithValue((object?)lastError ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<RunPurgeTombstone> CompleteAsync(
		Guid runId, string runType, string actor, string priorState, int artifactsDeleted, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runType);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);
		ArgumentException.ThrowIfNullOrWhiteSpace(priorState);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string detailJson = JsonSerializer.Serialize(new { artifacts_deleted = artifactsDeleted });

		Guid tombstoneId;
		DateTimeOffset occurredAt;
		await using (NpgsqlCommand insertTombstone = new(
			"""
			INSERT INTO run_purge_tombstones (run_id, run_type, prior_state, actor, outcome, detail)
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
				// exists for this run (a racing/duplicate CompleteAsync call) -- the
				// run_purge_tombstones_run_id_key UNIQUE constraint makes this safe to
				// no-op rather than double-write. Fall through and read it back below.
				tombstoneId = Guid.Empty;
				occurredAt = default;
			}
		}

		await using (NpgsqlCommand deletePurgeRow = new("DELETE FROM run_purges WHERE run_id = $1", connection, transaction))
		{
			deletePurgeRow.Parameters.AddWithValue(runId);
			await deletePurgeRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand markPurged = new("UPDATE runs SET purged_at = now() WHERE id = $1 AND purged_at IS NULL", connection, transaction))
		{
			markPurged.Parameters.AddWithValue(runId);
			await markPurged.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (tombstoneId != Guid.Empty)
		{
			return new RunPurgeTombstone(tombstoneId, runId, runType, priorState, actor, "completed", detailJson, occurredAt);
		}

		RunPurgeTombstone? existing = await GetTombstoneAsync(runId, cancellationToken).ConfigureAwait(false);
		return existing ?? throw new InvalidOperationException(
			$"run_purge_tombstones row for run '{runId}' vanished between the conflicting INSERT and the immediate re-read.");
	}

	private static RunPurgeStatus ReadStatus(NpgsqlDataReader reader) => new(
		RunId: reader.GetGuid(0),
		RequestedBy: reader.GetString(1),
		RequestedAt: reader.GetFieldValue<DateTimeOffset>(2),
		PriorState: reader.GetString(3),
		DbPhaseDone: reader.GetBoolean(4),
		ArtifactsPhase: reader.GetString(5),
		ArtifactsTotal: reader.GetInt32(6),
		ArtifactsDeleted: reader.GetInt32(7),
		LastError: reader.IsDBNull(8) ? null : reader.GetString(8),
		CompletedAt: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));

	private static RunPurgeTombstone ReadTombstone(NpgsqlDataReader reader) => new(
		Id: reader.GetGuid(0),
		RunId: reader.GetGuid(1),
		RunType: reader.GetString(2),
		PriorState: reader.GetString(3),
		Actor: reader.GetString(4),
		Outcome: reader.GetString(5),
		DetailJson: reader.GetString(6),
		OccurredAt: reader.GetFieldValue<DateTimeOffset>(7));
}
