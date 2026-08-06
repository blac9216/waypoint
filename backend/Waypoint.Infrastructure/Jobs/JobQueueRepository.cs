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

using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>Plain-Npgsql implementation of <see cref="IJobQueueRepository"/> -- see that interface for the contract each method keeps.</summary>
public sealed partial class JobQueueRepository : IJobQueueRepository
{
	private readonly string _connectionString;
	private readonly ILogger<JobQueueRepository> _logger;

	// Optional on purpose: the repository is a state store, and events are
	// observability -- but fan-out is the ONLY actor that knows a job was born
	// blocked (#147), so it emits post-commit when a publisher is wired (DI always
	// wires one; tests that do not care may omit it).
	private readonly IJobEventPublisher? _events;

	public JobQueueRepository(string connectionString, ILogger<JobQueueRepository> logger, IJobEventPublisher? events = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_logger = logger;
		_events = events;
	}

	// The claim idiom: lock one claimable row inside a CTE with FOR UPDATE SKIP LOCKED,
	// then UPDATE exactly that row. What makes it safe under concurrency is the
	// ordering-and-locking clause, reproduced here verbatim:
	//
	//     ORDER BY priority, created_at
	//     FOR UPDATE SKIP LOCKED
	//     LIMIT 1
	//
	// That clause -- together with the state = 'queued' predicate -- is what
	// JobsQueueClaimTests (issue #4) proved never double-claims under real concurrency,
	// and it is what idx_jobs_queue_claim is a partial index on.
	//
	// It is NOT true that this query is byte-identical to that test's. PR #126 claimed
	// that and it was wrong; the claim landed in a comment in #134 before the type it
	// referenced existed, so nobody could check it. The two differ deliberately:
	// JobsQueueClaimTests scopes its claim to one run (WHERE run_id = $1 AND ...) so its
	// fixtures cannot be disturbed by rows another test class left in the shared
	// container, and its $1/$2 are run id and claimant. This query is global by design --
	// a dispatcher claims the highest-priority job anywhere in the queue -- and its
	// $1/$2 are the worker id and the lease interval.
	//
	// The part that IS shared is asserted, not asserted-about: see
	// JobQueueClaimSqlParityTests, which normalizes both strings and fails if either
	// side's predicate, ordering or lock clause drifts from the other. Do not edit the
	// clause above without re-reading that test.
	//
	// Everything set in the UPDATE beyond `state` is new relative to that test. Stamping
	// the lease atomically with the claim is what makes the #107 stranded-job state
	// unreachable from this code path; jobs_running_requires_lease_check (0002, merged
	// in #134) is the backstop for every other path, and it now rejects this statement
	// outright if the lease stamp is ever dropped.
	internal const string ClaimSql = """
		WITH claimable AS (
			SELECT id FROM jobs
			WHERE state = 'queued'
			ORDER BY priority, created_at
			FOR UPDATE SKIP LOCKED
			LIMIT 1
		)
		UPDATE jobs SET
			state = 'running',
			claimed_by = $1,
			claimed_at = now(),
			lease_expires_at = now() + $2,
			heartbeat_at = now(),
			attempt_count = attempt_count + 1,
			started_at = COALESCE(started_at, now())
		WHERE id IN (SELECT id FROM claimable)
		RETURNING id, run_id, job_type, target_id, target_name, credential_id, priority, payload::text, attempt_count, max_attempts
		""";

	public async Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(ClaimSql, connection);
		command.Parameters.AddWithValue(workerId);
		command.Parameters.AddWithValue(leaseDuration);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		// Read once into a local rather than calling reader.GetGuid(0) twice. CA1873 flags
		// the method call as a possibly-expensive argument to a logging call that may be
		// discarded -- and it is right in principle even though this particular read is
		// cheap, because the [LoggerMessage] guard cannot elide an argument already
		// evaluated at the call site.
		Guid jobId = reader.GetGuid(0);
		LogJobClaimed(jobId, workerId, leaseDuration);

