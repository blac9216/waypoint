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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #406: docs/api-contract.md's run state machine names two terminal states
/// (<c>completed</c>, <c>completed_with_failures</c>) that, before this, no code path
/// ever wrote -- <c>AdvanceStateAsync</c> only ever set <c>jobs.state</c>, leaving
/// <c>runs.state</c> stuck at <c>running</c> forever. These tests exercise the
/// completion write directly against real Postgres: both terminal mappings, the
/// concurrent-last-two-jobs race, abort precedence, blocked-stays-running, and the
/// retry-reopens decision recorded in the PR body.
/// </summary>
[Collection("Postgres")]
public sealed class RunCompletionTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private RecordingEvents _events = null!;

	public RunCompletionTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_events = new RecordingEvents();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance, _events);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task AllJobsSucceed_RunTransitionsToCompleted_WithCompletedAtStamped()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");
		Guid jobB = await InsertJobAsync(runId, JobStates.Done, "worker-b"); // already terminal

		bool advanced = await _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Done, null, clearLease: true, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal("completed", await GetRunStateAsync(runId));
		Assert.NotNull(await GetRunCompletedAtAsync(runId));
		_ = jobB;
	}

	[Fact]
	public async Task AnyJobFails_RunTransitionsToCompletedWithFailures()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Done, "worker-a"); // already terminal, succeeded
		Guid jobB = await InsertJobAsync(runId, JobStates.Running, "worker-b");

		bool advanced = await _repository.AdvanceStateAsync(jobB, "worker-b", JobStates.Running, JobStates.Failed, "boom", clearLease: true, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal("completed_with_failures", await GetRunStateAsync(runId));
		_ = jobA;
	}

	[Theory]
	[InlineData(JobStates.AuthFailed)]
	[InlineData(JobStates.Cancelled)]
	public async Task OtherFailureTerminals_AlsoMapToCompletedWithFailures(string terminalState)
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Done, "worker-a");
		Guid jobB = await InsertJobAsync(runId, JobStates.Running, "worker-b");

		await _repository.AdvanceStateAsync(jobB, "worker-b", JobStates.Running, terminalState, "x", clearLease: true, CancellationToken.None);

		Assert.Equal("completed_with_failures", await GetRunStateAsync(runId));
		_ = jobA;
	}

	[Fact]
	public async Task BlockedJob_KeepsRunRunning_EvenWhenEveryOtherJobIsTerminal()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");
		await InsertJobAsync(runId, JobStates.Blocked, null); // not terminal per contract

		bool advanced = await _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Done, null, clearLease: true, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal("running", await GetRunStateAsync(runId));
	}

	[Fact]
	public async Task NonTerminalClearLeaseFalseTransition_NeverAttemptsCompletion()
	{
		// running -> attesting is a same-tier move (clearLease: false); even though it
		// is this run's only job, it must never be mistaken for the run's last word.
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");

		bool advanced = await _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Attesting, null, clearLease: false, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal("running", await GetRunStateAsync(runId));
		Assert.DoesNotContain(_events.Events, e => e.RunId == runId);
	}

	[Fact]
	public async Task AbortedRun_NeverOverwrittenToCompleted()
	{
		Guid runId = await SeedRunAsync("aborted");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");

		// The row itself is still 'running' at the job level (an aborted run's
		// in-flight job is cancelled via a separate path in production, but this test
		// isolates TryCompleteRunAsync's own guard: even if a terminal job write does
		// land here, the run's already-'aborted' state must never flip to completed).
		bool advanced = await _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Done, null, clearLease: true, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal("aborted", await GetRunStateAsync(runId));
	}

	[Fact]
	public async Task RunProgressEvent_EmittedOnCompletion_CarryingStateAndFailureCount()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");

		await _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Failed, "boom", clearLease: true, CancellationToken.None);

		RecordedEvent evt = Assert.Single(_events.Events, e => e.RunId == runId && e.EventType == JobEventTypes.RunProgress);
		Assert.Contains("completed_with_failures", evt.PayloadJson, StringComparison.Ordinal);
		Assert.Null(evt.JobId);
	}

	[Fact]
	public async Task ConcurrentLastTwoJobs_ExactlyOneCompletionWrite_NeverBothNeither()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");
		Guid jobB = await InsertJobAsync(runId, JobStates.Running, "worker-b");

		Task<bool> first = _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Done, null, clearLease: true, CancellationToken.None);
		Task<bool> second = _repository.AdvanceStateAsync(jobB, "worker-b", JobStates.Running, JobStates.Done, null, clearLease: true, CancellationToken.None);
		bool[] results = await Task.WhenAll(first, second);

		Assert.All(results, Assert.True);
		Assert.Equal("completed", await GetRunStateAsync(runId));

		// Exactly one run.progress "completed" event -- not zero, not two. The second
		// committer's TryCompleteRunAsync call must find the run already flipped by
		// the first (state = 'running' guard) and no-op rather than double-emit.
		int completionEvents = _events.Events.Count(e => e.RunId == runId && e.EventType == JobEventTypes.RunProgress
			&& e.PayloadJson.Contains("\"completed\":true", StringComparison.Ordinal));
		Assert.Equal(1, completionEvents);
	}

	[Fact]
	public async Task RetryOfFailedJobOnCompletedRun_ReopensRunToRunning()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Running, "worker-a");
		await _repository.AdvanceStateAsync(jobA, "worker-a", JobStates.Running, JobStates.Failed, "boom", clearLease: true, CancellationToken.None);
		Assert.Equal("completed_with_failures", await GetRunStateAsync(runId));
		Assert.NotNull(await GetRunCompletedAtAsync(runId));

		JobRetryOutcome outcome = await _repository.RetryJobAsync(jobA, "operator@example.internal", CancellationToken.None);

		Assert.Equal(JobRetryOutcome.Retried, outcome);
		Assert.Equal("running", await GetRunStateAsync(runId));
		Assert.Null(await GetRunCompletedAtAsync(runId));
		Assert.Equal(JobStates.Queued, await GetJobStateAsync(jobA));
	}

	[Fact]
	public async Task RetryOfFailedJobOnAbortedRun_DoesNotReopenAbortedRun()
	{
		// Belt-and-suspenders on the abort-precedence rule: the reopen UPDATE's WHERE
		// clause only matches ('completed', 'completed_with_failures'), so an aborted
		// run is structurally excluded even if a failed job under it is retried.
		Guid runId = await SeedRunAsync("aborted");
		Guid jobA = await InsertJobAsync(runId, JobStates.Failed, null);

		await _repository.RetryJobAsync(jobA, "operator@example.internal", CancellationToken.None);

		Assert.Equal("aborted", await GetRunStateAsync(runId));
	}

	[Fact]
	public async Task LeaseRecoverySweep_ExhaustedAttempts_AlsoCompletesTheRun()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertJobAsync(runId, JobStates.Done, "worker-a");
		Guid jobB = await InsertExpiredLeaseJobAsync(runId, attemptCount: 3, maxAttempts: 3);

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(10, CancellationToken.None);

		Assert.Equal(JobStates.Failed, Assert.Single(recovered, j => j.Id == jobB).NewState);
		Assert.Equal("completed_with_failures", await GetRunStateAsync(runId));
		_ = jobA;
	}

	[Fact]
	public async Task LeaseRecoverySweep_RequeueUnderBudget_LeavesRunRunning()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobA = await InsertExpiredLeaseJobAsync(runId, attemptCount: 1, maxAttempts: 3);

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(10, CancellationToken.None);

		Assert.Equal(JobStates.Queued, Assert.Single(recovered, j => j.Id == jobA).NewState);
		Assert.Equal("running", await GetRunStateAsync(runId));
	}

	private async Task<Guid> SeedRunAsync(string state)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}', $1) RETURNING id", c);
		q.Parameters.AddWithValue(state);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertJobAsync(Guid runId, string state, string? worker)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, claimed_by, claimed_at, lease_expires_at, heartbeat_at, attempt_count, max_attempts)
			VALUES ($1, 'scan', 1, $2, $3::text,
				CASE WHEN $3::text IS NULL THEN NULL ELSE now() END,
				CASE WHEN $2 IN ('running', 'attesting', 'converting') THEN now() + interval '1 minute' ELSE NULL END,
				CASE WHEN $2 IN ('running', 'attesting', 'converting') THEN now() ELSE NULL END,
				1, 3)
			RETURNING id
			""", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(state);
		q.Parameters.AddWithValue((object?)worker ?? DBNull.Value);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertExpiredLeaseJobAsync(Guid runId, int attemptCount, int maxAttempts)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, claimed_by, claimed_at, lease_expires_at, heartbeat_at, attempt_count, max_attempts)
			VALUES ($1, 'scan', 1, 'running', 'dead-worker', now() - interval '2 minutes', now() - interval '1 minute', now() - interval '90 seconds', $2, $3)
			RETURNING id
			""", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(attemptCount);
		q.Parameters.AddWithValue(maxAttempts);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<string> GetRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT state FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		return (string)(await q.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobStateAsync(Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT state FROM jobs WHERE id = $1", c);
		q.Parameters.AddWithValue(jobId);
		return (string)(await q.ExecuteScalarAsync())!;
	}

	private async Task<DateTime?> GetRunCompletedAtAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT completed_at FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		object? result = await q.ExecuteScalarAsync();
		return result is DBNull or null ? null : (DateTime)result;
	}

	private sealed record RecordedEvent(string EventType, Guid? JobId, Guid? RunId, string PayloadJson);

	private sealed class RecordingEvents : IJobEventPublisher
	{
		public ConcurrentBag<RecordedEvent> Events { get; } = [];

		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{
			Events.Add(new RecordedEvent(eventType, jobId, runId, payloadJson));
			return Task.CompletedTask;
		}
	}
}
