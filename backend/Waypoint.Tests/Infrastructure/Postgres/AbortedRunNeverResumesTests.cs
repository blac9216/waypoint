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
	public async Task DirectUpdate_CannotQueueAJobUnderAnAbortedRun()
	{
		Guid runId = await SeedRunAsync("running");
		Guid jobId = await InsertJobAsync(runId, "queued", null);
		await ExecuteAsync("UPDATE runs SET state = 'aborted' WHERE id = $1", runId);
		await ExecuteAsync("UPDATE jobs SET state = 'queued' WHERE id = $1", jobId);
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
			new JobEventPublisher(_fixture.ConnectionString, 5, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([handler]), Options.Create(new JobEngineOptions { PollInterval = TimeSpan.FromMilliseconds(25) }),
			NullLogger<JobDispatcherHostedService>.Instance);
		await dispatcher.StartAsync(CancellationToken.None);
		await Task.Delay(TimeSpan.FromMilliseconds(500));
		await dispatcher.StopAsync(CancellationToken.None);
		Assert.Equal(0, calls);
		Assert.Equal(JobStates.Cancelled, await GetStateAsync(jobId));
	}

	private async Task<Guid> SeedRunAsync(string state)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO runs (run_type, scope, state) VALUES ('test', '{}', $1) RETURNING id", c);
		q.Parameters.AddWithValue(state); return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertJobAsync(Guid runId, string state, string? worker)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString); await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO jobs (run_id, job_type, priority, state, claimed_by, claimed_at, lease_expires_at, heartbeat_at, attempt_count, max_attempts) VALUES ($1, 'download', 1, $2, $3, CASE WHEN $3 IS NULL THEN NULL ELSE now() END, CASE WHEN $3 IS NULL THEN NULL ELSE now() - interval '1 second' END, CASE WHEN $3 IS NULL THEN NULL ELSE now() - interval '2 seconds' END, CASE WHEN $3 IS NULL THEN 0 ELSE 1 END, 3) RETURNING id", c);
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