		return new ClaimedJob(
			Id: jobId,
			RunId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
			JobType: reader.GetString(2),
			TargetId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
			TargetName: reader.IsDBNull(4) ? null : reader.GetString(4),
			CredentialId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
			Priority: reader.GetInt16(6),
			Payload: reader.GetString(7),
			AttemptCount: reader.GetInt32(8),
			MaxAttempts: reader.GetInt32(9));
	}

	public async Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			UPDATE jobs SET heartbeat_at = now(), lease_expires_at = now() + $3
			WHERE id = $1 AND claimed_by = $2 AND state IN ('running', 'attesting', 'converting')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(workerId);
		command.Parameters.AddWithValue(leaseDuration);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null;
	}

	public async Task<bool> AdvanceStateAsync(
		Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedFromState);
		ArgumentException.ThrowIfNullOrWhiteSpace(toState);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			UPDATE jobs SET
				state = $3,
				note = $4,
				finished_at = CASE WHEN $5 THEN now() ELSE finished_at END,
				lease_expires_at = CASE WHEN $5 THEN NULL ELSE lease_expires_at END,
				heartbeat_at = CASE WHEN $5 THEN NULL ELSE heartbeat_at END
			WHERE id = $1 AND claimed_by = $2 AND state = $6
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(workerId);
		command.Parameters.AddWithValue(toState);
		command.Parameters.AddWithValue((object?)note ?? DBNull.Value);
		command.Parameters.AddWithValue(clearLease);
		command.Parameters.AddWithValue(expectedFromState);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null;
	}

	internal const string RecoverSql = """
		WITH recoverable AS (
			SELECT id FROM jobs
			WHERE state = 'running' AND lease_expires_at < now()
			ORDER BY lease_expires_at
			FOR UPDATE SKIP LOCKED
			LIMIT $1
		), classified AS (
			SELECT j.id, COALESCE(r.state = 'aborted', false) AS run_aborted
			FROM jobs j JOIN recoverable q ON q.id = j.id LEFT JOIN runs r ON r.id = j.run_id
		)
		UPDATE jobs j SET
			state = CASE WHEN c.run_aborted THEN 'cancelled' WHEN j.attempt_count < j.max_attempts THEN 'queued' ELSE 'failed' END,
			claimed_by = CASE WHEN NOT c.run_aborted AND j.attempt_count >= j.max_attempts THEN j.claimed_by ELSE NULL END,
			claimed_at = CASE WHEN NOT c.run_aborted AND j.attempt_count >= j.max_attempts THEN j.claimed_at ELSE NULL END,
			lease_expires_at = NULL, heartbeat_at = NULL,
			finished_at = CASE WHEN NOT c.run_aborted AND j.attempt_count < j.max_attempts THEN NULL ELSE now() END,
			note = CASE WHEN c.run_aborted THEN 'Cancelled: run aborted'
				WHEN j.attempt_count < j.max_attempts THEN 'Lease expired; requeued for retry (attempt ' || j.attempt_count || ' of ' || j.max_attempts || ')'
				ELSE 'Lease expired; max attempts (' || j.max_attempts || ') exhausted' END
		FROM classified c WHERE j.id = c.id
		RETURNING j.id, j.run_id, j.state, j.attempt_count, j.max_attempts
		""";

	public async Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
	{
		if (batchSize <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
		}

		if (!JobStateMachine.CanEngineTransition(JobShape.Simple, JobStates.Running, JobStates.Queued))
		{
			throw new InvalidOperationException("The engine transition gate rejects lease recovery.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(RecoverSql, connection);
		command.Parameters.AddWithValue(batchSize);
		List<RecoveredJob> recovered = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			RecoveredJob job = new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
			recovered.Add(job); LogRecoveredJob(job.Id, job.NewState, job.AttemptCount, job.MaxAttempts);
		}
		return recovered;
	}

	public async Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runType);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			INSERT INTO runs (run_type, scope, credential_id, initiated_by, state)
			VALUES ($1, $2::jsonb, $3, $4, 'pending')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(runType);
		command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson);
		command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)initiatedBy ?? DBNull.Value);

		return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
	}

	public async Task<IReadOnlyList<Guid>> FanOutJobsAsync(
		Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(specs);
		if (specs.Count == 0)
		{
			throw new ArgumentException("A run must fan out to at least one job.", nameof(specs));
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand markRunning = new(
			"UPDATE runs SET state = 'running', started_at = COALESCE(started_at, now()) WHERE id = $1 AND state = 'pending'", connection, transaction))
		{
			markRunning.Parameters.AddWithValue(runId);
			int affected = await markRunning.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (affected == 0)
			{
				throw new InvalidOperationException(
					string.Format(CultureInfo.InvariantCulture, "Run '{0}' is not pending; cannot fan out jobs to it.", runId));
			}
		}

		// RETURNING state/note reads the row as stored, i.e. after migration 0005's
		// BEFORE trigger has had its say: a spec against a queue-halted credential
		// comes back 'blocked' (with the halt reason as its note), not 'queued'. The
		// trigger's FOR SHARE on the credentials row is held to the end of this
		// transaction, so one fan-out observes a consistent halt state for all its
		// specs and a concurrent halt serializes entirely before or after it.
		List<Guid> jobIds = new(specs.Count);
		int blockedCount = 0;
		string? blockedNote = null;
		foreach (JobSpec spec in specs)
		{
			await using NpgsqlCommand insertJob = new(
				"""
				INSERT INTO jobs (run_id, job_type, target_id, target_name, credential_id, priority, payload, created_by, state)
				VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, 'queued')
				RETURNING id, state, note
				""", connection, transaction);
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue(spec.JobType);
			insertJob.Parameters.AddWithValue((object?)spec.TargetId ?? DBNull.Value);
			insertJob.Parameters.AddWithValue((object?)spec.TargetName ?? DBNull.Value);
			insertJob.Parameters.AddWithValue((object?)spec.CredentialId ?? DBNull.Value);
			insertJob.Parameters.AddWithValue(spec.Priority);
			insertJob.Parameters.AddWithValue(string.IsNullOrWhiteSpace(spec.Payload) ? "{}" : spec.Payload);
			insertJob.Parameters.AddWithValue((object?)createdBy ?? DBNull.Value);

			await using NpgsqlDataReader reader = await insertJob.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			jobIds.Add(reader.GetGuid(0));
			if (string.Equals(reader.GetString(1), JobStates.Blocked, StringComparison.Ordinal))
			{
				blockedCount++;
				blockedNote ??= reader.IsDBNull(2) ? null : reader.GetString(2);
			}
		}

		// Mirror what CheckConsecutiveAuthFailuresAsync does to runs that already had
		// queued work when the halt tripped: a run that fans out into a halt is blocked
		// with the same reason, so the dispatcher's post-claim guard also stops any of
		// its jobs that are not credential-bound.
		if (blockedCount > 0)
		{
			await using NpgsqlCommand blockRun = new(
				"UPDATE runs SET blocked = true, blocked_reason = $2 WHERE id = $1 AND blocked = false", connection, transaction);
			blockRun.Parameters.AddWithValue(runId);
			blockRun.Parameters.AddWithValue((object?)blockedNote ?? "Credential queue halted");
			await blockRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		if (blockedCount > 0)
		{
			LogFanOutBlocked(runId, blockedCount, jobIds.Count);
			await EmitBornBlockedAsync(runId, blockedCount, jobIds.Count, blockedNote, cancellationToken).ConfigureAwait(false);
		}

		LogFannedOutJobs(runId, jobIds.Count);
		return jobIds;
	}

	public async Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"UPDATE runs SET paused = true WHERE id = $1 AND state IN ('pending', 'running') RETURNING id", connection);
		command.Parameters.AddWithValue(runId);

		bool paused = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
		if (paused)
		{
			LogRunPaused(runId);
		}

		return paused;
	}

	public async Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new("UPDATE runs SET paused = false WHERE id = $1 AND state IN ('pending', 'running') RETURNING id", connection);
		command.Parameters.AddWithValue(runId);

		bool resumed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
		if (resumed)
		{
			LogRunResumed(runId);
		}

		return resumed;
	}

	public async Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT state, paused, blocked, blocked_reason FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
			? new RunQueueState(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
	}

	public async Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT
				r.id, r.run_type, r.state, r.paused, r.blocked, r.blocked_reason,
				r.scope::text,
				r.credential_id, r.initiated_by,
				r.created_at::text, r.started_at::text, r.completed_at::text,
				COUNT(j.id) FILTER (WHERE j.id IS NOT NULL),
				COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state = 'queued'),
				COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state = 'running'),
				COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state = 'done'),
				COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state IN ('failed', 'auth-failed')),
				COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state = 'blocked')
			FROM runs r
			LEFT JOIN jobs j ON j.run_id = r.id
			WHERE r.id = $1
			GROUP BY r.id
			""", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new RunSummary(
			Id: reader.GetGuid(0),
			RunType: reader.GetString(1),
			State: reader.GetString(2),
			Paused: reader.GetBoolean(3),
			Blocked: reader.GetBoolean(4),
			BlockedReason: reader.IsDBNull(5) ? null : reader.GetString(5),
			ScopeJson: reader.GetString(6),
			CredentialId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
			InitiatedBy: reader.IsDBNull(8) ? null : reader.GetString(8),
			CreatedAt: reader.GetString(9),
			StartedAt: reader.IsDBNull(10) ? null : reader.GetString(10),
			CompletedAt: reader.IsDBNull(11) ? null : reader.GetString(11),
			JobCount: reader.GetInt32(12),
			JobCountQueued: reader.GetInt32(13),
			JobCountRunning: reader.GetInt32(14),
			JobCountCompleted: reader.GetInt32(15),
			JobCountFailed: reader.GetInt32(16),
			JobCountBlocked: reader.GetInt32(17));
	}

	public async Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT
				id, run_id, job_type, target_id, target_name,
				state, stage, priority, attempt_count,
				created_at::text, started_at::text, finished_at::text
			FROM jobs
			WHERE run_id = $1
			ORDER BY priority, created_at
			""", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<JobSummary> jobs = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			jobs.Add(new JobSummary(
				Id: reader.GetGuid(0),
				RunId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
				JobType: reader.GetString(2),
				TargetId: reader.IsDBNull(3) ? null : reader.GetString(3),
				TargetName: reader.IsDBNull(4) ? null : reader.GetString(4),
				State: reader.GetString(5),
				Stage: reader.IsDBNull(6) ? null : reader.GetString(6),
				Priority: reader.GetInt16(7),
				AttemptCount: reader.GetInt32(8),
				CreatedAt: reader.GetString(9),
				StartedAt: reader.IsDBNull(10) ? null : reader.GetString(10),
				FinishedAt: reader.IsDBNull(11) ? null : reader.GetString(11)));
		}

		return jobs;
	}

	public async Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

		if (!JobStateMachine.CanEngineTransition(JobShape.Simple, JobStates.Running, JobStates.Queued))
		{
			throw new InvalidOperationException("The engine transition gate rejects claim release.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			UPDATE jobs SET
				state = 'queued',
				claimed_by = NULL,
				claimed_at = NULL,
				lease_expires_at = NULL,
				heartbeat_at = NULL,
				attempt_count = GREATEST(attempt_count - 1, 0)
			WHERE id = $1 AND claimed_by = $2 AND state = 'running'
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(workerId);

		return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
	}

	public async Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand lockRun = new(
			"SELECT state FROM runs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockRun.Parameters.AddWithValue(runId);
			object? state = await lockRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (state is not string currentState ||
				(!string.Equals(currentState, "pending", StringComparison.Ordinal) &&
				 !string.Equals(currentState, "running", StringComparison.Ordinal)))
			{
				return new AbortRunResult([], []);
			}
		}

		List<Guid> cancelledIds = [];
		List<Guid> inFlightIds = [];
		await using (NpgsqlCommand snapshotJobs = new(
			"SELECT id, state FROM jobs WHERE run_id = $1 FOR UPDATE", connection, transaction))
		{
			snapshotJobs.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await snapshotJobs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				string state = reader.GetString(1);
				if (string.Equals(state, JobStates.Queued, StringComparison.Ordinal) || string.Equals(state, JobStates.Blocked, StringComparison.Ordinal))
				{
					cancelledIds.Add(reader.GetGuid(0));
				}
				else if (string.Equals(state, JobStates.Running, StringComparison.Ordinal) ||
					string.Equals(state, JobStates.Attesting, StringComparison.Ordinal) ||
					string.Equals(state, JobStates.Converting, StringComparison.Ordinal))
				{
					inFlightIds.Add(reader.GetGuid(0));
				}
			}
		}

		await using (NpgsqlCommand markAborted = new(
			"UPDATE runs SET state = 'aborted', paused = false, completed_at = now() WHERE id = $1", connection, transaction))
		{
			markAborted.Parameters.AddWithValue(runId);
			await markAborted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Migration 0003's run-side trigger has already cancelled queued rows in this
		// transaction. Blocked rows are not queued, so finish those explicitly.
		await using (NpgsqlCommand cancelBlocked = new(
			"""
			UPDATE jobs SET state = 'cancelled', finished_at = now(), note = 'Cancelled: run aborted'
			WHERE run_id = $1 AND state = 'blocked'
			""", connection, transaction))
		{
			cancelBlocked.Parameters.AddWithValue(runId);
			await cancelBlocked.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogRunAborted(runId, cancelledIds.Count, inFlightIds.Count);
		return new AbortRunResult(cancelledIds, inFlightIds);
	}

	public async Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken)
	{
		if (threshold <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be positive.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Serialize checks for one credential and keep the resolved-outcome snapshot
		// in the same transaction as the block writes. Unresolved jobs have no outcome
		// and cannot displace a failure; id DESC makes equal finish times total-order.
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await using (NpgsqlCommand lockCredential = new("SELECT id FROM credentials WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockCredential.Parameters.AddWithValue(credentialId);
			if (await lockCredential.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
			{
				return new AuthFailureHaltResult(HaltTripped: false, [], []);
			}
		}

		bool tripped;
		await using (NpgsqlCommand recent = new(
			"SELECT state FROM jobs WHERE credential_id = $1 AND finished_at IS NOT NULL ORDER BY finished_at DESC, id DESC LIMIT $2", connection, transaction))
		{
			recent.Parameters.AddWithValue(credentialId);
			recent.Parameters.AddWithValue(threshold);

			int seen = 0;
			bool allAuthFailed = true;
			await using NpgsqlDataReader reader = await recent.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				seen++;
				if (!string.Equals(reader.GetString(0), JobStates.AuthFailed, StringComparison.Ordinal))
				{
					allAuthFailed = false;
				}
			}

			// "N consecutive" requires at least N jobs to exist for this credential at
			// all -- fewer than that can never trip the halt regardless of their state.
			tripped = seen == threshold && allAuthFailed;
		}

		if (!tripped)
		{
			return new AuthFailureHaltResult(HaltTripped: false, [], []);
		}

		string reason = string.Format(
			CultureInfo.InvariantCulture,
			"{0} consecutive auth failures against this credential; queue halted pending a credential swap.",
			threshold);

		// Persist the halt on the credential itself, inside the same transaction that
		// blocks the queued rows. This is what makes the halt a durable state rather
		// than an event: migration 0005's trigger coerces any later 'queued' write for
		// this credential (a post-halt fan-out, a lease-recovery requeue, a claim
		// release) to 'blocked' until an explicit credential-swap/unblock flow clears
		// queue_halted. The row is already FOR UPDATE-locked above.
		await using (NpgsqlCommand haltCredential = new(
			"UPDATE credentials SET queue_halted = true, queue_halted_reason = $2, queue_halted_at = COALESCE(queue_halted_at, now()) WHERE id = $1", connection, transaction))
		{
			haltCredential.Parameters.AddWithValue(credentialId);
			haltCredential.Parameters.AddWithValue(reason);
			await haltCredential.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Idempotent under concurrent callers by construction: both queries below only
		// touch rows still in 'queued', so a second, redundant call (two workers
		// finishing an auth-failed job around the same moment) finds nothing left to
		// change and returns an empty result the second time.
		List<Guid> blockedRunIds = [];
		await using (NpgsqlCommand blockRuns = new(
			"""
			UPDATE runs SET blocked = true, blocked_reason = $2
			WHERE id IN (SELECT DISTINCT run_id FROM jobs WHERE credential_id = $1 AND state = 'queued' AND run_id IS NOT NULL)
			AND blocked = false
			RETURNING id
			""", connection, transaction))
		{
			blockRuns.Parameters.AddWithValue(credentialId);
			blockRuns.Parameters.AddWithValue(reason);
			await using NpgsqlDataReader reader = await blockRuns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				blockedRunIds.Add(reader.GetGuid(0));
			}
		}

		List<Guid> blockedJobIds = [];
		await using (NpgsqlCommand blockJobs = new(
			"""
			UPDATE jobs SET state = 'blocked', note = $2
			WHERE credential_id = $1 AND state = 'queued'
			RETURNING id
			""", connection, transaction))
		{
			blockJobs.Parameters.AddWithValue(credentialId);
			blockJobs.Parameters.AddWithValue(reason);
			await using NpgsqlDataReader reader = await blockJobs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				blockedJobIds.Add(reader.GetGuid(0));
			}
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogAuthFailureHalt(credentialId, threshold, blockedRunIds.Count, blockedJobIds.Count);
		return new AuthFailureHaltResult(HaltTripped: true, blockedRunIds, blockedJobIds);
	}

	/// <summary>Issue #146: inverse of <see cref="CheckConsecutiveAuthFailuresAsync"/> — clear
	/// <c>credentials.queue_halted</c> and transition that credential's <c>blocked</c>
	/// jobs back to <c>queued</c> (their runs unblocked) in one transaction, serialized
	/// against the halt's <c>FOR UPDATE</c> idiom so a concurrent halt and unblock
	/// serialize to a consistent end state.</summary>
	public async Task<CredentialUnblockResult> UnblockCredentialAsync(Guid credentialId, string? reason, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Lock the credential row — the same FOR UPDATE the halt uses — so a concurrent
		// halt and unblock serialize. Read the current queue_halted flag to decide
		// whether this is a no-op.
		bool wasHalted;
		await using (NpgsqlCommand lockAndRead = new(
			"SELECT queue_halted FROM credentials WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockAndRead.Parameters.AddWithValue(credentialId);
			object? result = await lockAndRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is null)
			{
				return new CredentialUnblockResult(WasHalted: false, [], []);
			}
			wasHalted = (bool)result;
		}

		if (!wasHalted)
		{
			return new CredentialUnblockResult(WasHalted: false, [], []);
		}

		// Clear the halt. The row is already FOR UPDATE-locked above.
		await using (NpgsqlCommand clearHalt = new(
			"UPDATE credentials SET queue_halted = false, queue_halted_reason = NULL, queue_halted_at = NULL WHERE id = $1", connection, transaction))
		{
			clearHalt.Parameters.AddWithValue(credentialId);
			await clearHalt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Unblock blocked jobs for this credential — transition them back to 'queued'.
		// The trigger (migration 0005) will no longer coerce them because queue_halted
		// is now false.
		List<Guid> unblockedJobIds = [];
		await using (NpgsqlCommand unblockJobs = new(
			"""
			UPDATE jobs SET state = 'queued', note = $2
			WHERE credential_id = $1 AND state = 'blocked'
			RETURNING id
			""", connection, transaction))
		{
			unblockJobs.Parameters.AddWithValue(credentialId);
			unblockJobs.Parameters.AddWithValue(reason ?? "Credential queue unblocked");
			await using NpgsqlDataReader reader = await unblockJobs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				unblockedJobIds.Add(reader.GetGuid(0));
			}
		}

		// Unblock runs that were blocked because of this credential's halt.
		List<Guid> unblockedRunIds = [];
		await using (NpgsqlCommand unblockRuns = new(
			"""
			UPDATE runs SET blocked = false, blocked_reason = NULL
			WHERE id IN (SELECT DISTINCT run_id FROM jobs WHERE credential_id = $1 AND run_id IS NOT NULL)
			AND blocked = true
			RETURNING id
			""", connection, transaction))
		{
			unblockRuns.Parameters.AddWithValue(credentialId);
			await using NpgsqlDataReader reader = await unblockRuns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				unblockedRunIds.Add(reader.GetGuid(0));
			}
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogCredentialUnblocked(credentialId, unblockedRunIds.Count, unblockedJobIds.Count);
		return new CredentialUnblockResult(WasHalted: true, unblockedRunIds, unblockedJobIds);
	}

	/// <summary>#147: a run fanned out into an already-halted credential is otherwise
	/// invisible on SSE -- no auth failure occurs, so the dispatcher's halt path never
	/// runs. Emitted after the transaction committed ("nothing follows an emit in its
	/// transaction"), best-effort like every event.</summary>
	private async Task EmitBornBlockedAsync(Guid runId, int blockedCount, int jobCount, string? reason, CancellationToken cancellationToken)
	{
		if (_events is null)
		{
			return;
		}

		string payload = System.Text.Json.JsonSerializer.Serialize(new
		{
			blocked = true,
			born_blocked_job_count = blockedCount,
			job_count = jobCount,
			reason
		});
		await _events.EmitAsync(JobEventTypes.QueueState, null, runId, payload, cancellationToken).ConfigureAwait(false);
		await _events.EmitAsync(JobEventTypes.SystemNotice, null, null, payload, cancellationToken).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Fanned out {JobCount} job(s) for run {RunId}")]
	private partial void LogFannedOutJobs(Guid runId, int jobCount);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} fanned out into a credential queue halt: {BlockedCount} of {JobCount} job(s) created blocked; run blocked")]
	private partial void LogFanOutBlocked(Guid runId, int blockedCount, int jobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} paused")]
	private partial void LogRunPaused(Guid runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} resumed")]
	private partial void LogRunResumed(Guid runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} aborted: {CancelledCount} queued job(s) cancelled, {InFlightCount} in-flight job(s) signaled")]
	private partial void LogRunAborted(Guid runId, int cancelledCount, int inFlightCount);

	[LoggerMessage(Level = LogLevel.Error, Message = "Credential {CredentialId} hit {Threshold} consecutive auth failures: {BlockedRunCount} run(s) and {BlockedJobCount} queued job(s) blocked")]
	private partial void LogAuthFailureHalt(Guid credentialId, int threshold, int blockedRunCount, int blockedJobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Credential {CredentialId} queue unblocked: {UnblockedRunCount} run(s) and {UnblockedJobCount} blocked job(s) released to queued")]
	private partial void LogCredentialUnblocked(Guid credentialId, int unblockedRunCount, int unblockedJobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Recovered job {JobId} to {NewState} after expired lease (attempt {AttemptCount}/{MaxAttempts})")]
	private partial void LogRecoveredJob(Guid jobId, string newState, int attemptCount, int maxAttempts);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Claimed job {JobId} for worker {WorkerId} with a {LeaseDuration} lease")]
	private partial void LogJobClaimed(Guid jobId, string workerId, TimeSpan leaseDuration);
}
