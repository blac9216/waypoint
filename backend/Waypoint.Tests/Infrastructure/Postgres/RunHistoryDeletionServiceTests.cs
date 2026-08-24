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
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #592 (epic #588, last child): end-to-end coverage for
/// <see cref="RunHistoryDeletionService"/> against real Postgres -- terminal-only
/// enforcement, the compliance-purge gate (epic #588's "generic cleanup DEFERS to
/// domain purge" design), reference severing (schedules.last_run_id), retained
/// runs/jobs/job_events rows, tombstone recording, and idempotent re-invocation.
/// Mirrors <see cref="RunPurgeServiceTests"/>'s structure closely -- same fixture,
/// same seeding idiom -- since this is the sibling lifecycle operation.
/// </summary>
[Collection("Postgres")]
public sealed class RunHistoryDeletionServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private RunHistoryDeletionRepository _deletions = null!;
	private RunHistoryDeletionService _service = null!;

	public RunHistoryDeletionServiceTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_deletions = new RunHistoryDeletionRepository(_fixture.ConnectionString);
		_service = new RunHistoryDeletionService(_jobs, _deletions);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Theory]
	[InlineData("completed")]
	[InlineData("completed_with_failures")]
	[InlineData("aborted")]
	public async Task DeleteHistoryAsync_TerminalNonComplianceRun_Completes(string terminalState)
	{
		Guid runId = await SeedRunAsync("discover", terminalState);

		RunHistoryDeletionResult result = await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunHistoryDeletionOutcome.Completed, result.Outcome);
	}

	[Theory]
	[InlineData("pending")]
	[InlineData("running")]
	public async Task DeleteHistoryAsync_NonTerminalRun_RejectedAndLeftIntact(string nonTerminalState)
	{
		Guid runId = await SeedRunAsync("discover", nonTerminalState);

		RunHistoryDeletionResult result = await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunHistoryDeletionOutcome.RunNotTerminal, result.Outcome);
		Assert.Null(await GetHistoryDeletedAtAsync(runId));
	}

	[Fact]
	public async Task DeleteHistoryAsync_UnknownRun_ReturnsRunNotFound()
	{
		RunHistoryDeletionResult result = await _service.DeleteHistoryAsync(Guid.NewGuid(), "admin-tester", CancellationToken.None);

		Assert.Equal(RunHistoryDeletionOutcome.RunNotFound, result.Outcome);
	}

	[Theory]
	[InlineData("scan")]
	[InlineData("remediate")]
	public async Task DeleteHistoryAsync_ComplianceRunNotYetPurged_RejectedAndLeftIntact(string complianceRunType)
	{
		Guid runId = await SeedRunAsync(complianceRunType, "completed");

		RunHistoryDeletionResult result = await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunHistoryDeletionOutcome.RequiresDomainPurgeFirst, result.Outcome);
		Assert.Null(await GetHistoryDeletedAtAsync(runId));
		Assert.Null(await _deletions.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Theory]
	[InlineData("scan")]
	[InlineData("remediate")]
	public async Task DeleteHistoryAsync_PurgedComplianceRun_Completes(string complianceRunType)
	{
		Guid runId = await SeedRunAsync(complianceRunType, "completed");
		await MarkRunPurgedAsync(runId);

		RunHistoryDeletionResult result = await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunHistoryDeletionOutcome.Completed, result.Outcome);
	}

	[Fact]
	public async Task DeleteHistoryAsync_FullFlow_SeversScheduleReferenceRetainsRunAndRecordsTombstone()
	{
		Guid runId = await SeedRunAsync("discover", "completed");
		Guid jobId = await InsertJobAsync(runId, "discover");
		await InsertJobEventAsync(runId, jobId);
		Guid scheduleId = await InsertScheduleReferencingRunAsync(runId);

		RunHistoryDeletionResult result = await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunHistoryDeletionOutcome.Completed, result.Outcome);
		Assert.NotNull(result.Tombstone);
		Assert.Equal("admin-tester", result.Tombstone!.Actor);
		Assert.Equal("completed", result.Tombstone.PriorState);
		Assert.Equal("discover", result.Tombstone.RunType);
		Assert.Equal("completed", result.Tombstone.Outcome);

		// AC (b): the one cross-domain reference the issue names is severed.
		Assert.Null(await GetScheduleLastRunIdAsync(scheduleId));

		// AC (c): runs/jobs rows and the append-only job_events ledger are retained,
		// exactly like migration 0042's purge design -- only the marker is set.
		Assert.NotNull(await GetHistoryDeletedAtAsync(runId));
		Assert.Equal("completed", await GetRunStateAsync(runId));
		Assert.True(await JobRowExistsAsync(jobId));
		Assert.True(await JobEventExistsForRunAsync(runId));
	}

	[Fact]
	public async Task DeleteHistoryAsync_AlreadyDeleted_IsAFastCleanNoOpAndDoesNotDoubleWriteTombstone()
	{
		Guid runId = await SeedRunAsync("discover", "aborted");

		RunHistoryDeletionResult first = await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunHistoryDeletionOutcome.Completed, first.Outcome);

		RunHistoryDeletionResult second = await _service.DeleteHistoryAsync(runId, "someone-else", CancellationToken.None);
		Assert.Equal(RunHistoryDeletionOutcome.AlreadyDeleted, second.Outcome);
		// The tombstone's actor/prior-state reflect the ORIGINAL deletion, not the second caller.
		Assert.Equal("admin-tester", second.Tombstone!.Actor);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT COUNT(*) FROM run_history_deletion_tombstones WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task GetStatusAsync_NeverRequested_ReturnsNull()
	{
		Guid runId = await SeedRunAsync("discover", "completed");

		RunHistoryDeletionTombstone? result = await _service.GetStatusAsync(runId, CancellationToken.None);

		Assert.Null(result);
	}

	// -- trigger interaction: run_history_deletion_tombstones is append-only --------

	[Fact]
	public async Task RunHistoryDeletionTombstones_CannotBeUpdated()
	{
		Guid runId = await SeedRunAsync("discover", "completed");
		await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new("UPDATE run_history_deletion_tombstones SET actor = 'tampered' WHERE run_id = $1", connection);
		update.Parameters.AddWithValue(runId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunHistoryDeletionTombstones_CannotBeDeleted()
	{
		Guid runId = await SeedRunAsync("discover", "completed");
		await _service.DeleteHistoryAsync(runId, "admin-tester", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM run_history_deletion_tombstones WHERE run_id = $1", connection);
		delete.Parameters.AddWithValue(runId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
	}

	// -- seeding/reading helpers ---------------------------------------------------

	private async Task<Guid> SeedRunAsync(string runType, string state)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO runs (run_type, scope, state) VALUES ($1, '{}', $2) RETURNING id", c);
		q.Parameters.AddWithValue(runType);
		q.Parameters.AddWithValue(state);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertJobAsync(Guid runId, string jobType)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, $2, 1, 'done') RETURNING id", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(jobType);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task InsertJobEventAsync(Guid runId, Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"INSERT INTO job_events (run_id, job_id, event_type, payload) VALUES ($1, $2, 'job.state', '{}'::jsonb)", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(jobId);
		await q.ExecuteNonQueryAsync();
	}

	private async Task<Guid> InsertScheduleReferencingRunAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO schedules (name, job_type, cron_expression, last_run_id, created_by)
			VALUES ($1, 'discover', '0 0 * * *', $2, 'tester')
			RETURNING id
			""", c);
		q.Parameters.AddWithValue($"history-deletion-test-schedule-{Guid.NewGuid():N}");
		q.Parameters.AddWithValue(runId);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task MarkRunPurgedAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("UPDATE runs SET purged_at = now() WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		await q.ExecuteNonQueryAsync();
	}

	private async Task<string> GetRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT state FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		return (string)(await q.ExecuteScalarAsync())!;
	}

	private async Task<DateTimeOffset?> GetHistoryDeletedAtAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT history_deleted_at FROM runs WHERE id = $1", c);
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

	private async Task<Guid?> GetScheduleLastRunIdAsync(Guid scheduleId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT last_run_id FROM schedules WHERE id = $1", c);
		q.Parameters.AddWithValue(scheduleId);
		object? result = await q.ExecuteScalarAsync();
		return result is Guid guid ? guid : null;
	}

	private async Task<bool> JobRowExistsAsync(Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT EXISTS(SELECT 1 FROM jobs WHERE id = $1)", c);
		q.Parameters.AddWithValue(jobId);
		return (bool)(await q.ExecuteScalarAsync())!;
	}

	private async Task<bool> JobEventExistsForRunAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT EXISTS(SELECT 1 FROM job_events WHERE run_id = $1)", c);
		q.Parameters.AddWithValue(runId);
		return (bool)(await q.ExecuteScalarAsync())!;
	}
}
