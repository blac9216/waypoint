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

/// <summary>
/// Plain-Npgsql implementation of both <see cref="IJobControlRepository"/> (API
/// enqueue/control/query) and <see cref="IJobRunnerRepository"/> (runner
/// claim/lease/state/recovery) -- see those interfaces for the contract each method
/// keeps. Issue #415 split the former combined <c>IJobQueueRepository</c> into the two
/// focused interfaces along the ADR-0013/0014 process boundary; this class still
/// implements both because nothing about the SQL, transaction, or locking behavior
/// changed -- only which interface type each caller depends on.
/// </summary>
public sealed partial class JobQueueRepository : IJobControlRepository, IJobRunnerRepository
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
	// and it is what idx_jobs_queue_claim is a partial index on (job_type leads the
	// index as of #435/0024 so the job_type = ANY($3) predicate below stays index-
	// supported rather than falling back to scanning every claimable row).
	//
	// It is NOT true that this query is byte-identical to that test's. PR #126 claimed
	// that and it was wrong; the claim landed in a comment in #134 before the type it
	// referenced existed, so nobody could check it. The two differ deliberately:
	// JobsQueueClaimTests scopes its claim to one run (WHERE run_id = $1 AND ...) so its
	// fixtures cannot be disturbed by rows another test class left in the shared
	// container, and its $1/$2 are run id and claimant. This query is global-within-
	// allowlist by design -- a runner claims the highest-priority job anywhere in the
	// queue among the job types it registered -- and its $1/$2/$3 are the worker id,
	// the lease interval, and the job-type allowlist.
	//
	// The part that IS shared is asserted, not asserted-about: see
	// JobQueueClaimSqlParityTests, which normalizes both strings and fails if either
	// side's predicate, ordering or lock clause drifts from the other. Do not edit the
	// clause above without re-reading that test.
	//
	// job_type = ANY($3) is issue #435's ADR-0014 addition: "the atomic claim includes
	// the runner's explicit job-type allowlist... filtering after a claim is
	// prohibited." It sits inside the same locking CTE as state = 'queued', not as a
	// filter on the CTE's result, so an unlike runner can never observe -- let alone
	// lock -- a row outside its allowlist. ClaimJobAsync below rejects a null/empty
	// allowlist before this statement ever runs (fail closed).
	//
	// Everything set in the UPDATE beyond `state` is new relative to that test. Stamping
	// the lease atomically with the claim is what makes the #107 stranded-job state
	// unreachable from this code path; jobs_running_requires_lease_check (0002, merged
	// in #134) is the backstop for every other path, and it now rejects this statement
	// outright if the lease stamp is ever dropped.
	internal const string ClaimSql = """
		WITH claimable AS (
			SELECT id FROM jobs
			WHERE state = 'queued' AND job_type = ANY($3)
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
		RETURNING id, run_id, job_type, target_id, target_name, credential_id, priority, payload::text, attempt_count, max_attempts, stage, scan_plan_item_id
		""";

	public async Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		if (allowedJobTypes is null || allowedJobTypes.Count == 0)
		{
			// ADR-0014/#435: fail closed. A null or empty allowlist must never fall
			// back to claiming every job type -- that is exactly the cross-domain
			// claim this predicate exists to prevent.
			throw new ArgumentException("A non-empty job-type allowlist is required to claim a job.", nameof(allowedJobTypes));
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(ClaimSql, connection);
		command.Parameters.AddWithValue(workerId);
		command.Parameters.AddWithValue(leaseDuration);
		command.Parameters.AddWithValue(allowedJobTypes.ToArray());

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
			MaxAttempts: reader.GetInt32(9),
			Stage: reader.IsDBNull(10) ? null : reader.GetString(10),
			ScanPlanItemId: reader.IsDBNull(11) ? null : reader.GetGuid(11));
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

		// Issue #406: a job landing on one of the shape-terminal states (see
		// JobTerminalStates) is the only moment a run can possibly finish -- so the
		// completion check runs in the SAME transaction as this write, not as a
		// separate statement afterward. That is what makes "two jobs finish at the
		// same instant" race-safe: RunCompletionSql's own SELECT ... FOR UPDATE on
		// the run row serializes the second committer behind the first, and the
		// second committer's remaining-non-terminal-jobs count already reflects the
		// first commit, so exactly one of them observes zero remaining and flips the
		// run -- never both, never neither. clearLease is the same signal
		// JobExecutionContext.AdvanceAsync already relies on to distinguish a
		// same-tier pipeline move (clearLease: false, e.g. running -> attesting) from
		// a shape-terminal write; only the latter can possibly be a run's last job.
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// LOCK ORDER (issue #406 round-1 review): run row BEFORE job row. AbortRunAsync
		// and SwapAndResumeBlockedCredentialAsync both lock the run row first, then jobs;
		// this method must follow the same global order or a worker finishing a run's
		// last job (job-then-run) can deadlock against a concurrent abort (run-then-job).
		// So when this write can possibly finish the run -- clearLease + a terminal
		// toState -- we pre-read the job's run_id and take the run's FOR UPDATE lock
		// BEFORE touching the job row. Non-terminal pipeline moves (running -> attesting
		// etc.) never lock the run at all, exactly as before, so the hot per-stage path
		// keeps its old cost and never serializes on the run row.
		bool mayComplete = clearLease && JobTerminalStates.Contains(toState);
		if (mayComplete)
		{
			await using NpgsqlCommand lockRunFirst = new(
				"""
				SELECT r.id FROM jobs j JOIN runs r ON r.id = j.run_id
				WHERE j.id = $1 AND j.claimed_by = $2 AND j.state = $3
				FOR UPDATE OF r
				""", connection, transaction);
			lockRunFirst.Parameters.AddWithValue(jobId);
			lockRunFirst.Parameters.AddWithValue(workerId);
			lockRunFirst.Parameters.AddWithValue(expectedFromState);
			// If the job is unmatched (wrong owner/state) or has no run, we take no run
			// lock and fall through to the UPDATE, which returns matched=false and rolls
			// back -- identical outcome to before, just without a run lock we don't need.
			await lockRunFirst.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		Guid? runId;
		// The reader/command below are fully disposed by the time the `await using`
		// block closes -- rolling back while a reader is still open throws
		// NpgsqlOperationInProgressException (the same trap SwapAndResumeBlockedCredentialAsync's
		// comment documents), so the no-match rollback happens strictly after this
		// scope, never from inside it.
		bool matched;
		await using (NpgsqlCommand command = new(
			"""
			UPDATE jobs SET
				state = $3,
				note = $4,
				finished_at = CASE WHEN $5 THEN now() ELSE finished_at END,
				lease_expires_at = CASE WHEN $5 THEN NULL ELSE lease_expires_at END,
				heartbeat_at = CASE WHEN $5 THEN NULL ELSE heartbeat_at END
			WHERE id = $1 AND claimed_by = $2 AND state = $6
			RETURNING id, run_id
			""", connection, transaction))
		{
			command.Parameters.AddWithValue(jobId);
			command.Parameters.AddWithValue(workerId);
			command.Parameters.AddWithValue(toState);
			command.Parameters.AddWithValue((object?)note ?? DBNull.Value);
			command.Parameters.AddWithValue(clearLease);
			command.Parameters.AddWithValue(expectedFromState);

			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			matched = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			runId = matched && !reader.IsDBNull(1) ? reader.GetGuid(1) : null;
		}

		if (!matched)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return false;
		}

		RunCompletionResult? completion = null;
		if (clearLease && runId is Guid completedRunId && JobTerminalStates.Contains(toState))
		{
			completion = await TryCompleteRunAsync(completedRunId, connection, transaction, cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (completion is not null)
		{
			LogRunCompleted(completion.RunId, completion.State);
			await EmitRunCompletedAsync(completion, cancellationToken).ConfigureAwait(false);
		}

		return true;
	}

	/// <summary>
	/// Issue #406: transitions <paramref name="runId"/> out of <c>running</c> once every
	/// one of its jobs has reached a <see cref="JobTerminalStates"/> state --
	/// <c>completed</c> if all of them are the two success terminals
	/// (<see cref="JobStates.Uploaded"/>/<see cref="JobStates.Done"/>),
	/// <c>completed_with_failures</c> if any is a failure terminal
	/// (<see cref="JobStates.Failed"/>/<see cref="JobStates.AuthFailed"/>/<see cref="JobStates.Cancelled"/>).
	/// A <see cref="JobStates.Blocked"/> job is deliberately NOT terminal (per the
	/// contract and the caller's <see cref="JobTerminalStates.Contains"/> gate before
	/// this is even invoked) -- a run with any blocked job never reaches here with zero
	/// "remaining" because the blocked row still counts as outstanding work below.
	///
	/// MUST be called inside the same transaction as the job write that might have made
	/// this run's last job terminal -- see the caller's comment for why that, plus the
	/// <c>SELECT ... FOR UPDATE</c> here, is what makes two jobs finishing at the same
	/// instant race-safe rather than a double-write or a lost update.
	///
	/// Never touches a run already <c>aborted</c> (the <c>WHERE r.state = 'running'</c>
	/// guard) or already <c>completed</c>/<c>completed_with_failures</c> (idempotent:
	/// a second job's terminal write landing after the run already finished -- e.g. two
	/// jobs racing where this call itself is what serializes them -- finds nothing left
	/// to flip on its own turn).
	/// </summary>
	private static async Task<RunCompletionResult?> TryCompleteRunAsync(
		Guid runId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand lockRun = new("SELECT state FROM runs WHERE id = $1 FOR UPDATE", connection, transaction);
		lockRun.Parameters.AddWithValue(runId);
		object? currentState = await lockRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		if (currentState is not string state || !string.Equals(state, "running", StringComparison.Ordinal))
		{
			// Not found, already aborted, or already completed by a racing commit that
			// got here first -- nothing for this call to do.
			return null;
		}

		// "Remaining" is every job NOT in one of the five terminal states -- this
		// deliberately includes 'blocked' (the contract's "not terminal" state) as well
		// as 'queued'/'running'/'attesting'/'converting', so a run with any outstanding
		// or blocked work is left alone. failureCount only ever counts terminal rows, so
		// it is independent of remainingCount's definition.
		int remainingCount;
		int failureCount;
		await using (NpgsqlCommand counts = new(
			"""
			SELECT
				COUNT(*) FILTER (WHERE state NOT IN ('uploaded', 'done', 'failed', 'auth-failed', 'cancelled')),
				COUNT(*) FILTER (WHERE state IN ('failed', 'auth-failed', 'cancelled'))
			FROM jobs WHERE run_id = $1
			""", connection, transaction))
		{
			counts.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			remainingCount = Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
			failureCount = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
		}

		if (remainingCount > 0)
		{
			return null;
		}

		string newState = failureCount > 0 ? "completed_with_failures" : "completed";
		await using (NpgsqlCommand complete = new(
			"UPDATE runs SET state = $2, completed_at = now() WHERE id = $1", connection, transaction))
		{
			complete.Parameters.AddWithValue(runId);
			complete.Parameters.AddWithValue(newState);
			await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await DeleteRunSecretIfPresentAsync(runId, connection, transaction, cancellationToken).ConfigureAwait(false);

		return new RunCompletionResult(runId, newState, failureCount);
	}

	/// <summary>
	/// Issue #434 AC "terminal completion deletes the secret", extended by issue #586 to
	/// the per-target/per-purpose shape: the run-scoped encrypted secret(s)
	/// (<c>run_secrets</c>, <see cref="Waypoint.Core.Secrets.IRunSecretStore"/>) are
	/// deleted in the SAME transaction that flips the run to a terminal state --
	/// <see cref="TryCompleteRunAsync"/> above (completed/completed_with_failures) and
	/// <see cref="AbortRunAsync"/> below (aborted) -- rather than as a follow-up call
	/// after commit. That is what makes this race-safe against a concurrent job claim:
	/// both paths already hold the run row <c>FOR UPDATE</c> before this runs, so no
	/// job can be mid-claim against a run whose secret this statement is about to
	/// remove without that claim's own transaction serializing behind this one.
	///
	/// Deliberately raw SQL here rather than a dependency on
	/// <see cref="Waypoint.Core.Secrets.IRunSecretStore"/>: that interface owns its own
	/// connection/transaction per call (mirroring <c>ICredentialSecretStore</c>), which
	/// cannot be composed into a transaction this method does not own. A no-op (zero
	/// rows deleted) is the common case -- most jobs use a stored credential and have no
	/// run secret at all -- so no audit row is written unless one was actually deleted.
	/// This DELETE is unconditionally <c>run_id</c>-scoped (never target/purpose-scoped),
	/// so it covers BOTH the pre-#586 legacy shape (at most one row) and the #586
	/// per-target/per-purpose shape (any number of rows) with the same statement -- issue
	/// #586's migration/cleanup design point. Issue #586 also requires the deletion to
	/// stay individually attributed (one audit row per row actually deleted, each
	/// carrying its own target/purpose), so the DELETE is a <c>RETURNING</c> and the
	/// audit write is one INSERT per returned row rather than a single row for the whole
	/// run -- mirroring <see cref="Waypoint.Infrastructure.Secrets.RunSecretStore.DeleteAsync"/>'s
	/// own per-row audit loop.
	/// </summary>
	private static async Task DeleteRunSecretIfPresentAsync(
		Guid runId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
	{
		List<(Guid? TargetId, string Purpose)> deletedKeys = [];
		await using (NpgsqlCommand delete = new(
			"DELETE FROM run_secrets WHERE run_id = $1 RETURNING target_id, purpose", connection, transaction))
		{
			delete.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				deletedKeys.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1)));
			}
		}

		foreach ((Guid? targetId, string purpose) in deletedKeys)
		{
			await using NpgsqlCommand audit = new(
				"""
				INSERT INTO audit_log (event_type, actor, credential_id, job_id, run_id, detail)
				VALUES ('secret.run_deleted', 'system:run-completion', NULL, NULL, $1, $2::jsonb)
				""", connection, transaction);
			audit.Parameters.AddWithValue(runId);
			audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { target_id = targetId, purpose }));
			await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Mirrors <see cref="EmitCredentialSwappedAsync"/>'s "emit after commit" discipline
	/// (see <see cref="IJobRunnerRepository"/>'s "emit last" doc comment). There is no
	/// <c>run.state</c> event type in the closed six-value <c>job_events_event_type_check</c>
	/// set (docs/api-contract.md "Event streams (SSE)") -- <c>run.progress</c> is already
	/// the run-scoped "aggregate run counts/percent" carrier the Live Run/Results screens
	/// bind their progress UI to (<c>Waypoint.Runner.Jobs.JobDispatcherHostedService.AbortRunAsync</c>
	/// emits the same type for the abort case), so a run reaching a contract terminal
	/// state rides that existing channel with <c>state</c>/<c>completed</c> fields a
	/// consumer can key off of, rather than inventing a seventh type this migration would
	/// also have to add.
	/// </summary>
	private async Task EmitRunCompletedAsync(RunCompletionResult completion, CancellationToken cancellationToken)
	{
		if (_events is null)
		{
			return;
		}

		string payload = System.Text.Json.JsonSerializer.Serialize(new
		{
			completed = true,
			state = completion.State,
			failed_job_count = completion.FailedJobCount,
		});
		await _events.EmitAsync(JobEventTypes.RunProgress, null, completion.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #293's stage-per-execution requeue -- see <see cref="IJobRunnerRepository.RequeueAtStageAsync"/>.
	/// Same shape as <see cref="AdvanceStateAsync"/>'s <c>clearLease: true</c> path
	/// (finished_at is deliberately left untouched: the job is not finished, only
	/// resting) except the target state is always <c>queued</c> and <paramref name="stage"/>
	/// is written durably so the next claim's <c>RETURNING stage</c> hands it back.
	/// </summary>
	public async Task<bool> RequeueAtStageAsync(
		Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedFromState);
		ArgumentException.ThrowIfNullOrWhiteSpace(stage);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			UPDATE jobs SET
				state = 'queued',
				stage = $3,
				note = $4,
				lease_expires_at = NULL,
				heartbeat_at = NULL,
				claimed_by = NULL,
				claimed_at = NULL
			WHERE id = $1 AND claimed_by = $2 AND state = $5
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(workerId);
		command.Parameters.AddWithValue(stage);
		command.Parameters.AddWithValue((object?)note ?? DBNull.Value);
		command.Parameters.AddWithValue(expectedFromState);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		bool requeued = result is not null;
		if (requeued)
		{
			LogRequeuedAtStage(jobId, stage);
		}

		return requeued;
	}

	// #282 (closed by #293): widened from the original 'running'-only predicate to
	// also cover 'attesting'/'converting' -- the Standard-shape scan pipeline's other
	// two actively-worked, lease-bearing states (0015's CHECK already treats the three
	// as one unit; idx_jobs_lease_recovery follows suit in migration 0016). A worker
	// that crashes mid-attest/convert now recovers exactly like a crashed 'running'
	// worker: the row's stage column is untouched by this UPDATE (it only ever writes
	// state/claim/lease/finished/note), so whatever stage marker a prior
	// StageComplete requeue left in place survives the recovery and the next claim's
	// RETURNING stage hands it straight back to the handler -- recovery requeues at
	// the marker, not from the beginning, closing #282's stranding.
	// LOCK ORDER (issue #406 round-1 review): this sweep can land a run's last job on
	// 'failed' and then TryCompleteRunAsync flips the run, so it locks both run and job
	// rows in one transaction and, like AdvanceStateAsync / RetryJobAsync / AbortRunAsync,
	// must take the run lock FIRST. That is why RecoverExpiredLeasesAsync runs
	// LockRecoverableRunsSql (below) BEFORE this RecoverSql: it locks -- FOR UPDATE
	// SKIP LOCKED, ORDER BY id for a deterministic order among concurrent sweeps -- the
	// distinct run rows of every lease-expired job. SKIP LOCKED means a run a concurrent
	// AbortRunAsync already holds is simply passed over this sweep (its jobs recover on
	// the next tick) rather than blocking on it, so the sweep never waits in the
	// run->job direction against an abort that waits job->... -- closing the inversion.
	// RecoverSql then does its original job-level FOR UPDATE SKIP LOCKED work, with the
	// relevant run rows already held.
	internal const string LockRecoverableRunsSql = """
		SELECT id FROM runs WHERE id IN (
			SELECT DISTINCT run_id FROM jobs
			WHERE run_id IS NOT NULL
			  AND state IN ('running', 'attesting', 'converting')
			  AND lease_expires_at < now()
		)
		ORDER BY id
		FOR UPDATE SKIP LOCKED
		""";

	// $2 is the set of run ids LockRecoverableRunsSql actually locked this sweep. A
	// lease-expired job is only recovered here if its run is in that set (or it has no
	// run at all) -- so a job whose run a concurrent AbortRunAsync holds (hence skipped
	// by LockRecoverableRunsSql) is NOT touched here, which is what stops recovery from
	// ever holding a run's job while blocking on that run's row: the run-first + scope
	// pairing removes the deadlock cycle against abort entirely.
	internal const string RecoverSql = """
		WITH recoverable AS (
			SELECT id FROM jobs
			WHERE state IN ('running', 'attesting', 'converting') AND lease_expires_at < now()
			  AND (run_id IS NULL OR run_id = ANY($2))
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

		if (!JobStateMachine.CanEngineTransition(JobShape.Simple, JobStates.Running, JobStates.Queued)
			|| !JobStateMachine.CanEngineTransition(JobShape.Standard, JobStates.Attesting, JobStates.Queued)
			|| !JobStateMachine.CanEngineTransition(JobShape.Standard, JobStates.Converting, JobStates.Queued))
		{
			throw new InvalidOperationException("The engine transition gate rejects lease recovery.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Issue #406: a lease-expiry sweep can itself land a run's last job on a
		// terminal state -- 'failed' once attempt_count exhausts max_attempts (the
		// 'cancelled' arm only fires under an already-aborted run, which
		// TryCompleteRunAsync's own `state = 'running'` guard already leaves alone) --
		// so this needs the same in-transaction completion check AdvanceStateAsync
		// uses, applied once per distinct run this batch actually recovered into a
		// JobTerminalStates state. A batch can recover jobs from several runs at once,
		// unlike AdvanceStateAsync's single-job call, hence the per-run loop below
		// rather than a single completion attempt.
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// LOCK ORDER (issue #406 round-1 review): take the run-row locks FIRST -- see the
		// LockRecoverableRunsSql / RecoverSql comments. A run a concurrent abort holds is
		// SKIP-LOCKED past here and excluded from RecoverSql's $2 scope, so this sweep
		// never blocks on a run while holding one of that run's job rows.
		List<Guid> lockedRunIds = [];
		await using (NpgsqlCommand lockRuns = new(LockRecoverableRunsSql, connection, transaction))
		{
			await using NpgsqlDataReader reader = await lockRuns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				lockedRunIds.Add(reader.GetGuid(0));
			}
		}

		List<RecoveredJob> recovered = [];
		await using (NpgsqlCommand command = new(RecoverSql, connection, transaction))
		{
			command.Parameters.AddWithValue(batchSize);
			command.Parameters.AddWithValue(lockedRunIds.ToArray());
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				RecoveredJob job = new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
				recovered.Add(job);
			}
		}

		List<RunCompletionResult> completions = [];
		foreach (Guid runId in recovered
			.Where(job => job.RunId is not null && JobTerminalStates.Contains(job.NewState))
			.Select(job => job.RunId!.Value)
			.Distinct())
		{
			RunCompletionResult? completion = await TryCompleteRunAsync(runId, connection, transaction, cancellationToken).ConfigureAwait(false);
			if (completion is not null)
			{
				completions.Add(completion);
			}
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		foreach (RecoveredJob job in recovered)
		{
			LogRecoveredJob(job.Id, job.NewState, job.AttemptCount, job.MaxAttempts);
		}

		foreach (RunCompletionResult completion in completions)
		{
			LogRunCompleted(completion.RunId, completion.State);
			await EmitRunCompletedAsync(completion, cancellationToken).ConfigureAwait(false);
		}

		return recovered;
	}

	public async Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runType);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			INSERT INTO runs (run_type, scope, credential_id, initiated_by, schedule_id, state)
			VALUES ($1, $2::jsonb, $3, $4, $5, 'pending')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(runType);
		command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson);
		command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)initiatedBy ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)scheduleId ?? DBNull.Value);

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
		List<Guid> blockedCredentialIds = new();
		foreach (JobSpec spec in specs)
		{
			await using NpgsqlCommand insertJob = new(
				"""
				INSERT INTO jobs (run_id, job_type, target_id, target_name, credential_id, priority, payload, created_by, state, has_run_secret, scan_plan_item_id)
				VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, 'queued', $9, $10)
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
			insertJob.Parameters.AddWithValue(spec.HasRunSecret);
			insertJob.Parameters.AddWithValue((object?)spec.ScanPlanItemId ?? DBNull.Value);

			Guid jobId;
			await using (NpgsqlDataReader reader = await insertJob.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
			{
				await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
				jobId = reader.GetGuid(0);
				jobIds.Add(jobId);
				if (string.Equals(reader.GetString(1), JobStates.Blocked, StringComparison.Ordinal))
				{
					blockedCount++;
					blockedNote ??= reader.IsDBNull(2) ? null : reader.GetString(2);
					// #174: the 0005 trigger coerces per-row keyed by that row's own
					// credential_id, so a single fan-out can in principle be born blocked by
					// more than one halted credential -- collect the distinct set actually
					// observed rather than assuming one. Identity only (id) -- the halt
					// carries no secret material to leak here.
					if (spec.CredentialId is Guid blockedCredentialId && !blockedCredentialIds.Contains(blockedCredentialId))
					{
						blockedCredentialIds.Add(blockedCredentialId);
					}
				}
			}

			// Issue #585: the job's immutable per-purpose credential snapshot (migration
			// 0044), inserted in the SAME transaction as the job row so a claim can never
			// observe a job whose snapshot has not landed yet. Identity only -- ids, never
			// secret material.
			foreach (JobCredentialBindingSpec bindingSpec in spec.CredentialBindings ?? [])
			{
				await using NpgsqlCommand insertBinding = new(
					"INSERT INTO job_credential_bindings (job_id, purpose, credential_id, is_run_secret) VALUES ($1, $2, $3, $4)",
					connection, transaction);
				insertBinding.Parameters.AddWithValue(jobId);
				insertBinding.Parameters.AddWithValue(bindingSpec.Purpose);
				// Issue #586: an ad hoc purpose (IsRunSecret) never names a credential row --
				// CredentialId is null and the schema's run_secrets_binding_shape_check
				// backstops that at the database layer too.
				insertBinding.Parameters.AddWithValue((object?)bindingSpec.CredentialId ?? DBNull.Value);
				insertBinding.Parameters.AddWithValue(bindingSpec.IsRunSecret);
				await insertBinding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
			await EmitBornBlockedAsync(runId, blockedCount, jobIds.Count, blockedNote, blockedCredentialIds, cancellationToken).ConfigureAwait(false);
		}

		LogFannedOutJobs(runId, jobIds.Count);
		return jobIds;
	}

	/// <summary>
	/// Issue #1122: see <see cref="IJobControlRepository.CompleteEmptyRunAsync"/>. The
	/// <c>SELECT ... FOR UPDATE</c> plus the <c>pending</c> guard is the same
	/// serialization <see cref="FanOutJobsAsync"/> uses, so a concurrent abort/pause of
	/// the same run either lands entirely before this (and this returns false) or
	/// entirely after it.
	/// </summary>
	public async Task<bool> CompleteEmptyRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand lockRun = new("SELECT state FROM runs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockRun.Parameters.AddWithValue(runId);
			object? currentState = await lockRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (currentState is not string state || !string.Equals(state, "pending", StringComparison.Ordinal))
			{
				return false;
			}
		}

		await using (NpgsqlCommand anyJob = new("SELECT 1 FROM jobs WHERE run_id = $1 LIMIT 1", connection, transaction))
		{
			anyJob.Parameters.AddWithValue(runId);
			if (await anyJob.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
			{
				throw new InvalidOperationException(string.Format(
					CultureInfo.InvariantCulture, "Run '{0}' has jobs; complete it through job terminal transitions, not as an empty run.", runId));
			}
		}

		await using (NpgsqlCommand complete = new(
			"UPDATE runs SET state = 'completed', started_at = COALESCE(started_at, now()), completed_at = now() WHERE id = $1",
			connection, transaction))
		{
			complete.Parameters.AddWithValue(runId);
			await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Issue #434's contract: the run secret dies in the same transaction as the
		// terminal transition, exactly as TryCompleteRunAsync/AbortRunAsync do.
		await DeleteRunSecretIfPresentAsync(runId, connection, transaction, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		LogRunCompleted(runId, "completed");
		await EmitRunCompletedAsync(new RunCompletionResult(runId, "completed", 0), cancellationToken).ConfigureAwait(false);
		return true;
	}

	/// <summary>
	/// Issue #1016: see <see cref="IJobRunnerRepository.FanOutAdditionalJobsAsync"/>.
	/// Requires <c>runs.state = 'running'</c> (the counterpart guard to
	/// <see cref="FanOutJobsAsync"/>'s <c>'pending'</c> guard) so a caller can never
	/// silently add jobs to a run that has not started, already finished, or was
	/// aborted out from under it.
	/// </summary>
	public async Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(
		Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(specs);
		if (specs.Count == 0)
		{
			throw new ArgumentException("Fanning out additional jobs requires at least one spec.", nameof(specs));
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand lockRun = new("SELECT state FROM runs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockRun.Parameters.AddWithValue(runId);
			object? state = await lockRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (state is not string runState || !string.Equals(runState, "running", StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					string.Format(CultureInfo.InvariantCulture, "Run '{0}' is not running; cannot fan out additional jobs to it.", runId));
			}
		}

		List<Guid> jobIds = new(specs.Count);
		foreach (JobSpec spec in specs)
		{
			await using NpgsqlCommand insertJob = new(
				"""
				INSERT INTO jobs (run_id, job_type, target_id, target_name, credential_id, priority, payload, created_by, state, has_run_secret, scan_plan_item_id)
				VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, 'queued', $9, $10)
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
			insertJob.Parameters.AddWithValue(spec.HasRunSecret);
			insertJob.Parameters.AddWithValue((object?)spec.ScanPlanItemId ?? DBNull.Value);

			object? jobId = await insertJob.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			jobIds.Add((Guid)jobId!);
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
		await using NpgsqlCommand command = new("SELECT state, paused, blocked, blocked_reason, initiated_by FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
			? new RunQueueState(
				reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
				reader.IsDBNull(3) ? null : reader.GetString(3),
				reader.IsDBNull(4) ? null : reader.GetString(4))
			: null;
	}

	/// <summary>
	/// Shared SELECT/FROM/JOIN for the <see cref="RunSummary"/> projection used by
	/// <see cref="GetRunAsync"/>, <see cref="ListRunsAsync"/>, and
	/// <see cref="ListRunHistoryAsync"/>. Keeping one copy of the 19-column ordinal
	/// list means a column added here cannot silently drift out of sync with
	/// <see cref="ReadRunSummary"/>.
	///
	/// IMPORTANT: a C# raw string literal excludes the newline immediately before its
	/// closing <c>"""</c> delimiter, so this constant's text ends flush at
	/// <c>...j.run_id = r.id</c> with no trailing whitespace. Every call site MUST
	/// start its own appended clause with an explicit <c>"\n"</c> (as every call site
	/// below does) -- concatenating a bare <c>"WHERE ..."</c>/<c>"GROUP BY ..."</c>
	/// string directly fuses the last identifier with the next keyword (e.g.
	/// <c>r.idWHERE</c>) and produces invalid SQL that only a real Postgres round trip
	/// catches, not a fake-repository test. See the Postgres integration tests for
	/// <see cref="GetRunAsync"/>, <see cref="ListRunsAsync"/>, and
	/// <see cref="ListRunHistoryAsync"/>, which pin this.
	///
	/// Issue #970: the five <c>job_count_*</c> FILTER predicates are built from
	/// <see cref="JobCountBuckets"/> (backed by the <see cref="JobStates"/> vocabulary)
	/// rather than hand-typed per bucket, so a terminal state such as
	/// <see cref="JobStates.Uploaded"/> cannot be typo'd out of every bucket again --
	/// every value in <see cref="JobStates.All"/> resolves to exactly one bucket, so
	/// the five counts always sum to <c>job_count</c>.
	/// </summary>
	private static readonly string RunSummaryProjectionSql = $"""
		SELECT
			r.id, r.run_type, r.state, r.paused, r.blocked, r.blocked_reason,
			r.scope::text,
			r.credential_id, r.initiated_by, r.schedule_id,
			r.created_at::text, r.started_at::text, r.completed_at::text,
			COUNT(j.id) FILTER (WHERE j.id IS NOT NULL),
			COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state IN ({JobCountStateList(JobCountBucket.Queued)})),
			COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state IN ({JobCountStateList(JobCountBucket.Running)})),
			COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state IN ({JobCountStateList(JobCountBucket.Completed)})),
			COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state IN ({JobCountStateList(JobCountBucket.Failed)})),
			COUNT(j.id) FILTER (WHERE j.id IS NOT NULL AND j.state IN ({JobCountStateList(JobCountBucket.Blocked)})),
			r.credential_name, r.credential_type, r.credential_username
		FROM runs r
		LEFT JOIN jobs j ON j.run_id = r.id
		""";

	/// <summary>Comma-separated, single-quoted SQL literal list of the <see cref="JobStates"/> values in <paramref name="bucket"/>.</summary>
	private static string JobCountStateList(JobCountBucket bucket) =>
		string.Join(", ", JobCountBuckets.StatesIn(bucket).Select(state => $"'{state}'"));

	/// <summary>Reads one row of <see cref="RunSummaryProjectionSql"/>'s column order into a <see cref="RunSummary"/>.</summary>
	private static RunSummary ReadRunSummary(NpgsqlDataReader reader) => new(
		Id: reader.GetGuid(0),
		RunType: reader.GetString(1),
		State: reader.GetString(2),
		Paused: reader.GetBoolean(3),
		Blocked: reader.GetBoolean(4),
		BlockedReason: reader.IsDBNull(5) ? null : reader.GetString(5),
		ScopeJson: reader.GetString(6),
		CredentialId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
		InitiatedBy: reader.IsDBNull(8) ? null : reader.GetString(8),
		ScheduleId: reader.IsDBNull(9) ? null : reader.GetGuid(9),
		CreatedAt: reader.GetString(10),
		StartedAt: reader.IsDBNull(11) ? null : reader.GetString(11),
		CompletedAt: reader.IsDBNull(12) ? null : reader.GetString(12),
		JobCount: reader.GetInt32(13),
		JobCountQueued: reader.GetInt32(14),
		JobCountRunning: reader.GetInt32(15),
		JobCountCompleted: reader.GetInt32(16),
		JobCountFailed: reader.GetInt32(17),
		JobCountBlocked: reader.GetInt32(18),
		CredentialName: reader.IsDBNull(19) ? null : reader.GetString(19),
		CredentialType: reader.IsDBNull(20) ? null : reader.GetString(20),
		CredentialUsername: reader.IsDBNull(21) ? null : reader.GetString(21));

	public async Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(RunSummaryProjectionSql + "\nWHERE r.id = $1\nGROUP BY r.id", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return ReadRunSummary(reader);
	}

	public async Task<RunListResult> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand countCommand = new("SELECT COUNT(*) FROM runs", connection);
		int totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

		await using NpgsqlCommand command = new(
			RunSummaryProjectionSql + "\n" + """
			GROUP BY r.id
			ORDER BY r.created_at DESC, r.id DESC
			LIMIT $1 OFFSET $2
			""", connection);
		command.Parameters.AddWithValue(limit);
		command.Parameters.AddWithValue(offset);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<RunSummary> runs = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			runs.Add(ReadRunSummary(reader));
		}

		return new RunListResult(runs, totalCount);
	}

	public async Task<RunHistoryPage> ListRunHistoryAsync(RunHistoryQuery query, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		List<string> clauses = [];
		List<object> parameters = [];

		if (query.States is { Count: > 0 })
		{
			clauses.Add($"r.state = ANY(${parameters.Count + 1})");
			parameters.Add(query.States.ToArray());
		}

		if (query.RunTypes is { Count: > 0 })
		{
			clauses.Add($"r.run_type = ANY(${parameters.Count + 1})");
			parameters.Add(query.RunTypes.ToArray());
		}

		if (query.Since is { } since)
		{
			clauses.Add($"r.created_at >= ${parameters.Count + 1}");
			parameters.Add(since);
		}

		if (query.Until is { } until)
		{
			clauses.Add($"r.created_at <= ${parameters.Count + 1}");
			parameters.Add(until);
		}

		if (query.AfterCreatedAt is { } afterCreatedAt && query.AfterId is { } afterId)
		{
			// Keyset predicate matching the ORDER BY created_at DESC, id DESC tie-break:
			// "strictly after" in that composite order means either an earlier created_at,
			// or the same created_at with a strictly smaller id (see RunHistoryCursor's
			// doc comment for why created_at alone is not unique enough to page on).
			int p1 = parameters.Count + 1;
			int p2 = parameters.Count + 2;
			clauses.Add($"(r.created_at < ${p1} OR (r.created_at = ${p1} AND r.id < ${p2}))");
			parameters.Add(afterCreatedAt);
			parameters.Add(afterId);
		}

		string whereSql = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;

		// Fetch Limit + 1 to detect "more rows exist" without a second COUNT query --
		// same idiom IJobEventHistoryReader.ReadHistoryAsync uses (issue #581/PR #684).
		int fetchLimit = query.Limit + 1;
		int limitParamIndex = parameters.Count + 1;
		parameters.Add(fetchLimit);

		string sql = RunSummaryProjectionSql + "\n" + whereSql + "\n" + $"""
			GROUP BY r.id
			ORDER BY r.created_at DESC, r.id DESC
			LIMIT ${limitParamIndex}
			""";

		await using NpgsqlCommand command = new(sql, connection);
		foreach (object parameter in parameters)
		{
			command.Parameters.AddWithValue(parameter);
		}

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<RunSummary> runs = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			runs.Add(ReadRunSummary(reader));
		}

		bool hasMore = runs.Count > query.Limit;
		if (hasMore)
		{
			runs.RemoveAt(runs.Count - 1);
		}

		return new RunHistoryPage(runs, hasMore);
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
				created_at::text, started_at::text, finished_at::text,
				upload_status, upload_detail,
				credential_name, credential_type, credential_username
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
				// target_id is a uuid column; this Npgsql version refuses a direct
				// GetString() read of a uuid ("Reading as 'System.String' is not
				// supported for fields having DataTypeName 'uuid'") -- read it typed
				// and format to match JobSummary.TargetId's string? wire shape
				// (issue #273: the first caller to populate a non-null target_id and
				// exercise this path against real Postgres).
				TargetId: reader.IsDBNull(3) ? null : reader.GetGuid(3).ToString(),
				TargetName: reader.IsDBNull(4) ? null : reader.GetString(4),
				State: reader.GetString(5),
				Stage: reader.IsDBNull(6) ? null : reader.GetString(6),
				Priority: reader.GetInt16(7),
				AttemptCount: reader.GetInt32(8),
				CreatedAt: reader.GetString(9),
				StartedAt: reader.IsDBNull(10) ? null : reader.GetString(10),
				FinishedAt: reader.IsDBNull(11) ? null : reader.GetString(11),
				UploadStatus: reader.IsDBNull(12) ? null : reader.GetString(12),
				UploadDetail: reader.IsDBNull(13) ? null : reader.GetString(13),
				CredentialName: reader.IsDBNull(14) ? null : reader.GetString(14),
				CredentialType: reader.IsDBNull(15) ? null : reader.GetString(15),
				CredentialUsername: reader.IsDBNull(16) ? null : reader.GetString(16)));
		}

		return jobs;
	}

	public async Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT
				id, run_id, job_type, target_id, target_name,
				state, stage, priority, attempt_count,
				created_at::text, started_at::text, finished_at::text,
				upload_status, upload_detail,
				credential_name, credential_type, credential_username
			FROM jobs
			WHERE id = $1
			""", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new JobSummary(
			Id: reader.GetGuid(0),
			RunId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
			JobType: reader.GetString(2),
			TargetId: reader.IsDBNull(3) ? null : reader.GetGuid(3).ToString(),
			TargetName: reader.IsDBNull(4) ? null : reader.GetString(4),
			State: reader.GetString(5),
			Stage: reader.IsDBNull(6) ? null : reader.GetString(6),
			Priority: reader.GetInt16(7),
			AttemptCount: reader.GetInt32(8),
			CreatedAt: reader.GetString(9),
			StartedAt: reader.IsDBNull(10) ? null : reader.GetString(10),
			FinishedAt: reader.IsDBNull(11) ? null : reader.GetString(11),
			UploadStatus: reader.IsDBNull(12) ? null : reader.GetString(12),
			UploadDetail: reader.IsDBNull(13) ? null : reader.GetString(13),
			CredentialName: reader.IsDBNull(14) ? null : reader.GetString(14),
			CredentialType: reader.IsDBNull(15) ? null : reader.GetString(15),
			CredentialUsername: reader.IsDBNull(16) ? null : reader.GetString(16));
	}

	public async Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(uploadStatus);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE jobs SET upload_status = $1, upload_detail = $2 WHERE id = $3", connection);
		command.Parameters.AddWithValue(uploadStatus);
		command.Parameters.AddWithValue((object?)detail ?? DBNull.Value);
		command.Parameters.AddWithValue(jobId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task RecordUploadAttemptAsync(
		Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(uploadStatus);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Issue #744: attempt_number is assigned under this same connection immediately
		// before the insert -- a benign race under concurrent retries (there is no
		// uniqueness constraint on (job_id, attempt_number)) produces at worst a gap or
		// a duplicate ordinal, never a lost attempt row; every insert always lands.
		await using NpgsqlCommand countCommand = new(
			"SELECT count(*) FROM upload_attempts WHERE job_id = $1", connection);
		countCommand.Parameters.AddWithValue(jobId);
		long priorAttempts = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

		await using NpgsqlCommand insertCommand = new(
			"""
			INSERT INTO upload_attempts (job_id, attempt_number, endpoint, collection, status, error_detail)
			VALUES ($1, $2, $3, $4, $5, $6)
			""", connection);
		insertCommand.Parameters.AddWithValue(jobId);
		insertCommand.Parameters.AddWithValue((int)priorAttempts + 1);
		insertCommand.Parameters.AddWithValue((object?)endpoint ?? DBNull.Value);
		insertCommand.Parameters.AddWithValue((object?)collection ?? DBNull.Value);
		insertCommand.Parameters.AddWithValue(uploadStatus);
		insertCommand.Parameters.AddWithValue((object?)detail ?? DBNull.Value);
		await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, job_id, attempt_number, endpoint, collection, status, error_detail, attempted_at
			FROM upload_attempts
			WHERE job_id = $1
			ORDER BY attempt_number
			""", connection);
		command.Parameters.AddWithValue(jobId);

		List<UploadAttemptRecord> attempts = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			attempts.Add(new UploadAttemptRecord(
				Id: reader.GetGuid(0),
				JobId: reader.GetGuid(1),
				AttemptNumber: reader.GetInt32(2),
				Endpoint: reader.IsDBNull(3) ? null : reader.GetString(3),
				Collection: reader.IsDBNull(4) ? null : reader.GetString(4),
				Status: reader.GetString(5),
				ErrorDetail: reader.IsDBNull(6) ? null : reader.GetString(6),
				AttemptedAt: reader.GetFieldValue<DateTimeOffset>(7)));
		}

		return attempts;
	}

	public async Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT job_id, purpose, credential_id, credential_name, credential_type, credential_username, is_run_secret
			FROM job_credential_bindings
			WHERE job_id = $1
			ORDER BY purpose
			""", connection);
		command.Parameters.AddWithValue(jobId);

		List<JobCredentialBinding> bindings = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			bindings.Add(new JobCredentialBinding(
				JobId: reader.GetGuid(0),
				Purpose: reader.GetString(1),
				CredentialId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
				CredentialName: reader.IsDBNull(3) ? null : reader.GetString(3),
				CredentialType: reader.IsDBNull(4) ? null : reader.GetString(4),
				CredentialUsername: reader.IsDBNull(5) ? null : reader.GetString(5),
				IsRunSecret: reader.GetBoolean(6)));
		}

		return bindings;
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

		// Issue #434: abort is also a terminal transition -- see DeleteRunSecretIfPresentAsync's
		// doc comment for why this runs inside the same transaction as markAborted above
		// (the run row's FOR UPDATE lock, taken earlier in this method, is what makes it
		// race-safe against a concurrent claim).
		await DeleteRunSecretIfPresentAsync(runId, connection, transaction, cancellationToken).ConfigureAwait(false);

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

	public async Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Lock the row so the cancel and the state check are one atomic decision -- a
		// concurrent claim cannot move the job from queued to running between our read
		// and our write.
		string? currentState;
		await using (NpgsqlCommand lockJob = new(
			"SELECT state FROM jobs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockJob.Parameters.AddWithValue(jobId);
			object? state = await lockJob.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			currentState = state as string;
		}

		if (currentState is null)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return JobCancelOutcome.NotFound;
		}

		bool cancellable = string.Equals(currentState, JobStates.Queued, StringComparison.Ordinal)
			|| string.Equals(currentState, JobStates.Blocked, StringComparison.Ordinal);
		if (!cancellable)
		{
			bool inFlight = string.Equals(currentState, JobStates.Running, StringComparison.Ordinal)
				|| string.Equals(currentState, JobStates.Attesting, StringComparison.Ordinal)
				|| string.Equals(currentState, JobStates.Converting, StringComparison.Ordinal);
			if (!inFlight)
			{
				// Terminal: nothing left to cancel. Leave the job as-is.
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return JobCancelOutcome.NotCancellable;
			}

			// Already running: no immediate state change (a concurrent worker owns the
			// row) -- set the per-job cooperative-cancel flag instead (issue #234). The
			// dispatcher's heartbeat loop observes it on its next tick, same as the
			// run-scoped abort check.
			await using (NpgsqlCommand requestCancel = new(
				"UPDATE jobs SET cancel_requested = true WHERE id = $1", connection, transaction))
			{
				requestCancel.Parameters.AddWithValue(jobId);
				await requestCancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			LogJobCancelRequested(jobId);
			return JobCancelOutcome.CancelRequested;
		}

		await using (NpgsqlCommand cancel = new(
			"""
			UPDATE jobs SET
				state = 'cancelled',
				claimed_by = NULL,
				claimed_at = NULL,
				lease_expires_at = NULL,
				heartbeat_at = NULL,
				finished_at = now(),
				note = 'Cancelled by request'
			WHERE id = $1
			""", connection, transaction))
		{
			cancel.Parameters.AddWithValue(jobId);
			await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogJobCancelled(jobId);
		return JobCancelOutcome.Cancelled;
	}

	public async Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new("SELECT cancel_requested FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is bool cancelRequested && cancelRequested;
	}

	/// <summary>Issue #297 -- see <see cref="IJobControlRepository.RetryJobAsync"/>.</summary>
	public async Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		if (!JobStateMachine.CanEngineTransition(JobShape.Simple, JobStates.Failed, JobStates.Queued))
		{
			throw new InvalidOperationException("The engine transition gate rejects a manual job retry.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// LOCK ORDER (issue #406 round-1 review): run row BEFORE job row. The reopen
		// UPDATE below writes runs, so this method locks both -- and must take them in
		// the same global order as AbortRunAsync (run-then-job) or the two deadlock when
		// an operator retries a job while an operator aborts its run. So read the job's
		// run_id, lock that run row FOR UPDATE, THEN lock the job row and do the
		// state-gated read below. A concurrent abort thus fully serializes with this
		// retry on the run row (the reopen's IN ('completed','completed_with_failures')
		// predicate already excludes 'aborted', so once abort wins the run is left
		// aborted). The two reads are one atomic decision inside the same transaction.
		string? currentState;
		Guid? runId;
		await using (NpgsqlCommand readRun = new(
			"SELECT run_id FROM jobs WHERE id = $1", connection, transaction))
		{
			readRun.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await readRun.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return JobRetryOutcome.NotFound;
			}

			runId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
		}

		if (runId is Guid runToLock)
		{
			await using NpgsqlCommand lockRun = new(
				"SELECT id FROM runs WHERE id = $1 FOR UPDATE", connection, transaction);
			lockRun.Parameters.AddWithValue(runToLock);
			await lockRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		// Lock the job row so the retry and the state check are one atomic decision, same
		// discipline as CancelJobAsync -- a concurrent lease-recovery sweep or another
		// retry cannot race this read/write pair. Run row (if any) is already locked above.
		await using (NpgsqlCommand lockJob = new(
			"SELECT state FROM jobs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockJob.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await lockJob.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				// Job vanished between the two reads (extraordinarily unlikely inside one
				// txn, but keep the same NotFound contract rather than NRE).
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return JobRetryOutcome.NotFound;
			}

			currentState = reader.GetString(0);
		}

		if (!string.Equals(currentState, JobStates.Failed, StringComparison.Ordinal))
		{
			// Deliberately excludes auth-failed (credential-swap-resume, #146/#295, is
			// the correct path there) and cancelled (a deliberate operator action --
			// silently re-queueing it would be wrong; start a new run instead).
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return JobRetryOutcome.NotFailed;
		}

		// stage and attempt_count are deliberately absent from this SET list: stage
		// survives untouched so the next claim's RETURNING stage resumes the pipeline
		// where it failed (ADR-0012 §5), and a manual operator override is not subject
		// to the automatic-retry attempt_count/max_attempts budget -- see the interface
		// doc comment.
		await using (NpgsqlCommand retry = new(
			"""
			UPDATE jobs SET
				state = 'queued',
				claimed_by = NULL,
				claimed_at = NULL,
				lease_expires_at = NULL,
				heartbeat_at = NULL,
				cancel_requested = false,
				note = 'Retried by operator'
			WHERE id = $1
			""", connection, transaction))
		{
			retry.Parameters.AddWithValue(jobId);
			await retry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Issue #406: retrying this job's only effect on jobs.state is 'failed' ->
		// 'queued', so if the run had already reached one of the two completed states
		// (its last-terminal-job moment predates this retry by definition, since a
		// 'failed' row is itself one of the five terminal states TryCompleteRunAsync
		// counted), the run must reopen to 'running' -- otherwise a completed run
		// keeps dispatching a freshly-queued job underneath a state the contract calls
		// terminal, and GET /runs/{id} lies about there being nothing left to do.
		// completed_at is cleared to match ('the run finished at this instant' is no
		// longer true) -- TryCompleteRunAsync re-stamps it if/when this retried job
		// (or whatever else is still outstanding) reaches a terminal state again.
		// Scoped to exactly the two completed states, same FOR UPDATE-locked read as
		// every other run-control primitive in this class, so a concurrent abort
		// racing this retry is serialized rather than silently overwritten back to
		// 'running' -- 'aborted' is excluded from the IN list below on purpose.
		if (runId is Guid retryRunId)
		{
			await using NpgsqlCommand reopenRun = new(
				"UPDATE runs SET state = 'running', completed_at = NULL WHERE id = $1 AND state IN ('completed', 'completed_with_failures')",
				connection, transaction);
			reopenRun.Parameters.AddWithValue(retryRunId);
			await reopenRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// "No audit, no retry" -- same discipline as SwapAndResumeBlockedCredentialAsync's
		// Step 5. Identity + job/run/stage context only.
		await using (NpgsqlCommand audit = new(
			"""
			INSERT INTO audit_log (event_type, actor, run_id, detail)
			VALUES ('job.retried', $1, $2, $3::jsonb)
			""", connection, transaction))
		{
			audit.Parameters.AddWithValue(actor);
			audit.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
			audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new
			{
				job_id = jobId,
				run_id = runId,
			}));
			await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogJobRetried(jobId, actor);
		return JobRetryOutcome.Retried;
	}

	/// <summary>Issue #757 -- see <see cref="IJobControlRepository.BulkCancelJobsAsync"/>.</summary>
	public async Task<BulkJobActionResult<JobCancelOutcome>> BulkCancelJobsAsync(
		Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(jobIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		List<BulkJobItemResult<JobCancelOutcome>> results = new(jobIds.Count);
		Dictionary<JobCancelOutcome, int> tally = [];
		foreach (Guid jobId in jobIds)
		{
			// Scope to this run BEFORE attempting the cancel -- a job id belonging to a
			// different run must never be touched under this run's authority, same rule
			// RetryJob (the singular HTTP action) already enforces via GetJobAsync.
			JobSummary? job = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
			JobCancelOutcome outcome = job is null || job.RunId != runId
				? JobCancelOutcome.NotFound
				: await CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);

			results.Add(new BulkJobItemResult<JobCancelOutcome>(jobId, outcome));
			tally[outcome] = tally.GetValueOrDefault(outcome) + 1;
		}

		await WriteBulkAuditAsync("job.bulk_cancelled", runId, actor, jobIds.Count, tally, cancellationToken).ConfigureAwait(false);
		LogBulkJobActionCompleted("job.bulk_cancelled", runId, jobIds.Count, actor);
		return new BulkJobActionResult<JobCancelOutcome>(results);
	}

	/// <summary>Issue #757 -- see <see cref="IJobControlRepository.BulkRetryJobsAsync"/>.</summary>
	public async Task<BulkJobActionResult<JobRetryOutcome>> BulkRetryJobsAsync(
		Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(jobIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		List<BulkJobItemResult<JobRetryOutcome>> results = new(jobIds.Count);
		Dictionary<JobRetryOutcome, int> tally = [];
		foreach (Guid jobId in jobIds)
		{
			JobSummary? job = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
			JobRetryOutcome outcome = job is null || job.RunId != runId
				? JobRetryOutcome.NotFound
				: await RetryJobAsync(jobId, actor, cancellationToken).ConfigureAwait(false);

			results.Add(new BulkJobItemResult<JobRetryOutcome>(jobId, outcome));
			tally[outcome] = tally.GetValueOrDefault(outcome) + 1;
		}

		// RetryJobAsync above already wrote one 'job.retried' row per successfully
		// retried job -- this summary row is the bulk-level record (actor + full
		// tally including conflicts), not a duplicate of those per-job rows.
		await WriteBulkAuditAsync("job.bulk_retried", runId, actor, jobIds.Count, tally, cancellationToken).ConfigureAwait(false);
		LogBulkJobActionCompleted("job.bulk_retried", runId, jobIds.Count, actor);
		return new BulkJobActionResult<JobRetryOutcome>(results);
	}

	/// <summary>
	/// Shared summary-audit writer for <see cref="BulkCancelJobsAsync"/>/
	/// <see cref="BulkRetryJobsAsync"/> -- one row per bulk call, detail carries the
	/// resolved id count and a <c>{ outcome: count }</c> tally so an auditor can see
	/// how many of the N resolved jobs landed in each outcome without re-deriving it
	/// from N per-item rows.
	/// </summary>
	private async Task WriteBulkAuditAsync<TOutcome>(
		string eventType, Guid runId, string actor, int resolvedCount, Dictionary<TOutcome, int> tally, CancellationToken cancellationToken)
		where TOutcome : struct, Enum
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand audit = new(
			"""
			INSERT INTO audit_log (event_type, actor, run_id, detail)
			VALUES ($1, $2, $3, $4::jsonb)
			""", connection);
		audit.Parameters.AddWithValue(eventType);
		audit.Parameters.AddWithValue(actor);
		audit.Parameters.AddWithValue(runId);
		audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new
		{
			run_id = runId,
			resolved_count = resolvedCount,
			outcomes = tally.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal),
		}));
		await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
		//
		// Issue #20: the halt also flips the credential's operator-visible health to
		// 'auth_failing' (0001's CHECK already allowed the value; nothing before this
		// wrote it). Health is a separate column from queue_halted on purpose -- a
		// future non-halt-driven health source (e.g. a failed /test call outside a
		// job) can set it without needing queue-halt's job/run side effects, and
		// clearing it is symmetric: only a successful /test call (not a bare
		// unblock) proves the credential works again. See UnblockCredentialAsync's
		// comment for why unblock alone does not clear health.
		await using (NpgsqlCommand haltCredential = new(
			"UPDATE credentials SET queue_halted = true, queue_halted_reason = $2, queue_halted_at = COALESCE(queue_halted_at, now()), health = 'auth_failing' WHERE id = $1", connection, transaction))
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
	/// <remarks>
	/// Issue #20: this deliberately does NOT clear <c>credentials.health</c> back to
	/// healthy. Unblocking re-queues work under the *same* credential -- it is an
	/// operator's "try again" or a swap that has not yet been proven to work, not
	/// evidence the credential is valid. Health only clears on a successful
	/// <c>/credentials/{id}/test</c> result (<see cref="MarkTestOutcomeAsync"/>), so
	/// the health field's meaning stays "last proven state", not "not currently
	/// halted".
	/// </remarks>
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

	/// <summary>
	/// True swap semantics for <c>POST /runs/{id}/resume-blocked</c> (docs/api-contract.md,
	/// ADR-0008) -- see <see cref="IJobControlRepository.SwapAndResumeBlockedCredentialAsync"/>
	/// for the contract. Built alongside <see cref="UnblockCredentialAsync"/>, not as a
	/// replacement for it: that method stays the "retry with the same credential"
	/// primitive; this one is "an Admin swapped in a different credential and wants the
	/// run's blocked work re-queued under it."
	///
	/// Locking follows the same idiom as the halt/unblock pair: the affected credential
	/// row(s) are locked <c>FOR UPDATE</c> before any write, so a concurrent halt trip or
	/// unblock against the same credential serializes against this swap rather than
	/// interleaving with it.
	/// </summary>
	public async Task<CredentialSwapResult> SwapAndResumeBlockedCredentialAsync(
		Guid runId, Guid replacementCredentialId, string actor, string? reason, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Step 1: does the run exist, and which credential(s) are its blocked jobs
		// halted on? Lock the run row itself (FOR UPDATE) so a concurrent abort/pause
		// cannot race the swap.
		await using (NpgsqlCommand lockRun = new("SELECT id FROM runs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockRun.Parameters.AddWithValue(runId);
			object? found = await lockRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (found is null)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return new CredentialSwapResult(CredentialSwapOutcome.RunNotFound, null, null, []);
			}
		}

		List<Guid> distinctHaltedCredentialIds = [];
		await using (NpgsqlCommand findHalted = new(
			"SELECT DISTINCT credential_id FROM jobs WHERE run_id = $1 AND state = 'blocked' AND credential_id IS NOT NULL",
			connection, transaction))
		{
			findHalted.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await findHalted.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				distinctHaltedCredentialIds.Add(reader.GetGuid(0));
			}
		}

		if (distinctHaltedCredentialIds.Count == 0)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return new CredentialSwapResult(CredentialSwapOutcome.RunNotHalted, null, null, []);
		}

		if (distinctHaltedCredentialIds.Count > 1)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return new CredentialSwapResult(CredentialSwapOutcome.AmbiguousHaltedCredential, null, null, []);
		}

		Guid oldCredentialId = distinctHaltedCredentialIds[0];

		// Lock the halted credential row -- same FOR UPDATE the halt/unblock pair uses --
		// so a concurrent halt trip or unblock against it serializes against this swap.
		await using (NpgsqlCommand lockOld = new("SELECT id FROM credentials WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockOld.Parameters.AddWithValue(oldCredentialId);
			await lockOld.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		// Step 2: validate the replacement credential -- exists, not itself halted, and
		// (where enforceable) the same credential_type as the one it is replacing. There
		// is no target/job type-linkage in the schema today (jobs.target_id has no FK to
		// targets, and no join exists anywhere from a job to a target's `kind`), so the
		// only type check this primitive can make is against the halted credential's own
		// type -- see IJobControlRepository's doc comment.
		string? replacementType = null;
		bool replacementHalted = false;
		bool replacementFound = false;
		await using (NpgsqlCommand lockNew = new(
			"SELECT credential_type, queue_halted FROM credentials WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockNew.Parameters.AddWithValue(replacementCredentialId);
			await using NpgsqlDataReader reader = await lockNew.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				replacementFound = true;
				replacementType = reader.GetString(0);
				replacementHalted = reader.GetBoolean(1);
			}
		}

		// The reader/command above are fully disposed by this point (the `await using`
		// blocks closed before falling through here) -- rolling back while a reader is
		// still open throws NpgsqlOperationInProgressException, so every early exit
		// below happens strictly after that scope, never from inside it.
		if (!replacementFound)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return new CredentialSwapResult(CredentialSwapOutcome.ReplacementCredentialNotFound, null, null, []);
		}

		if (replacementHalted)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return new CredentialSwapResult(CredentialSwapOutcome.ReplacementCredentialHalted, null, null, []);
		}

		string? oldType;
		await using (NpgsqlCommand readOldType = new("SELECT credential_type FROM credentials WHERE id = $1", connection, transaction))
		{
			readOldType.Parameters.AddWithValue(oldCredentialId);
			oldType = (string?)await readOldType.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		if (oldType is not null && !string.Equals(oldType, replacementType, StringComparison.Ordinal))
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return new CredentialSwapResult(CredentialSwapOutcome.ReplacementCredentialTypeMismatch, null, null, []);
		}

		// Step 3: swap jobs.credential_id old -> new for exactly this run's halted job
		// set (scoped by run_id AND the old credential_id together, never every job
		// system-wide that references the old credential -- a sibling run blocked by the
		// same credential is untouched here).
		List<Guid> resumedJobIds = [];
		await using (NpgsqlCommand swap = new(
			"""
			UPDATE jobs SET credential_id = $3, state = 'queued', note = $4
			WHERE run_id = $1 AND state = 'blocked' AND credential_id = $2
			RETURNING id
			""", connection, transaction))
		{
			swap.Parameters.AddWithValue(runId);
			swap.Parameters.AddWithValue(oldCredentialId);
			swap.Parameters.AddWithValue(replacementCredentialId);
			swap.Parameters.AddWithValue(reason ?? "Credential swapped and resumed");
			await using NpgsqlDataReader reader = await swap.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				resumedJobIds.Add(reader.GetGuid(0));
			}
		}

		// Issue #585: keep the swapped jobs' per-purpose snapshot (job_credential_bindings,
		// migration 0044) in lockstep with jobs.credential_id -- handlers prefer the
		// snapshot row for their execution purpose, so leaving it pointing at the halted
		// credential would make the resumed job decrypt the very credential the swap just
		// replaced. Scoped to exactly the resumed job set and the old credential, so a
		// purpose bound to a DIFFERENT credential (e.g. an unrelated vcsa-ssh binding) is
		// untouched. The controller has already enforced replacement type == halted type,
		// which keeps every purpose the old credential satisfied satisfiable by the new
		// one (ADR-0021 §2 compatibility is type-keyed).
		if (resumedJobIds.Count > 0)
		{
			await using NpgsqlCommand swapBindings = new(
				"UPDATE job_credential_bindings SET credential_id = $3 WHERE job_id = ANY($1) AND credential_id = $2",
				connection, transaction);
			swapBindings.Parameters.AddWithValue(resumedJobIds.ToArray());
			swapBindings.Parameters.AddWithValue(oldCredentialId);
			swapBindings.Parameters.AddWithValue(replacementCredentialId);
			await swapBindings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Step 4: unblock the run itself now that its halted jobs have moved off the
		// halted credential.
		await using (NpgsqlCommand clearRunBlock = new(
			"UPDATE runs SET blocked = false, blocked_reason = NULL WHERE id = $1", connection, transaction))
		{
			clearRunBlock.Parameters.AddWithValue(runId);
			await clearRunBlock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// Step 5: audit entry carrying BOTH credential identities -- "no audit, no
		// secret" discipline, same table CredentialRepository.DeleteAsync writes to.
		// Identity only, never secret material.
		await using (NpgsqlCommand audit = new(
			"""
			INSERT INTO audit_log (event_type, actor, credential_id, run_id, detail)
			VALUES ('credential.swapped', $1, $2, $3, $4::jsonb)
			""", connection, transaction))
		{
			audit.Parameters.AddWithValue(actor);
			audit.Parameters.AddWithValue(replacementCredentialId);
			audit.Parameters.AddWithValue(runId);
			audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new
			{
				run_id = runId,
				old_credential_id = oldCredentialId,
				new_credential_id = replacementCredentialId,
				resumed_job_count = resumedJobIds.Count,
				reason,
			}));
			await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogCredentialSwappedAndResumed(runId, oldCredentialId, replacementCredentialId, resumedJobIds.Count);
		await EmitCredentialSwappedAsync(runId, oldCredentialId, replacementCredentialId, resumedJobIds.Count, cancellationToken).ConfigureAwait(false);
		return new CredentialSwapResult(CredentialSwapOutcome.Swapped, oldCredentialId, replacementCredentialId, resumedJobIds);
	}

	/// <summary>
	/// Mirrors the halt trip's own <c>queue.state</c> emission (<see cref="EmitBornBlockedAsync"/>
	/// / <c>JobDispatcherHostedService.HandleAuthFailureAsync</c>) -- same event type and
	/// run/system-notice fan-out, since the frontend's <c>applyEvent</c> reducer already
	/// treats any <c>queue.state</c> for this run as "the halt banner state changed."
	/// <c>blocked: false</c> plus both credential ids distinguishes a swap-resume from a
	/// same-credential unblock in the payload, for any consumer that cares which
	/// happened. Emitted after commit ("nothing follows an emit in its transaction"),
	/// best-effort like every event.
	/// </summary>
	private async Task EmitCredentialSwappedAsync(
		Guid runId, Guid oldCredentialId, Guid newCredentialId, int resumedJobCount, CancellationToken cancellationToken)
	{
		if (_events is null)
		{
			return;
		}

		string payload = System.Text.Json.JsonSerializer.Serialize(new
		{
			blocked = false,
			swapped = true,
			old_credential_id = oldCredentialId,
			new_credential_id = newCredentialId,
			resumed_job_count = resumedJobCount,
		});
		await _events.EmitAsync(JobEventTypes.QueueState, null, runId, payload, cancellationToken).ConfigureAwait(false);
		await _events.EmitAsync(JobEventTypes.SystemNotice, null, null, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>#147: a run fanned out into an already-halted credential is otherwise
	/// invisible on SSE -- no auth failure occurs, so the dispatcher's halt path never
	/// runs. Emitted after the transaction committed ("nothing follows an emit in its
	/// transaction"), best-effort like every event.
	///
	/// #174: <paramref name="blockedCredentialIds"/> identifies which credential(s)'
	/// halt caused the block, mirroring the dispatcher's own halt-trip payload
	/// (<c>JobDispatcherHostedService.HandleAuthFailureAsync</c> emits
	/// <c>credential_id</c>) so an operator watching the stream knows which credential
	/// needs attention instead of only "a run was born blocked". Identity only (the
	/// credential id) -- never the secret material behind it.</summary>
	private async Task EmitBornBlockedAsync(
		Guid runId, int blockedCount, int jobCount, string? reason, IReadOnlyList<Guid> blockedCredentialIds, CancellationToken cancellationToken)
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
			reason,
			credential_ids = blockedCredentialIds
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

	[LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} completed: {State}")]
	private partial void LogRunCompleted(Guid runId, string state);

	[LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} cancelled by request")]
	private partial void LogJobCancelled(Guid jobId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} cancel requested while running; awaiting next heartbeat tick")]
	private partial void LogJobCancelRequested(Guid jobId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} retried by {Actor}")]
	private partial void LogJobRetried(Guid jobId, string actor);

	[LoggerMessage(Level = LogLevel.Information, Message = "Bulk action {EventType} on run {RunId}: {ResolvedCount} job(s) resolved, requested by {Actor}")]
	private partial void LogBulkJobActionCompleted(string eventType, Guid runId, int resolvedCount, string actor);

	[LoggerMessage(Level = LogLevel.Error, Message = "Credential {CredentialId} hit {Threshold} consecutive auth failures: {BlockedRunCount} run(s) and {BlockedJobCount} queued job(s) blocked")]
	private partial void LogAuthFailureHalt(Guid credentialId, int threshold, int blockedRunCount, int blockedJobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Credential {CredentialId} queue unblocked: {UnblockedRunCount} run(s) and {UnblockedJobCount} blocked job(s) released to queued")]
	private partial void LogCredentialUnblocked(Guid credentialId, int unblockedRunCount, int unblockedJobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} resumed by credential swap: {OldCredentialId} -> {NewCredentialId}, {ResumedJobCount} job(s) requeued")]
	private partial void LogCredentialSwappedAndResumed(Guid runId, Guid oldCredentialId, Guid newCredentialId, int resumedJobCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Recovered job {JobId} to {NewState} after expired lease (attempt {AttemptCount}/{MaxAttempts})")]
	private partial void LogRecoveredJob(Guid jobId, string newState, int attemptCount, int maxAttempts);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Claimed job {JobId} for worker {WorkerId} with a {LeaseDuration} lease")]
	private partial void LogJobClaimed(Guid jobId, string workerId, TimeSpan leaseDuration);

	[LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} requeued at stage '{Stage}'")]
	private partial void LogRequeuedAtStage(Guid jobId, string stage);
}
