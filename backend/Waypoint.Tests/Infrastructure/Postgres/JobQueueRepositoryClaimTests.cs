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
				ClaimedJob? job = await dispatcher.ClaimJobAsync(workerId, TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
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

	/// <summary>
	/// The exact multi-attempt concurrency shape the issue asks for: many concurrent
	/// claimers, one job each, none overlapping. Each round starts from a freshly
	/// truncated and independently seeded queue. Run all three before asserting so a
	/// mutation of <c>FOR UPDATE SKIP LOCKED</c> has to survive three scheduling races,
	/// rather than getting one lucky run.
	/// </summary>
	[Fact]
	public async Task ManyConcurrentClaimers_AcrossThreeIndependentlySeededRuns_NeverClaimTheSameJob()
	{
		const int jobCount = 64;
		List<(int Round, int ClaimedCount, int DistinctClaimedCount)> rounds = [];

		for (int round = 1; round <= 3; round++)
		{
			await _fixture.ResetJobEngineDataAsync();
			await SeedQueuedJobsAsync(jobCount);

			Task<ClaimedJob?>[] claims = [.. Enumerable.Range(0, jobCount)
				.Select(i => _repository.ClaimJobAsync($"worker-{round}-{i}", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None))];

			ClaimedJob?[] results = await Task.WhenAll(claims);
			Guid[] claimedIds = [.. results.Where(job => job is not null).Select(job => job!.Id)];
			rounds.Add((round, claimedIds.Length, claimedIds.Distinct().Count()));
		}

		Assert.True(
			rounds.All(result => result.ClaimedCount == jobCount && result.DistinctClaimedCount == jobCount),
			$"Each independently seeded round must claim {jobCount} distinct jobs. " +
			string.Join("; ", rounds.Select(result =>
				$"round {result.Round}: claimed {result.ClaimedCount}, distinct {result.DistinctClaimedCount}")));
	}

	[Fact]
	public async Task ClaimJobAsync_StampsLeaseAtomicallyWithTheClaim()
	{
		await SeedQueuedJobsAsync(1);

		ClaimedJob? job = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
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
		Assert.Null(await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None));
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

		ClaimedJob? first = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		Assert.Equal(highPriorityNewer, first!.Id);

		ClaimedJob? second = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		Assert.Equal(lowPriorityOlder, second!.Id);
	}

	/// <summary>
	/// A claim must report the row it actually took, in full. Asserted as one value
	/// comparison rather than ten field assertions -- <see cref="ClaimedJob"/> is a
	/// record precisely so "did the claim return this row" is expressible directly, and a
	/// per-field version silently stops covering any field added later.
	/// </summary>
	[Fact]
	public async Task AClaim_ReportsEveryFieldOfTheRowItTook()
	{
		Guid credentialId;
		Guid targetId = Guid.NewGuid();
		Guid runId;

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();

			await using (NpgsqlCommand credential = new(
				"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection))
			{
				credential.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
				credentialId = (Guid)(await credential.ExecuteScalarAsync())!;
			}

			await using (NpgsqlCommand run = new(
				"INSERT INTO runs (run_type, state) VALUES ('scan', 'running') RETURNING id", connection))
			{
				runId = (Guid)(await run.ExecuteScalarAsync())!;
			}

			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO jobs (run_id, job_type, target_id, target_name, credential_id, priority, payload, state, max_attempts)
				VALUES ($1, 'scan', $2, 'esxi-01.example.internal', $3, 4, '{"profile":"vsphere"}'::jsonb, 'queued', 7)
				""", connection);
			insert.Parameters.AddWithValue(runId);
			insert.Parameters.AddWithValue(targetId);
			insert.Parameters.AddWithValue(credentialId);
			await insert.ExecuteNonQueryAsync();
		}

		ClaimedJob? claimed = await _repository.ClaimJobAsync("worker-fields", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		Assert.NotNull(claimed);

		ClaimedJob expected = new(
			Id: claimed.Id,
			RunId: runId,
			JobType: "scan",
			TargetId: targetId,
			TargetName: "esxi-01.example.internal",
			CredentialId: credentialId,
			Priority: 4,
			Payload: claimed.Payload,
			AttemptCount: 1,
			MaxAttempts: 7);

		Assert.Equal(expected, claimed);
		Assert.Equal(expected.GetHashCode(), claimed.GetHashCode());
		Assert.Contains("esxi-01.example.internal", claimed.ToString(), StringComparison.Ordinal);
		Assert.Contains("vsphere", claimed.Payload, StringComparison.Ordinal);
	}

	/// <summary>
	/// Two claims of two different rows must never compare equal. Without this, the value
	/// comparison above would be satisfied by a record whose equality was somehow
	/// degenerate, and the two-dispatcher distinctness proof leans on the same semantics.
	/// </summary>
	[Fact]
	public async Task TwoDistinctClaims_AreNotEqualByValue()
	{
		await SeedQueuedJobsAsync(2);

		ClaimedJob? first = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		ClaimedJob? second = await _repository.ClaimJobAsync("worker-b", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.NotEqual(first, second);
	}

	/// <summary>
	/// Every other fixture in this class -- and in <c>JobsQueueClaimTests</c> -- claims a
	/// job that has never been attempted, so <c>attempt_count</c> is 0 at claim time in all
	/// of them. That shared property is load-bearing (docs/testing.md, "Fixture
	/// monoculture"): a predicate narrowing the claim to first attempts only, such as the
	/// <c>AND attempt_count = 0</c> in #140's worked example, passes every other test in the
	/// repository while quietly making a retry unclaimable forever. A job that failed once
	/// with <c>max_attempts = 3</c> would sit <c>queued</c> and never be dispatched again --
	/// invisible, because the queue looks healthy and the row looks retryable.
	///
	/// This closes the behavioural half of that exposure. The static half -- #140's actual
	/// subject, that <c>JobQueueClaimSqlParityTests</c> only reads as far as the locking CTE
	/// and so cannot see a predicate added to the UPDATE's own WHERE -- is still open and
	/// still owned there; a guard that reads the whole statement catches drift this fixture
	/// would only catch if it happened to change an observable outcome.
	/// </summary>
	[Fact]
	public async Task ClaimJobAsync_ClaimsAJobThatAlreadyFailedAnAttempt()
	{
		Guid retryJobId;

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();

			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO jobs (job_type, priority, state, attempt_count, max_attempts, target_name)
				VALUES ('download', 1, 'queued', 1, 3, $1)
				RETURNING id
				""", connection);
			insert.Parameters.AddWithValue($"retry-{Guid.NewGuid():N}");
			retryJobId = (Guid)(await insert.ExecuteScalarAsync())!;
		}

		ClaimedJob? claimed = await _repository.ClaimJobAsync("worker-retry", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);

		Assert.NotNull(claimed);
		Assert.Equal(retryJobId, claimed.Id);

		// The second attempt is counted as the second, not reset to the first: #129's
		// max_attempts check reads this number to decide when to stop retrying.
		Assert.Equal(2, claimed.AttemptCount);
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
