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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #594 (epic #577): end-to-end coverage for <see cref="RunPurgeService"/>
/// against real Postgres -- terminal-only enforcement, full purge (database
/// projections gone, tombstone recorded with correct actor/time/prior-state/counts),
/// partial-failure retry (simulating the runner's own outcome report the way
/// <c>RunCompletionTests</c> drives <c>AdvanceStateAsync</c> directly rather than
/// running a real dispatcher loop), and idempotent re-invocation.
/// </summary>
[Collection("Postgres")]
public sealed class RunPurgeServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private RunPurgeRepository _purges = null!;
	private AttestationSnapshotRepository _attestationSnapshots = null!;
	private RunPurgeService _service = null!;

	public RunPurgeServiceTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_purges = new RunPurgeRepository(_fixture.ConnectionString);
		_attestationSnapshots = new AttestationSnapshotRepository(_fixture.ConnectionString);
		_service = new RunPurgeService(_jobs, _purges, _attestationSnapshots, _fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Theory]
	[InlineData("completed")]
	[InlineData("completed_with_failures")]
	[InlineData("aborted")]
	public async Task PurgeRunAsync_TerminalRun_ProceedsPastTheGate(string terminalState)
	{
		Guid runId = await SeedRunAsync(terminalState);

		RunPurgeResult result = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);

		Assert.NotEqual(RunPurgeOutcome.RunNotTerminal, result.Outcome);
		Assert.NotEqual(RunPurgeOutcome.RunNotFound, result.Outcome);
	}

	[Theory]
	[InlineData("pending")]
	[InlineData("running")]
	public async Task PurgeRunAsync_NonTerminalRun_RejectedAndLeftIntact(string nonTerminalState)
	{
		Guid runId = await SeedRunAsync(nonTerminalState);

		RunPurgeResult result = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.RunNotTerminal, result.Outcome);
		Assert.Equal(nonTerminalState, await GetRunStateAsync(runId));
		Assert.Null(await _purges.GetStatusAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task PurgeRunAsync_UnknownRun_ReturnsRunNotFound()
	{
		RunPurgeResult result = await _service.PurgeRunAsync(Guid.NewGuid(), "admin-tester", CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.RunNotFound, result.Outcome);
	}

	[Fact]
	public async Task PurgeRunAsync_FullFlow_DeletesProjectionsAndRecordsTombstone()
	{
		Guid runId = await SeedRunAsync("completed");
		Guid scanJobId = await InsertScanJobAsync(runId);
		await InsertAttestationSnapshotAsync(runId, scanJobId);
		await InsertRunSecretAsync(runId);
		Guid scheduleId = await InsertScheduleReferencingRunAsync(runId);

		// Phase 1: kicks off the database phase and enqueues the artifact-deletion job.
		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// The database-side effects must be visible immediately -- they do not wait
		// on the artifact job.
		Assert.Empty(await _attestationSnapshots.ListForRunAsync(runId, CancellationToken.None));
		Assert.False(await RunSecretExistsAsync(runId));
		Assert.Null(await GetScheduleLastRunIdAsync(scheduleId));

		// Simulate the compliance-runner's PurgeJobHandler reporting success (the same
		// "drive the completion primitive directly" idiom RunCompletionTests uses
		// instead of running a real dispatcher loop).
		await _purges.ReportArtifactOutcomeAsync(runId, succeeded: true, artifactsDeleted: 3, lastError: null, CancellationToken.None);

		// Phase 2: re-invoking purge now finalizes.
		RunPurgeResult completed = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Completed, completed.Outcome);
		Assert.NotNull(completed.Status);
		Assert.Equal("completed", completed.Status!.PriorState);
		Assert.Equal("admin-tester", completed.Status.RequestedBy);
		Assert.True(completed.Status.DbPhaseDone);
		Assert.Equal("done", completed.Status.ArtifactsPhase);
		Assert.NotNull(completed.Status.CompletedAt);

		// The run_purges row is gone (only the tombstone remains as the historical record).
		Assert.Null(await _purges.GetStatusAsync(runId, CancellationToken.None));

		RunPurgeTombstone? tombstone = await _purges.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(tombstone);
		Assert.Equal(runId, tombstone!.RunId);
		Assert.Equal("admin-tester", tombstone.Actor);
		Assert.Equal("completed", tombstone.PriorState);
		Assert.Equal("completed", tombstone.Outcome);
		Assert.Contains("3", tombstone.DetailJson);

		Assert.NotNull(await GetRunPurgedAtAsync(runId));

		// Unrelated domain state must be untouched -- the run/job rows themselves stay
		// (design decision: runs/jobs are retained, not deleted).
		Assert.Equal("completed", await GetRunStateAsync(runId));
		Assert.True(await ScanJobRowExistsAsync(scanJobId));
	}

	[Fact]
	public async Task PurgeRunAsync_PartialFailureThenRetry_EventuallyCompletesWithoutDoubleReporting()
	{
		Guid runId = await SeedRunAsync("completed");
		await InsertScanJobAsync(runId);

		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// Simulate PurgeJobHandler reporting a partial failure.
		await _purges.ReportArtifactOutcomeAsync(runId, succeeded: false, artifactsDeleted: 1, lastError: "permission denied", CancellationToken.None);

		RunPurgeResult? failed = await _service.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(failed);
		Assert.Equal(RunPurgeOutcome.Failed, failed!.Outcome);

		// Retry: PurgeRunAsync must not redo the (already-committed) database phase --
		// db_phase_done stays true across the retry -- and must re-attempt the
		// artifact phase (a fresh enqueue) rather than getting stuck on 'failed'.
		RunPurgeResult retried = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, retried.Outcome);
		Assert.True(retried.Status!.DbPhaseDone);
		Assert.Equal("running", retried.Status.ArtifactsPhase);

		// This time the runner reports success.
		await _purges.ReportArtifactOutcomeAsync(runId, succeeded: true, artifactsDeleted: 3, lastError: null, CancellationToken.None);
		RunPurgeResult completed = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Completed, completed.Outcome);

		// Exactly one tombstone -- no double-write from the retry cycle.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT COUNT(*) FROM run_purge_tombstones WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PurgeRunAsync_AlreadyPurged_IsAFastCleanNoOp()
	{
		Guid runId = await SeedRunAsync("aborted"); // no scan jobs -- exercises the "nothing to delete on disk" fast path

		RunPurgeResult first = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Completed, first.Outcome);

		RunPurgeResult second = await _service.PurgeRunAsync(runId, "someone-else", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.AlreadyPurged, second.Outcome);
		// The tombstone's actor/prior-state reflect the ORIGINAL purge, not the second caller.
		Assert.Equal("admin-tester", second.Status!.RequestedBy);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT COUNT(*) FROM run_purge_tombstones WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PurgeRunAsync_NoScanJobs_SkipsArtifactEnqueueAndCompletesDirectly()
	{
		// A discover/credential-test-only run never produced an artifact -- purge
		// must not enqueue an empty-inventory purge job, and must still complete.
		Guid runId = await SeedRunAsync("completed");
		await InsertNonScanJobAsync(runId, "credential-test");

		RunPurgeResult result = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.Completed, result.Outcome);
		Assert.Equal(0, result.Status!.ArtifactsDeleted);
	}

	[Fact]
	public async Task GetStatusAsync_NeverRequested_ReturnsNull()
	{
		Guid runId = await SeedRunAsync("completed");

		RunPurgeResult? result = await _service.GetStatusAsync(runId, CancellationToken.None);

		Assert.Null(result);
	}

	// -- seeding/reading helpers ---------------------------------------------------

	private async Task<Guid> SeedRunAsync(string state)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}', $1) RETURNING id", c);
		q.Parameters.AddWithValue(state);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertScanJobAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, 'scan', 1, 'done') RETURNING id", c);
		q.Parameters.AddWithValue(runId);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertNonScanJobAsync(Guid runId, string jobType)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, $2, 1, 'done') RETURNING id", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(jobType);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task InsertAttestationSnapshotAsync(Guid runId, Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO attestation_snapshots (run_id, job_id, target_id, profile, scope, applied, expired)
			VALUES ($1, $2, $3, 'vsphere-stig', 'target', true, false)
			""", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(jobId);
		q.Parameters.AddWithValue(Guid.NewGuid());
		await q.ExecuteNonQueryAsync();
	}

	private async Task InsertRunSecretAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
			VALUES ($1, 'leftover-user', '\x00'::bytea, '\x00'::bytea, 'k', 'AES-256-GCM', now() + interval '1 hour')
			""", c);
		q.Parameters.AddWithValue(runId);
		await q.ExecuteNonQueryAsync();
	}

	private async Task<Guid> InsertScheduleReferencingRunAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO schedules (name, job_type, cron_expression, last_run_id, created_by)
			VALUES ($1, 'scan', '0 0 * * *', $2, 'tester')
			RETURNING id
			""", c);
		q.Parameters.AddWithValue($"purge-test-schedule-{Guid.NewGuid():N}");
		q.Parameters.AddWithValue(runId);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<string> GetRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT state FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		return (string)(await q.ExecuteScalarAsync())!;
	}

	private async Task<DateTimeOffset?> GetRunPurgedAtAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT purged_at FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		object? result = await q.ExecuteScalarAsync();
		return result switch
		{
			null or DBNull => null,
			DateTimeOffset dto => dto,
			DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
			_ => null,
		};
	}

	private async Task<bool> RunSecretExistsAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT EXISTS(SELECT 1 FROM run_secrets WHERE run_id = $1)", c);
		q.Parameters.AddWithValue(runId);
		return (bool)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid?> GetScheduleLastRunIdAsync(Guid scheduleId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT last_run_id FROM schedules WHERE id = $1", c);
		q.Parameters.AddWithValue(scheduleId);
		object? result = await q.ExecuteScalarAsync();
		return result is Guid guid ? guid : null;
	}

	private async Task<bool> ScanJobRowExistsAsync(Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT EXISTS(SELECT 1 FROM jobs WHERE id = $1)", c);
		q.Parameters.AddWithValue(jobId);
		return (bool)(await q.ExecuteScalarAsync())!;
	}
}
