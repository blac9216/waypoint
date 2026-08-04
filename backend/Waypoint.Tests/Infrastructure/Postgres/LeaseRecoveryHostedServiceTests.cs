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
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Exercises <see cref="LeaseRecoveryHostedService"/> itself (not just the repository
/// method it wraps -- see <see cref="JobLeaseRecoveryTests"/> for that), including that
/// a recovered job's <c>job.state</c> event actually lands in <c>job_events</c>.
/// </summary>
[Collection("Postgres")]
public sealed class LeaseRecoveryHostedServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;

	public LeaseRecoveryHostedServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, NullLogger<JobEventPublisher>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task SweepAsync_RecoversAnExpiredJob_AndEmitsAJobStateEvent()
	{
		Guid jobId = await SeedQueuedJobAsync();
		TimeSpan lease = TimeSpan.FromMilliseconds(200);
		await _repository.ClaimJobAsync("crashed-worker", lease, CancellationToken.None);
		await Task.Delay(lease + TimeSpan.FromMilliseconds(300));

		LeaseRecoveryHostedService service = new(
			_repository, _events, Options.Create(new JobEngineOptions { Enabled = true }), NullLogger<LeaseRecoveryHostedService>.Instance);

		await service.SweepAsync(batchSize: 10, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection))
		{
			jobState.Parameters.AddWithValue(jobId);
			Assert.Equal("queued", (string)(await jobState.ExecuteScalarAsync())!);
		}

		await using NpgsqlCommand eventCount = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.state'", connection);
		eventCount.Parameters.AddWithValue(jobId);
		Assert.Equal(1L, (long)(await eventCount.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task SweepAsync_NothingExpired_IsANoOpAndEmitsNoEvents()
	{
		Guid jobId = await SeedQueuedJobAsync();
		await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);

		LeaseRecoveryHostedService service = new(
			_repository, _events, Options.Create(new JobEngineOptions { Enabled = true }), NullLogger<LeaseRecoveryHostedService>.Instance);

		await service.SweepAsync(batchSize: 10, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection);
		jobState.Parameters.AddWithValue(jobId);
		Assert.Equal("running", (string)(await jobState.ExecuteScalarAsync())!);
	}

	/// <summary>A backlog bigger than one batch is drained across several passes within a single <see cref="LeaseRecoveryHostedService.SweepAsync"/> call.</summary>
	[Fact]
	public async Task SweepAsync_DrainsABacklogLargerThanOneBatch()
	{
		const int jobCount = 12;
		TimeSpan lease = TimeSpan.FromMilliseconds(150);

		for (int i = 0; i < jobCount; i++)
		{
			Guid jobId = await SeedQueuedJobAsync();
			await _repository.ClaimJobAsync($"worker-{i}", lease, CancellationToken.None);
		}

		await Task.Delay(lease + TimeSpan.FromMilliseconds(300));

		LeaseRecoveryHostedService service = new(
			_repository, _events, Options.Create(new JobEngineOptions { Enabled = true }), NullLogger<LeaseRecoveryHostedService>.Instance);

		// Batch size smaller than the backlog: proves SweepAsync loops internally
		// rather than leaving most of the backlog for the next timer tick.
		await service.SweepAsync(batchSize: 5, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand remaining = new("SELECT count(*) FROM jobs WHERE state = 'running'", connection);
		Assert.Equal(0L, (long)(await remaining.ExecuteScalarAsync())!);
	}

	private async Task<Guid> SeedQueuedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('download', 1, 'queued') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}
}
