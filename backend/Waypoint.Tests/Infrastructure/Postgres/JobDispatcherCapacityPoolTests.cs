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
using Waypoint.Core.Capacity;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Capacity;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Resources;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #569's dispatcher-integration acceptance criteria against real Postgres,
/// mirroring <see cref="JobDispatcherResourceAdmissionTests"/>'s structure for the
/// shared pool: the multi-runner overcommit case ADR-0018 Option B exists to fix (two
/// replica dispatchers whose LOCAL budgets would happily overcommit the host are
/// jointly bounded by the shared pool), pool-denied claims released back to queued
/// (never executed, never lost), lease release on terminal state, and the
/// DB-unavailable posture (no pool reachable, no work starts -- never silent
/// overcommit).
/// </summary>
[Collection("Postgres")]
public sealed class JobDispatcherCapacityPoolTests : IAsyncLifetime
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;
	private CapacityLeasePoolRepository _pool = null!;

	public JobDispatcherCapacityPoolTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ExecuteAsync("TRUNCATE TABLE capacity_leases; DELETE FROM capacity_pool");
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
		_pool = new CapacityLeasePoolRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private static CapacityLeaseCoordinator CreateCoordinator(ICapacityLeasePool pool) =>
		new(pool, Options.Create(new CapacityPoolOptions()), NullLogger<CapacityLeaseCoordinator>.Instance);

	private JobDispatcherHostedService CreateDispatcher(JobEngineOptions options, CapacityLeaseCoordinator? capacityPool, params IJobHandler[] handlers) =>
		new(_repository, _repository, _events, new JobHandlerRegistry(handlers), Options.Create(options), resourceAdmission: null, capacityPool, NullLogger<JobDispatcherHostedService>.Instance);

	/// <summary>
	/// A pool with no free room must never let a claimed job execute: it is claimed,
	/// pool-denied, and released back to queued -- the same claim-then-release loop
	/// as a local admission denial, repeated for as long as the pool stays full.
	/// </summary>
	[Fact]
	public async Task PoolFull_JobIsClaimedThenReleasedRatherThanExecuted()
	{
		await _pool.RegisterPoolCapacityAsync("test", 1.0, 1024L * 1024 * 1024, operatorSet: false, CancellationToken.None);

		// Another runner's RUNNING job holds the whole pool (running so this
		// dispatcher can never claim it and convert its lease for itself).
		Guid holder = await SeedJobAsync("download", "running");
		Assert.True(await _pool.TryClaimAsync(holder, "other-runner", "download", 1.0, 1024, Lease, CancellationToken.None));

		Guid jobId = await SeedQueuedJobAsync("download");
		int executionCount = 0;
		FakeJobHandler handler = new("download", (_, _) => { Interlocked.Increment(ref executionCount); return Task.FromResult(JobExecutionOutcome.Succeeded()); });

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 },
			CreateCoordinator(_pool), handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			// Several claim/deny/release cycles: the job must keep landing back in
			// queued, never executing, while the pool is fully held elsewhere.
			await Task.Delay(TimeSpan.FromMilliseconds(500));
			Assert.Equal(0, executionCount);
			Assert.Equal(JobStates.Queued, await GetJobStateAsync(jobId));
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>Happy path: with pool room, the job executes and its capacity lease is released on terminal state.</summary>
	[Fact]
	public async Task JobWithinPool_Executes_AndReleasesItsCapacityLeaseOnCompletion()
	{
		await _pool.RegisterPoolCapacityAsync("test", 4.0, 4L * 1024 * 1024 * 1024, operatorSet: false, CancellationToken.None);
		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) },
			CreateCoordinator(_pool), handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Done);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(JobStates.Done, await GetJobStateAsync(jobId));
		Assert.Equal(0, await CountLeasesAsync());
	}

	/// <summary>
	/// The ADR-0018 Option B acceptance criterion itself: two replica dispatchers whose
	/// LOCAL budgets (here: none at all -- resourceAdmission null, MaxConcurrency 4
	/// each) would happily run everything at once are jointly bounded by the shared
	/// pool, which only fits one "download" (0.5 cores) at a time. Combined observed
	/// concurrency must never exceed 1 -- the overcommit that per-runner-only admission
	/// allowed is structurally gone.
	/// </summary>
	[Fact]
	public async Task TwoReplicaDispatchers_SharedPoolJointlyBoundsThem_NeverOvercommitting()
	{
		await _pool.RegisterPoolCapacityAsync("test", 0.5, 1024L * 1024 * 1024, operatorSet: false, CancellationToken.None);

		Guid[] jobIds =
		[
			await SeedQueuedJobAsync("download"),
			await SeedQueuedJobAsync("download"),
			await SeedQueuedJobAsync("download"),
		];

		int maxObservedConcurrentExecutions = 0;
		int currentExecutions = 0;
		object concurrencyLock = new();

		FakeJobHandler MakeHandler() => new("download", async (_, ct) =>
		{
			lock (concurrencyLock)
			{
				currentExecutions++;
				maxObservedConcurrentExecutions = Math.Max(maxObservedConcurrentExecutions, currentExecutions);
			}

			await Task.Delay(TimeSpan.FromMilliseconds(150), ct);

			lock (concurrencyLock)
			{
				currentExecutions--;
			}

			return JobExecutionOutcome.Succeeded();
		});

		JobDispatcherHostedService dispatcherA = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(30), MaxConcurrency = 4 },
			CreateCoordinator(new CapacityLeasePoolRepository(_fixture.ConnectionString)), MakeHandler());
		JobDispatcherHostedService dispatcherB = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(30), MaxConcurrency = 4 },
			CreateCoordinator(new CapacityLeasePoolRepository(_fixture.ConnectionString)), MakeHandler());

		await dispatcherA.StartAsync(CancellationToken.None);
		await dispatcherB.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(
				async () =>
				{
					string[] states = await Task.WhenAll(jobIds.Select(GetJobStateAsync));
					return states.All(state => state == JobStates.Done);
				},
				allDone => allDone);
		}
		finally
		{
			await dispatcherA.StopAsync(CancellationToken.None);
			await dispatcherB.StopAsync(CancellationToken.None);
		}

		Assert.Equal(1, maxObservedConcurrentExecutions);
		Assert.Equal(0, await CountLeasesAsync());
	}

	/// <summary>
	/// The DB-unavailable acceptance criterion at the dispatcher level: a coordinator
	/// whose pool connection cannot be opened denies every claim -- jobs stay queued,
	/// nothing executes, nothing is lost. (The job queue itself stays reachable here;
	/// only the pool's own connection is broken, the sharpest version of the failure.)
	/// </summary>
	[Fact]
	public async Task PoolDatabaseUnavailable_NoWorkStarts_NothingLost()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		int executionCount = 0;
		FakeJobHandler handler = new("download", (_, _) => { Interlocked.Increment(ref executionCount); return Task.FromResult(JobExecutionOutcome.Succeeded()); });

		// Unroutable per RFC 5737 (TEST-NET-1) with an aggressive timeout, so the
		// coordinator's claim path throws quickly instead of hanging the poll loop.
		CapacityLeasePoolRepository unreachablePool = new(
			"Host=192.0.2.1;Username=waypoint;Password=unused;Database=waypoint;Timeout=1;Cancellation Timeout=200");

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 },
			CreateCoordinator(unreachablePool), handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(3));
			Assert.Equal(0, executionCount);
			// The claim/deny/release cycle is slow here (each deny waits out the pool
			// connection timeout), so the row alternates between the transient claimed
			// window (running) and released (queued). Poll for the released phase --
			// what must NEVER happen is execution, asserted above and re-asserted after.
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Queued);
			Assert.Equal(0, executionCount);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// Issue #631 regression: a transient DB fault in the in-flight heartbeat loop
	/// (here <see cref="IJobRunnerRepository.RenewLeaseAsync"/>) faults the heartbeat
	/// Task. Awaiting that faulted Task in the completion path's finally block must NOT
	/// discard the handler's successful outcome and skip the terminal state advance --
	/// the job must still reach its terminal state, not sit at <c>running</c> until
	/// lease-recovery reclaims and (non-idempotently) retries it.
	/// </summary>
	[Fact]
	public async Task Issue631_HeartbeatFault_DoesNotBlockTerminalStateAdvance()
	{
		await _pool.RegisterPoolCapacityAsync("test", 4.0, 4L * 1024 * 1024 * 1024, operatorSet: false, CancellationToken.None);

		Guid runId = await _repository.CreateRunAsync("tool-install", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("tool-install", 1, Payload: "{}")], "tester", CancellationToken.None);
		Guid jobId = jobIds[0];

		// The heartbeat's first lease-renewal throws a transient DB-shaped error while
		// the handler is still running -- reproducing a Postgres connection reset /
		// timeout on a heartbeat tick in the live stack.
		FaultOnFirstRenewRepository faultingRepository = new(_repository);

		// Handler outlasts a short heartbeat interval so the faulting renewal fires
		// DURING execution, then returns success.
		FakeJobHandler handler = new("tool-install", async (_, ct) =>
		{
			await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
			return JobExecutionOutcome.Succeeded("ok");
		});

		JobDispatcherHostedService dispatcher = new(
			faultingRepository, _repository, _events, new JobHandlerRegistry([handler]),
			Options.Create(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), HeartbeatInterval = TimeSpan.FromMilliseconds(120) }),
			resourceAdmission: null, CreateCoordinator(_pool), NullLogger<JobDispatcherHostedService>.Instance);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Done);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.True(faultingRepository.RenewFaultFired, "the injected heartbeat fault never fired -- test did not exercise the bug");
		Assert.Equal(JobStates.Done, await GetJobStateAsync(jobId));
		Assert.Equal(0, await CountLeasesAsync());
	}

	/// <summary>Delegates every call to the real repository except the first <see cref="RenewLeaseAsync"/>, which throws a transient DB-shaped exception.</summary>
	private sealed class FaultOnFirstRenewRepository(IJobRunnerRepository inner) : IJobRunnerRepository
	{
		private int _renewCalls;

		public bool RenewFaultFired { get; private set; }

		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			if (Interlocked.Increment(ref _renewCalls) == 1)
			{
				RenewFaultFired = true;
				throw new InvalidOperationException("Injected transient heartbeat DB fault (issue #631 repro).");
			}

			return inner.RenewLeaseAsync(jobId, workerId, leaseDuration, cancellationToken);
		}

		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken) =>
			inner.ClaimJobAsync(workerId, leaseDuration, allowedJobTypes, cancellationToken);

		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => inner.IsCancelRequestedAsync(jobId, cancellationToken);

		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken) =>
			inner.AdvanceStateAsync(jobId, workerId, expectedFromState, toState, note, clearLease, cancellationToken);

		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken) =>
			inner.RequeueAtStageAsync(jobId, workerId, expectedFromState, stage, note, cancellationToken);

		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => inner.RecoverExpiredLeasesAsync(batchSize, cancellationToken);

		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) => inner.GetRunQueueStateAsync(runId, cancellationToken);

		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => inner.ReleaseClaimAsync(jobId, workerId, cancellationToken);

		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) =>
			inner.CheckConsecutiveAuthFailuresAsync(credentialId, threshold, cancellationToken);

		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken) => inner.SetUploadStatusAsync(jobId, uploadStatus, detail, cancellationToken);

		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => inner.GetJobCredentialBindingsAsync(jobId, cancellationToken);
	}

	private Task<Guid> SeedQueuedJobAsync(string jobType) => SeedJobAsync(jobType, "queued");

	private async Task<Guid> SeedJobAsync(string jobType, string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		// jobs_running_requires_lease_check (migration 0015): a running row must carry
		// a lease, so the running holder is seeded as another worker's live claim.
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (job_type, priority, state, claimed_by, claimed_at, lease_expires_at, heartbeat_at)
			VALUES ($1, 1, $2,
				CASE WHEN $2 = 'queued' THEN NULL ELSE 'other-runner' END,
				CASE WHEN $2 = 'queued' THEN NULL ELSE now() END,
				CASE WHEN $2 = 'queued' THEN NULL ELSE now() + interval '5 minutes' END,
				CASE WHEN $2 = 'queued' THEN NULL ELSE now() END)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(jobType);
		insert.Parameters.AddWithValue(state);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobStateAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<int> CountLeasesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*)::int FROM capacity_leases", connection);
		return (int)(await command.ExecuteScalarAsync())!;
	}

	private async Task ExecuteAsync(string sql)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(sql, connection);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task PollUntilAsync<T>(Func<Task<T>> probe, Func<T, bool> isDone)
	{
		using CancellationTokenSource timeout = new(PollTimeout);
		while (true)
		{
			T value = await probe();
			if (isDone(value))
			{
				return;
			}

			timeout.Token.ThrowIfCancellationRequested();
			await Task.Delay(TimeSpan.FromMilliseconds(50));
		}
	}
}
