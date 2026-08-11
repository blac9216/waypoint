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
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

[Collection("Postgres")]
public sealed class AbortedRunNeverResumesTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	public AbortedRunNeverResumesTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
	}
	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task DirectInsert_CannotQueueAJobUnderAnAbortedRun()
	{
		Guid runId = await SeedRunAsync("aborted");
		Guid jobId = await InsertJobAsync(runId, "queued", null);
		Assert.Equal(JobStates.Cancelled, await GetStateAsync(jobId));
	}

	[Fact]
	public async Task AbortingRun_CancelsAlreadyQueuedJobsWithoutAnotherJobWrite()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobId = await InsertJobAsync(runId, "queued", null);
		await ExecuteAsync("UPDATE runs SET state = 'aborted' WHERE id = $1", runId);
		Assert.Equal(JobStates.Cancelled, await GetStateAsync(jobId));
	}

	[Fact]
	public async Task DispatcherDiesMidJob_RunAborted_JobNeverExecutesAgain()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobId = await InsertJobAsync(runId, "running", "dead-worker");
		await ExecuteAsync("UPDATE runs SET state = 'aborted' WHERE id = $1", runId);

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(10, CancellationToken.None);
		Assert.Equal(JobStates.Cancelled, Assert.Single(recovered).NewState);

		int calls = 0;
		FakeJobHandler handler = new("download", (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(JobExecutionOutcome.Succeeded()); });
		JobDispatcherHostedService dispatcher = new(_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([handler]), Options.Create(new JobEngineOptions { PollInterval = TimeSpan.FromMilliseconds(25) }),
			NullLogger<JobDispatcherHostedService>.Instance);
		await dispatcher.StartAsync(CancellationToken.None);
		await Task.Delay(TimeSpan.FromMilliseconds(500));
		await dispatcher.StopAsync(CancellationToken.None);
		Assert.Equal(0, calls);
		Assert.Equal(JobStates.Cancelled, await GetStateAsync(jobId));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ConcurrentAbortAndQueuedInsert_AlwaysEndsCancelled(bool abortStartsFirst)
	{
		Guid runId = await SeedRunAsync("running");
		await using NpgsqlConnection first = new(_fixture.ConnectionString);
		await using NpgsqlConnection second = new(_fixture.ConnectionString);
		await first.OpenAsync(); await second.OpenAsync();
		await using NpgsqlTransaction firstTransaction = await first.BeginTransactionAsync();
		await using NpgsqlTransaction secondTransaction = await second.BeginTransactionAsync();
		NpgsqlConnection abortConnection = abortStartsFirst ? first : second;
		NpgsqlConnection insertConnection = abortStartsFirst ? second : first;
		NpgsqlTransaction abortTransaction = abortStartsFirst ? firstTransaction : secondTransaction;
		NpgsqlTransaction insertTransaction = abortStartsFirst ? secondTransaction : firstTransaction;

		await using NpgsqlCommand abort = new("UPDATE runs SET state = 'aborted' WHERE id = $1", abortConnection, abortTransaction);
		abort.Parameters.AddWithValue(runId);
		await using NpgsqlCommand insert = new("INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, 'download', 1, 'queued') RETURNING id", insertConnection, insertTransaction);
		insert.Parameters.AddWithValue(runId);

		Task<object?> firstWrite = abortStartsFirst ? abort.ExecuteScalarAsync() : insert.ExecuteScalarAsync();
		await firstWrite;
		Task<object?> secondWrite = abortStartsFirst ? insert.ExecuteScalarAsync() : abort.ExecuteScalarAsync();
		await Task.Delay(50, CancellationToken.None);
		Assert.False(secondWrite.IsCompleted, "The second writer must serialize on the run row until the first transaction commits.");
		if (abortStartsFirst)
		{
			await abortTransaction.CommitAsync();
		}
		else
		{
			await insertTransaction.CommitAsync();
		}

		object? secondResult = await secondWrite;
		if (abortStartsFirst)
		{
			await insertTransaction.CommitAsync();
		}
		else
		{
			await abortTransaction.CommitAsync();
		}

		Guid jobId = abortStartsFirst ? (Guid)secondResult! : (Guid)firstWrite.Result!;
		Assert.Equal(JobStates.Cancelled, await GetStateAsync(jobId));
	}

	[Fact]
	public async Task OnlyTheSweepRemains_RecoveryStillCancelsRatherThanRequeues()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand disable = new("ALTER TABLE jobs DISABLE TRIGGER jobs_no_queued_under_aborted_run", connection))
		{
			await disable.ExecuteNonQueryAsync();
		}

		try
		{
			Guid runId = await SeedRunAsync("running");
			Guid jobId = await InsertJobAsync(runId, "running", "dead-worker");
			await ExecuteAsync("UPDATE runs SET state = 'aborted' WHERE id = $1", runId);
			Assert.Equal(JobStates.Cancelled, Assert.Single(await _repository.RecoverExpiredLeasesAsync(10, CancellationToken.None), job => job.Id == jobId).NewState);
		}
		finally
		{
			await using NpgsqlCommand enable = new("ALTER TABLE jobs ENABLE TRIGGER jobs_no_queued_under_aborted_run", connection);
			await enable.ExecuteNonQueryAsync();
		}
	}

	[Fact]
	public async Task OnlyTheTriggerRemains_APreFixSweepStillCannotRequeue()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobId = await InsertJobAsync(runId, "running", "dead-worker");
		await ExecuteAsync("UPDATE runs SET state = 'aborted' WHERE id = $1", runId);
		await ExecuteAsync("UPDATE jobs SET state = 'queued', claimed_by = NULL, claimed_at = NULL, lease_expires_at = NULL, heartbeat_at = NULL WHERE id = $1", jobId);
		Assert.Equal(JobStates.Cancelled, await GetStateAsync(jobId));
	}

	[Fact]
	public async Task RunQueueState_ReadsEveryField_AndMissingRunReturnsNull()
	{
		Guid runId = await SeedRunAsync("running");
		await ExecuteAsync("UPDATE runs SET paused = true, blocked = true, blocked_reason = 'operator hold' WHERE id = $1", runId);
		RunQueueState state = Assert.IsType<RunQueueState>(await _repository.GetRunQueueStateAsync(runId, CancellationToken.None));
		Assert.Equal("running", state.State);
		Assert.True(state.Paused);
		Assert.True(state.Blocked);
		Assert.Equal("operator hold", state.BlockedReason);
		Assert.Null(await _repository.GetRunQueueStateAsync(Guid.NewGuid(), CancellationToken.None));
	}

	private async Task<Guid> SeedRunAsync(string state)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO runs (run_type, scope, state) VALUES ('download', '{}', $1) RETURNING id", c);
		q.Parameters.AddWithValue(state); return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertJobAsync(Guid runId, string state, string? worker)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO jobs (run_id, job_type, priority, state, claimed_by, claimed_at, lease_expires_at, heartbeat_at, attempt_count, max_attempts) VALUES ($1, 'download', 1, $2, $3::text, CASE WHEN $3::text IS NULL THEN NULL ELSE now() END, CASE WHEN $3::text IS NULL THEN NULL ELSE now() - interval '1 second' END, CASE WHEN $3::text IS NULL THEN NULL ELSE now() - interval '2 seconds' END, CASE WHEN $3::text IS NULL THEN 0 ELSE 1 END, 3) RETURNING id", c);
		q.Parameters.AddWithValue(runId); q.Parameters.AddWithValue(state); q.Parameters.AddWithValue((object?)worker ?? DBNull.Value); return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task ExecuteAsync(string sql, Guid id)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync(); await using NpgsqlCommand q = new(sql, c); q.Parameters.AddWithValue(id); await q.ExecuteNonQueryAsync();
	}
	private async Task<string> GetStateAsync(Guid id)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync(); await using NpgsqlCommand q = new("SELECT state FROM jobs WHERE id = $1", c); q.Parameters.AddWithValue(id); return (string)(await q.ExecuteScalarAsync())!;
	}
}
