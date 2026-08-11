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
/// Issue #435 (ADR-0014): proves the job-type allowlist <see cref="JobQueueRepository.ClaimJobAsync"/>
/// now requires is enforced inside the atomic claim statement, not as a post-claim
/// filter -- "unlike runners never cross-claim under concurrency," "identical replicas
/// compete safely," and everything <see cref="JobQueueRepositoryClaimTests"/> already
/// proved about priority/ordering/lease/recovery stays true once an allowlist is
/// threaded through every call. This file is additive: it does not replace or narrow
/// that class's coverage, which continues to exercise the same claim path with
/// <see cref="JobCapabilities.All"/>.
/// </summary>
[Collection("Postgres")]
public sealed class JobQueueRepositoryAllowlistClaimTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public JobQueueRepositoryAllowlistClaimTests(PostgresFixture fixture)
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

	[Fact]
	public async Task ClaimJobAsync_NullAllowlist_ThrowsAndClaimsNothing()
	{
		await SeedQueuedJobAsync("scan", priority: 1);

		await Assert.ThrowsAsync<ArgumentException>(
			() => _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), null!, CancellationToken.None));

		Assert.Equal("queued", await ReadStateAsync());
	}

	[Fact]
	public async Task ClaimJobAsync_EmptyAllowlist_ThrowsAndClaimsNothing()
	{
		await SeedQueuedJobAsync("scan", priority: 1);

		await Assert.ThrowsAsync<ArgumentException>(
			() => _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), new HashSet<string>(), CancellationToken.None));

		Assert.Equal("queued", await ReadStateAsync());
	}

	[Fact]
	public async Task ClaimJobAsync_AllowlistExcludingTheOnlyQueuedType_ReturnsNullAndLeavesItQueued()
	{
		await SeedQueuedJobAsync("download", priority: 1);

		ClaimedJob? claimed = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);

		Assert.Null(claimed);
		Assert.Equal("queued", await ReadStateAsync());
	}

	[Fact]
	public async Task ClaimJobAsync_AllowlistIncludingTheQueuedType_ClaimsIt()
	{
		Guid jobId = await SeedQueuedJobAsync("scan", priority: 1);

		ClaimedJob? claimed = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);

		Assert.NotNull(claimed);
		Assert.Equal(jobId, claimed.Id);
		Assert.Equal("scan", claimed.JobType);
	}

	/// <summary>
	/// A download-shaped allowlist skips over a higher-priority compliance job to claim
	/// its own lower-priority-number... i.e. it must still find and claim the
	/// highest-priority job *within its allowlist*, not simply the highest-priority job
	/// in the whole queue. Seeds a priority-1 scan (compliance-only) and a priority-3
	/// download (download-only); a download allowlist must return the download despite
	/// the scan outranking it globally.
	/// </summary>
	[Fact]
	public async Task ClaimJobAsync_SkipsHigherPriorityJobOutsideItsAllowlist()
	{
		await SeedQueuedJobAsync("scan", priority: 1);
		Guid downloadJobId = await SeedQueuedJobAsync("download", priority: 3);

		ClaimedJob? claimed = await _repository.ClaimJobAsync("download-worker", TimeSpan.FromMinutes(5), JobCapabilities.Download, CancellationToken.None);

		Assert.NotNull(claimed);
		Assert.Equal(downloadJobId, claimed.Id);
		Assert.Equal("download", claimed.JobType);
	}

	/// <summary>
	/// Ordering within the allowlist is unchanged: two compliance-allowlisted job types
	/// still claim in priority order, exactly as <see cref="JobQueueRepositoryClaimTests.ClaimJobAsync_RespectsPriorityThenAge"/>
	/// proved for the unfiltered claim.
	/// </summary>
	[Fact]
	public async Task ClaimJobAsync_WithAllowlist_StillRespectsPriorityThenAge()
	{
		Guid lowPriorityOlder = await SeedQueuedJobAsync("discover", priority: 5);
		await Task.Delay(TimeSpan.FromMilliseconds(5));
		Guid highPriorityNewer = await SeedQueuedJobAsync("scan", priority: 1);

		ClaimedJob? first = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		Assert.Equal(highPriorityNewer, first!.Id);

		ClaimedJob? second = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		Assert.Equal(lowPriorityOlder, second!.Id);
	}

	/// <summary>
	/// The headline concurrency proof this issue asks for: two differently-capable
	/// runners (a compliance-only claimer and a download-only claimer) racing against a
	/// mixed queue must never claim outside their own allowlist, and between them must
	/// drain the whole queue exactly once. Each "runner" is its own
	/// <see cref="JobQueueRepository"/> instance (its own connection per call) racing in
	/// its own <see cref="Task"/>, the same shape <c>JobQueueRepositoryClaimTests</c>
	/// uses for its two-dispatcher proof -- ADR-0014's new requirement layered on top of
	/// that proven concurrency idiom.
	/// </summary>
	[Fact]
	public async Task UnlikeRunners_RacingAMixedQueue_NeverCrossClaim()
	{
		const int perTypeCount = 100;
		List<Guid> complianceJobIds = [];
		List<Guid> downloadJobIds = [];

		for (int i = 0; i < perTypeCount; i++)
		{
			complianceJobIds.Add(await SeedQueuedJobAsync("scan", priority: (short)((i % 6) + 1)));
			downloadJobIds.Add(await SeedQueuedJobAsync("download", priority: (short)((i % 6) + 1)));
		}

		JobQueueRepository complianceRunner = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		JobQueueRepository downloadRunner = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		List<ClaimedJob> claimedByCompliance = [];
		List<ClaimedJob> claimedByDownload = [];

		async Task DrainAsync(JobQueueRepository runner, string workerId, IReadOnlySet<string> allowlist, List<ClaimedJob> into)
		{
			while (true)
			{
				ClaimedJob? job = await runner.ClaimJobAsync(workerId, TimeSpan.FromMinutes(5), allowlist, CancellationToken.None);
				if (job is null)
				{
					return;
				}

				into.Add(job);
			}
		}

		await Task.WhenAll(
			DrainAsync(complianceRunner, "compliance-runner", JobCapabilities.Compliance, claimedByCompliance),
			DrainAsync(downloadRunner, "download-runner", JobCapabilities.Download, claimedByDownload));

		Assert.Equal(perTypeCount, claimedByCompliance.Count);
		Assert.Equal(perTypeCount, claimedByDownload.Count);
		Assert.All(claimedByCompliance, job => Assert.Equal("scan", job.JobType));
		Assert.All(claimedByDownload, job => Assert.Equal("download", job.JobType));
		Assert.Equal([.. complianceJobIds.Order()], claimedByCompliance.Select(j => j.Id).Order());
		Assert.Equal([.. downloadJobIds.Order()], claimedByDownload.Select(j => j.Id).Order());
	}

	/// <summary>
	/// Identical replicas (same allowlist, same domain) must still compete safely for
	/// the same eligible slice of the queue -- the allowlist predicate narrows *what*
	/// is claimable, not the double-claim-free guarantee over that narrowed set.
	/// jobCount matches <c>JobQueueRepositoryClaimTests.ManyConcurrentClaimers_...</c>'s
	/// 64-way concurrency rather than going higher: each claimer here opens its own
	/// connection for the whole test, and the shared test-container Postgres
	/// (docs/testing.md) runs many other test classes' connections concurrently within
	/// the same default <c>max_connections</c> budget.
	/// </summary>
	[Fact]
	public async Task IdenticalReplicas_WithTheSameAllowlist_CompeteSafelyWithNoDoubleClaim()
	{
		const int jobCount = 64;
		List<Guid> jobIds = [];
		for (int i = 0; i < jobCount; i++)
		{
			jobIds.Add(await SeedQueuedJobAsync("download", priority: (short)((i % 6) + 1)));
		}

		// A handful of jobs outside the allowlist seeded alongside, so a bug that
		// dropped the predicate (falling back to a global claim) would be caught by
		// claiming more than jobCount distinct rows below.
		for (int i = 0; i < 10; i++)
		{
			await SeedQueuedJobAsync("scan", priority: 1);
		}

		Task<ClaimedJob?>[] claims = [.. Enumerable.Range(0, jobCount)
			.Select(i => new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance)
				.ClaimJobAsync($"download-replica-{i}", TimeSpan.FromMinutes(5), JobCapabilities.Download, CancellationToken.None))];

		ClaimedJob?[] results = await Task.WhenAll(claims);
		Guid[] claimedIds = [.. results.Where(job => job is not null).Select(job => job!.Id)];

		Assert.Equal(jobCount, claimedIds.Length);
		Assert.Equal(jobCount, claimedIds.Distinct().Count());
		Assert.Equal([.. jobIds.Order()], claimedIds.Order());
		Assert.All(results, job => Assert.Equal("download", job!.JobType));
	}

	private async Task<Guid> SeedQueuedJobAsync(string jobType, short priority)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, target_name) VALUES ($1, $2, 'queued', $3) RETURNING id", connection);
		insert.Parameters.AddWithValue(jobType);
		insert.Parameters.AddWithValue(priority);
		insert.Parameters.AddWithValue($"target-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<string> ReadStateAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new("SELECT state FROM jobs LIMIT 1", connection);
		return (string)(await command.ExecuteScalarAsync())!;
	}
}
