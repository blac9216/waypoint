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

	// The predicate/order/lock clause below (state = 'queued' ... ORDER BY priority,
	// created_at FOR UPDATE SKIP LOCKED) is byte-identical to the query
	// JobsQueueClaimTests (issue #4) proved never double-claims under real concurrency,
	// and matches idx_jobs_queue_claim exactly. Do not edit that clause without
	// re-running that proof (JobQueueClaimConcurrencyTests carries it forward against
	// this method). Everything set in the UPDATE beyond `state` is new: stamping the
	// lease atomically with the claim is what makes the #107 stranded-job state
	// unreachable from this code path (the CHECK constraint is the backstop for every
	// other path).
	private const string ClaimSql = """
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

		return new ClaimedJob(
			Id: reader.GetGuid(0),
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

	// FOR UPDATE SKIP LOCKED here for the same reason as the claim query: two
	// concurrent recovery sweeps (two dispatcher replicas both running the
	// LeaseRecoveryHostedService) must never process the same expired-lease row twice.
	// Every column that leaves 'running' is cleared in the same statement the state
	// changes in, so a recovered row can never itself violate
	// jobs_running_requires_lease_check.
	private const string RecoverSql = """
		WITH recoverable AS (
			SELECT id FROM jobs
			WHERE state = 'running' AND lease_expires_at < now()
			ORDER BY lease_expires_at
			FOR UPDATE SKIP LOCKED
			LIMIT $1
		)
		UPDATE jobs SET
			state = CASE WHEN attempt_count < max_attempts THEN 'queued' ELSE 'failed' END,
			claimed_by = CASE WHEN attempt_count < max_attempts THEN NULL ELSE claimed_by END,
			claimed_at = CASE WHEN attempt_count < max_attempts THEN NULL ELSE claimed_at END,
			lease_expires_at = NULL,
			heartbeat_at = NULL,
			finished_at = CASE WHEN attempt_count < max_attempts THEN NULL ELSE now() END,
			note = CASE WHEN attempt_count < max_attempts
				THEN 'Lease expired; requeued for retry (attempt ' || attempt_count || ' of ' || max_attempts || ')'
				ELSE 'Lease expired; max attempts (' || max_attempts || ') exhausted' END
		WHERE id IN (SELECT id FROM recoverable)
		RETURNING id, run_id, state, attempt_count, max_attempts
		""";

	public async Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
	{
		if (batchSize <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(RecoverSql, connection);
		command.Parameters.AddWithValue(batchSize);

		List<RecoveredJob> recovered = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			RecoveredJob job = new(
				Id: reader.GetGuid(0),
				RunId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
				NewState: reader.GetString(2),
				AttemptCount: reader.GetInt32(3),
				MaxAttempts: reader.GetInt32(4));
			recovered.Add(job);
			LogRecoveredJob(job.Id, job.NewState, job.AttemptCount, job.MaxAttempts);
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
			"UPDATE runs SET state = 'running', started_at = COALESCE(started_at, now()) WHERE id = $1", connection, transaction))
		{
			markRunning.Parameters.AddWithValue(runId);
			int affected = await markRunning.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (affected == 0)
			{
				throw new InvalidOperationException(
					string.Format(CultureInfo.InvariantCulture, "Run '{0}' does not exist; cannot fan out jobs to it.", runId));
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

		await using NpgsqlCommand command = new("UPDATE runs SET paused = false WHERE id = $1 RETURNING id", connection);
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

		await using NpgsqlCommand command = new("SELECT paused, blocked, blocked_reason FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new RunQueueState(
			Paused: reader.GetBoolean(0),
			Blocked: reader.GetBoolean(1),
			BlockedReason: reader.IsDBNull(2) ? null : reader.GetString(2));
	}

	public async Task<string?> GetRunStateAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new("SELECT state FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result as string;
	}

	public async Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

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

		await using (NpgsqlCommand markAborted = new(
			"UPDATE runs SET state = 'aborted', completed_at = now() WHERE id = $1 AND state IN ('pending', 'running')", connection, transaction))
		{
			markAborted.Parameters.AddWithValue(runId);
			await markAborted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		List<Guid> cancelledIds = [];
		await using (NpgsqlCommand cancelQueued = new(
			"""
			UPDATE jobs SET state = 'cancelled', finished_at = now(), lease_expires_at = NULL, heartbeat_at = NULL, note = 'Cancelled: run aborted'
			WHERE run_id = $1 AND state IN ('queued', 'blocked')
			RETURNING id
			""", connection, transaction))
		{
			cancelQueued.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await cancelQueued.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				cancelledIds.Add(reader.GetGuid(0));
			}
		}

		List<Guid> inFlightIds = [];
		await using (NpgsqlCommand selectInFlight = new(
			"SELECT id FROM jobs WHERE run_id = $1 AND state IN ('running', 'attesting', 'converting')", connection, transaction))
		{
			selectInFlight.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await selectInFlight.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				inFlightIds.Add(reader.GetGuid(0));
			}
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

		// idx_jobs_credential_recent (credential_id, created_at DESC) serves the
		// credential_id equality lookup here; the ORDER BY deliberately does NOT match
		// its (created_at DESC) suffix -- see below -- so this is an index-assisted
		// filter followed by a small in-memory sort, not a pure index-order scan. That
		// is an acceptable trade at this scale: the query only ever runs once per
		// auth-failed completion (a rare event), against one credential's jobs, not the
		// hot claim/recovery paths idx_jobs_queue_claim/idx_jobs_lease_recovery serve.
		//
		// Ordering by created_at would be wrong: FanOutJobsAsync inserts every job for
		// one run in a single transaction, and Postgres's now() returns the same
		// transaction timestamp for every statement inside it -- so a run's fanned-out
		// jobs can share one identical created_at and have no defined relative order by
		// it. finished_at does not have that problem (each completion is its own
		// connection/transaction, i.e. a genuinely distinct now()), and it is also the
		// more correct definition of "3 consecutive failures" in the first place: an
		// operator means the three most recently *resolved* attempts against a
		// credential, not the three that happened to be queued first. A still-queued
		// job (finished_at IS NULL) falls back to created_at, which only matters for
		// ordering it behind every already-resolved job -- exactly where an
		// unattempted job belongs in a "most recent outcomes" list.
		bool tripped;
		await using (NpgsqlCommand recent = new(
			"SELECT state FROM jobs WHERE credential_id = $1 ORDER BY COALESCE(finished_at, created_at) DESC LIMIT $2", connection))
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

		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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

	[LoggerMessage(Level = LogLevel.Warning, Message = "Recovered job {JobId}: lease expired, now {NewState} (attempt {AttemptCount} of {MaxAttempts})")]
	private partial void LogRecoveredJob(Guid jobId, string newState, int attemptCount, int maxAttempts);

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
}
