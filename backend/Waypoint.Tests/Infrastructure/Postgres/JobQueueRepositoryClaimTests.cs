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
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves <see cref="JobQueueRepository.ClaimJobAsync"/> -- the production claim, not
/// the hand-rolled SQL <c>JobsQueueClaimTests</c> already proved -- keeps the same
/// double-claim-free guarantee under real concurrency, and additionally that it always
/// stamps a lease atomically with the claim (issue #107's actual defense: even without
/// the CHECK constraint, this is the one and only code path production ever uses to set
/// <c>state = 'running'</c>).
/// </summary>
[Collection("Postgres")]
public sealed class JobQueueRepositoryClaimTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public JobQueueRepositoryClaimTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// The headline proof this issue asks for: "two dispatcher instances never claim
	/// the same job." Each simulated dispatcher is its own <see cref="JobQueueRepository"/>
	/// instance (its own connection, per call) racing in its own <see cref="Task"/> --
	/// exactly two "instances", run repeatedly across many jobs so the race window is
	/// exercised many times, not just once.
	/// </summary>
	[Fact]
	public async Task TwoDispatcherInstances_RacingForTheSameQueue_NeverClaimTheSameJob()
	{
		const int jobCount = 200;
		const int dispatcherCount = 2;
		await SeedQueuedJobsAsync(jobCount);

		JobQueueRepository dispatcherA = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		JobQueueRepository dispatcherB = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		List<ClaimedJob> claimedByA = [];
		List<ClaimedJob> claimedByB = [];

		async Task DrainAsync(JobQueueRepository dispatcher, string workerId, List<ClaimedJob> into)
		{
			while (true)
			{
				ClaimedJob? job = await dispatcher.ClaimJobAsync(workerId, TimeSpan.FromMinutes(5), CancellationToken.None);
				if (job is null)
				{
					return;
				}

				into.Add(job);
			}
		}

		await Task.WhenAll(
			DrainAsync(dispatcherA, "dispatcher-a", claimedByA),
			DrainAsync(dispatcherB, "dispatcher-b", claimedByB));

		Guid[] allClaimed = [.. claimedByA.Select(j => j.Id), .. claimedByB.Select(j => j.Id)];

		Assert.Equal(jobCount, allClaimed.Length);
		Assert.Equal(jobCount, allClaimed.Distinct().Count());
		_ = dispatcherCount; // documents intent; the two DrainAsync calls above are the two instances.
	}

	/// <summary>The exact multi-attempt concurrency shape the issue asks for: many concurrent claimers, one job each, none overlapping.</summary>
	[Fact]
	public async Task ManyConcurrentClaimers_OneJobEach_NeverClaimTheSameJob()
	{
		const int jobCount = 64;
		await SeedQueuedJobsAsync(jobCount);

		Task<ClaimedJob?>[] claims = [.. Enumerable.Range(0, jobCount)
			.Select(i => _repository.ClaimJobAsync($"worker-{i}", TimeSpan.FromMinutes(5), CancellationToken.None))];

		ClaimedJob?[] results = await Task.WhenAll(claims);
		Guid[] claimedIds = [.. results.Where(job => job is not null).Select(job => job!.Id)];

		Assert.Equal(jobCount, claimedIds.Length);
		Assert.Equal(jobCount, claimedIds.Distinct().Count());
	}

	[Fact]
	public async Task ClaimJobAsync_StampsLeaseAtomicallyWithTheClaim()
	{
		await SeedQueuedJobsAsync(1);

		ClaimedJob? job = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(job);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"SELECT state, claimed_by, lease_expires_at IS NOT NULL, heartbeat_at IS NOT NULL, attempt_count FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(job!.Id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("running", reader.GetString(0));
		Assert.Equal("worker-a", reader.GetString(1));
		Assert.True(reader.GetBoolean(2), "lease_expires_at must be set by the same statement that claims the job.");
		Assert.True(reader.GetBoolean(3), "heartbeat_at must be set by the claim.");
		Assert.Equal(1, reader.GetInt32(4));
	}

	[Fact]
	public async Task ClaimJobAsync_EmptyQueue_ReturnsNull()
	{
		Assert.Null(await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None));
	}

	[Fact]
	public async Task ClaimJobAsync_RespectsPriorityThenAge()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Lower priority number = claimed first, regardless of insertion order.
		Guid lowPriorityOlder = await InsertQueuedJobAsync(connection, priority: 5);
		await Task.Delay(TimeSpan.FromMilliseconds(5));
		Guid highPriorityNewer = await InsertQueuedJobAsync(connection, priority: 1);

		ClaimedJob? first = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.Equal(highPriorityNewer, first!.Id);

		ClaimedJob? second = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.Equal(lowPriorityOlder, second!.Id);
	}

	private async Task SeedQueuedJobsAsync(int count)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		for (int i = 0; i < count; i++)
		{
			await InsertQueuedJobAsync(connection, priority: (short)((i % 6) + 1));
		}
	}

	private static async Task<Guid> InsertQueuedJobAsync(NpgsqlConnection connection, short priority)
	{
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, target_name) VALUES ('download', $1, 'queued', $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(priority);
		insert.Parameters.AddWithValue($"artifact-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}
}
