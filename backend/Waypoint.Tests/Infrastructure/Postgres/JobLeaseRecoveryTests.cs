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
/// Proves dead-job recovery against a real PostgreSQL 16 container: a crashed worker's
/// job genuinely comes back (or fails once attempts are exhausted), a worker that is
/// merely slow but still heartbeating is never touched, and two concurrent recovery
/// sweeps never double-recover the same expired-lease row.
/// </summary>
[Collection("Postgres")]
public sealed class JobLeaseRecoveryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public JobLeaseRecoveryTests(PostgresFixture fixture)
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
	/// "Killing a worker mid-job -> lease expiry -> job re-queued... per policy": claims
	/// a job with a short lease and, unlike every other test here, never renews it --
	/// standing in for a worker that crashed the instant after claiming. Once the lease
	/// genuinely expires (real wall-clock wait, not a backdated column), the sweep must
	/// requeue it with a lease-free, unclaimed row -- and because that requeue happens
	/// in the same statement that leaves 'running', it can never itself violate
	/// jobs_running_requires_lease_check (issue #107).
	/// </summary>
	[Fact]
	public async Task CrashedWorkersLease_ExpiresAndIsRecovered_JobReturnsToQueue()
	{
		Guid jobId = await SeedQueuedJobAsync(maxAttempts: 3);
		TimeSpan shortLease = TimeSpan.FromMilliseconds(300);

		ClaimedJob? claimed = await _repository.ClaimJobAsync("crashed-worker", shortLease, JobCapabilities.All, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(1, claimed!.AttemptCount);

		// The "crash": no heartbeat, ever. Wait past the lease.
		await Task.Delay(shortLease + TimeSpan.FromMilliseconds(400));

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(batchSize: 10, CancellationToken.None);

		RecoveredJob thisJob = Assert.Single(recovered, job => job.Id == jobId);
		Assert.Equal(JobStates.Queued, thisJob.NewState);
		Assert.Equal(1, thisJob.AttemptCount);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand verify = new(
			"SELECT state, claimed_by, lease_expires_at, heartbeat_at FROM jobs WHERE id = $1", connection);
		verify.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("queued", reader.GetString(0));
		Assert.True(reader.IsDBNull(1), "claimed_by must be cleared on requeue.");
		Assert.True(reader.IsDBNull(2), "lease_expires_at must be cleared on requeue.");
		Assert.True(reader.IsDBNull(3), "heartbeat_at must be cleared on requeue.");
	}

	[Fact]
	public async Task Recovery_RejectsInvalidBatch_AndCapturingLoggerRecordsRecoveredRow()
	{
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.RecoverExpiredLeasesAsync(0, CancellationToken.None));
		Guid jobId = await SeedQueuedJobAsync(maxAttempts: 3);
		await _repository.ClaimJobAsync("crashed-worker", TimeSpan.FromMilliseconds(100), JobCapabilities.All, CancellationToken.None);
		await Task.Delay(TimeSpan.FromMilliseconds(250));
		Waypoint.Tests.Support.CapturingLogger<JobQueueRepository> logger = new();
		JobQueueRepository capturing = new(_fixture.ConnectionString, logger);
		Assert.Contains(await capturing.RecoverExpiredLeasesAsync(10, CancellationToken.None), job => job.Id == jobId);
		Assert.Contains(logger.Entries, entry => entry.Message.Contains(jobId.ToString(), StringComparison.Ordinal));
	}


	/// <summary>A job that has already exhausted its attempts on its last, crashed attempt fails instead of requeuing forever.</summary>
	[Fact]
	public async Task CrashedWorkersLease_AtMaxAttempts_FailsInsteadOfRequeuing()
	{
		Guid jobId = await SeedQueuedJobAsync(maxAttempts: 1);
		TimeSpan shortLease = TimeSpan.FromMilliseconds(300);

		await _repository.ClaimJobAsync("crashed-worker", shortLease, JobCapabilities.All, CancellationToken.None);
		await Task.Delay(shortLease + TimeSpan.FromMilliseconds(400));

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(batchSize: 10, CancellationToken.None);

		RecoveredJob thisJob = Assert.Single(recovered, job => job.Id == jobId);
		Assert.Equal(JobStates.Failed, thisJob.NewState);
	}

	/// <summary>
	/// "The recovery cannot double-run a job that is merely slow": a job whose worker is
	/// still alive and heartbeating (renewing the lease on a cadence shorter than the
	/// lease itself) must never be touched by a concurrent recovery sweep, no matter how
	/// many times the sweep runs while the job is in flight.
	/// </summary>
	[Fact]
	public async Task SlowButHeartbeatingWorker_IsNeverRecovered()
	{
		Guid jobId = await SeedQueuedJobAsync(maxAttempts: 3);
		// Generous margin against real network/round-trip jitter against a containerized
		// Postgres: a 300ms lease with an 80ms heartbeat cadence gave only 3-4 renewals
		// before expiry, and a single delayed renewal (thread-pool/connection-open
		// latency) could let the lease genuinely lapse and make this test flaky for a
		// reason that has nothing to do with the property under test. 2s/250ms gives
		// roughly 8 renewals per lease window.
		TimeSpan lease = TimeSpan.FromSeconds(2);
		TimeSpan heartbeatCadence = TimeSpan.FromMilliseconds(250);

		ClaimedJob? claimed = await _repository.ClaimJobAsync("slow-worker", lease, JobCapabilities.All, CancellationToken.None);
		Assert.NotNull(claimed);

		using CancellationTokenSource stop = new();

		Task heartbeatLoop = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				await _repository.RenewLeaseAsync(jobId, "slow-worker", lease, CancellationToken.None);
				await Task.Delay(heartbeatCadence);
			}
		});

		// Sweep aggressively -- far more often than the lease would naturally expire --
		// for several seconds while the "slow" worker keeps heartbeating alongside it.
		int totalRecoveredElsewhere = 0;
		for (int i = 0; i < 20; i++)
		{
			IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(batchSize: 10, CancellationToken.None);
			totalRecoveredElsewhere += recovered.Count(job => job.Id == jobId);
			await Task.Delay(TimeSpan.FromMilliseconds(150));
		}

		await stop.CancelAsync();
		await heartbeatLoop;

		Assert.Equal(0, totalRecoveredElsewhere);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand verify = new("SELECT state, claimed_by FROM jobs WHERE id = $1", connection);
		verify.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("running", reader.GetString(0));
		Assert.Equal("slow-worker", reader.GetString(1));
	}

	/// <summary>
	/// Two concurrent recovery sweeps (e.g. two Waypoint instances both running
	/// <c>LeaseRecoveryHostedService</c>) racing over the same batch of expired leases
	/// must never both recover the same row -- <c>RecoverSql</c>'s <c>FOR UPDATE SKIP
	/// LOCKED</c> is what this proves. See the PR body's "Empirical evidence" section
	/// for this test run against a deliberately naive (non-locking) recovery query,
	/// where it fails by observing duplicate recoveries.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public async Task ConcurrentRecoverySweeps_NeverDoubleRecoverTheSameJob(int independentRun)
	{
		const int jobCount = 40;
		TimeSpan lease = TimeSpan.FromMilliseconds(200);

		Guid[] jobIds = new Guid[jobCount];
		for (int i = 0; i < jobCount; i++)
		{
			jobIds[i] = await SeedQueuedJobAsync(maxAttempts: 3);
		}

		JobQueueRepository claimer = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		for (int i = 0; i < jobCount; i++)
		{
			await claimer.ClaimJobAsync($"worker-{independentRun}-{i}", lease, JobCapabilities.All, CancellationToken.None);
		}

		await Task.Delay(lease + TimeSpan.FromMilliseconds(400));

		JobQueueRepository sweeperA = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		JobQueueRepository sweeperB = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		async Task<List<RecoveredJob>> DrainAsync(JobQueueRepository sweeper)
		{
			List<RecoveredJob> all = [];
			while (true)
			{
				IReadOnlyList<RecoveredJob> batch = await sweeper.RecoverExpiredLeasesAsync(batchSize: 5, CancellationToken.None);
				if (batch.Count == 0)
				{
					return all;
				}

				all.AddRange(batch);
			}
		}

		List<RecoveredJob>[] results = await Task.WhenAll(DrainAsync(sweeperA), DrainAsync(sweeperB));
		Guid[] allRecoveredIds = [.. results.SelectMany(list => list).Select(job => job.Id)];

		Assert.Equal(jobCount, allRecoveredIds.Length);
		Assert.Equal(jobCount, allRecoveredIds.Distinct().Count());
		Assert.Equal(jobIds.OrderBy(id => id), allRecoveredIds.OrderBy(id => id));
	}

	private async Task<Guid> SeedQueuedJobAsync(int maxAttempts)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, max_attempts) VALUES ('download', 1, 'queued', $1) RETURNING id", connection);
		insert.Parameters.AddWithValue(maxAttempts);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}
}
