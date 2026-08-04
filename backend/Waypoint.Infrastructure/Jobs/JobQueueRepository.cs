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

	public JobQueueRepository(string connectionString, ILogger<JobQueueRepository> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_logger = logger;
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

		List<Guid> jobIds = new(specs.Count);
		foreach (JobSpec spec in specs)
		{
			await using NpgsqlCommand insertJob = new(
				"""
				INSERT INTO jobs (run_id, job_type, target_id, target_name, credential_id, priority, payload, created_by, state)
				VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, 'queued')
				RETURNING id
				""", connection, transaction);
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue(spec.JobType);
			insertJob.Parameters.AddWithValue((object?)spec.TargetId ?? DBNull.Value);
			insertJob.Parameters.AddWithValue((object?)spec.TargetName ?? DBNull.Value);
			insertJob.Parameters.AddWithValue((object?)spec.CredentialId ?? DBNull.Value);
			insertJob.Parameters.AddWithValue(spec.Priority);
			insertJob.Parameters.AddWithValue(string.IsNullOrWhiteSpace(spec.Payload) ? "{}" : spec.Payload);
			insertJob.Parameters.AddWithValue((object?)createdBy ?? DBNull.Value);

			jobIds.Add((Guid)(await insertJob.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
				return new AuthFailureHaltResult([], []);
			}
		}

		bool tripped;
		await using (NpgsqlCommand recent = new(
			"SELECT state FROM jobs WHERE credential_id = $1 ORDER BY COALESCE(finished_at, created_at) DESC LIMIT $2", connection, transaction))
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
			return new AuthFailureHaltResult([], []);
		}

		string reason = string.Format(
			CultureInfo.InvariantCulture,
			"{0} consecutive auth failures against this credential; queue halted pending a credential swap.",
			threshold);

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
		return new AuthFailureHaltResult(blockedRunIds, blockedJobIds);
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Fanned out {JobCount} job(s) for run {RunId}")]
	private partial void LogFannedOutJobs(Guid runId, int jobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} paused")]
	private partial void LogRunPaused(Guid runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} resumed")]
	private partial void LogRunResumed(Guid runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} aborted: {CancelledCount} queued job(s) cancelled, {InFlightCount} in-flight job(s) signaled")]
	private partial void LogRunAborted(Guid runId, int cancelledCount, int inFlightCount);

	[LoggerMessage(Level = LogLevel.Error, Message = "Credential {CredentialId} hit {Threshold} consecutive auth failures: {BlockedRunCount} run(s) and {BlockedJobCount} queued job(s) blocked")]
	private partial void LogAuthFailureHalt(Guid credentialId, int threshold, int blockedRunCount, int blockedJobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Recovered job {JobId} to {NewState} after expired lease (attempt {AttemptCount}/{MaxAttempts})")]
	private partial void LogRecoveredJob(Guid jobId, string newState, int attemptCount, int maxAttempts);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Claimed job {JobId} for worker {WorkerId} with a {LeaseDuration} lease")]
	private partial void LogJobClaimed(Guid jobId, string workerId, TimeSpan leaseDuration);
}
