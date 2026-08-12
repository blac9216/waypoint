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
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Resources;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #437's dispatcher-integration acceptance criteria against a real PostgreSQL 16
/// container: a job whose resource profile does not fit the runner's admission budget
/// is claimed then immediately released back to <c>queued</c> rather than executed
/// over-budget or lost (see <c>JobDispatcherHostedService</c>'s doc comment for why
/// "admit before claiming" becomes "admit before executing" given <c>ClaimJobAsync</c>'s
/// atomic <c>SKIP LOCKED</c> statement), and two independent dispatcher instances
/// sharing one queue -- the replica topology ADR-0014 §5 describes -- each enforce their
/// own resource budget without the claim SQL's <c>SKIP LOCKED</c> safety changing at all.
/// </summary>
[Collection("Postgres")]
public sealed class JobDispatcherResourceAdmissionTests : IAsyncLifetime
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;

	public JobDispatcherResourceAdmissionTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private static ResourceAdmissionController CreateController(double cpuCores, long memoryBytes)
	{
		string emptyCgroupRoot = Directory.CreateTempSubdirectory("waypoint-dispatcher-admission-test-").FullName;
		RunnerResourceOptions options = new()
		{
			CgroupRoot = emptyCgroupRoot,
			FallbackCpuCores = cpuCores,
			FallbackMemoryBytes = memoryBytes,
		};
		CgroupResourceDiscovery discovery = new(Options.Create(options), NullLogger<CgroupResourceDiscovery>.Instance);
		return new ResourceAdmissionController(Options.Create(options), discovery, NullLogger<ResourceAdmissionController>.Instance);
	}

	private JobDispatcherHostedService CreateDispatcher(JobEngineOptions options, ResourceAdmissionController? resourceAdmission, params IJobHandler[] handlers) =>
		new(_repository, _repository, _events, new JobHandlerRegistry(handlers), Options.Create(options), resourceAdmission, NullLogger<JobDispatcherHostedService>.Instance);

	/// <summary>
	/// A budget too small for even one "scan" (2.0 cores per <c>JobResourceProfiles</c>)
	/// must never let that job run: it is claimed, denied, released back to queued, and
	/// stays there for as long as the resource-starved runner keeps polling.
	/// </summary>
	[Fact]
	public async Task JobExceedingResourceBudget_IsClaimedThenReleasedRatherThanExecuted()
	{
		Guid jobId = await SeedQueuedJobAsync("scan");
		int executionCount = 0;
		FakeJobHandler handler = new("scan", (_, _) => { Interlocked.Increment(ref executionCount); return Task.FromResult(JobExecutionOutcome.Succeeded()); });

		ResourceAdmissionController controller = CreateController(cpuCores: 0.1, memoryBytes: 1024 * 1024); // Far below "scan"'s profile.
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 }, controller, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			// Give the dispatcher several poll cycles to repeatedly claim-and-release
			// the job; it must never transition out of queued because it never fits.
			await Task.Delay(TimeSpan.FromMilliseconds(500));
			Assert.Equal(JobStates.Queued, await GetJobStateAsync(jobId));
			Assert.Equal(0, executionCount);
			Assert.Equal(0, controller.AdmittedJobCount);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>A job that fits the budget executes normally with resource admission wired in -- the happy path is not broken by adding the check.</summary>
	[Fact]
	public async Task JobWithinResourceBudget_ExecutesNormally()
	{
		Guid jobId = await SeedQueuedJobAsync("discover");
		FakeJobHandler handler = new("discover", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));

		ResourceAdmissionController controller = CreateController(cpuCores: 4.0, memoryBytes: 4L * 1024 * 1024 * 1024);
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }, controller, handler);

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
		Assert.Equal(0, controller.AdmittedJobCount); // Released after completion.
	}

	/// <summary>
	/// A null <see cref="ResourceAdmissionController"/> (the Combined-host wiring)
	/// preserves today's MaxConcurrency-only behavior exactly -- no resource gate at
	/// all, matching this dispatcher's proven pre-#437 tests.
	/// </summary>
	[Fact]
	public async Task NullResourceAdmission_SkipsTheResourceGateEntirely()
	{
		// "download" (JobShape.Simple) rather than "scan" (JobShape.Standard, whose
		// terminal state is "uploaded" via an attesting/converting pipeline this test
		// has no interest in) -- see JobShapes.ForJob.
		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }, resourceAdmission: null, handler);

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
	}

	/// <summary>
	/// Issue #437 AC: "replicas remain queue-safe and do not assume extra host
	/// resources." Two independent dispatcher instances -- each with its own
	/// process-local <see cref="ResourceAdmissionController"/>, exactly like two
	/// replica containers would have -- share one Postgres queue with three "download"
	/// jobs (<c>JobShape.Simple</c>, so a plain <c>running -&gt; done</c> terminal keeps
	/// this test's concurrency-observation focus clear of the "scan" shape's separate
	/// attesting/converting/uploaded pipeline). Each replica's budget admits at most one
	/// download at a time, so at no point does either replica run more than one job even
	/// though both are polling the same queue concurrently; SKIP LOCKED still guarantees
	/// no job is double-claimed, and admission denial on one replica's claim lets the
	/// other replica's later poll pick the released job back up.
	/// </summary>
	[Fact]
	public async Task TwoReplicaDispatchers_EachEnforceOwnBudgetWithoutDoubleClaimingOrAssumingExtraCapacity()
	{
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

		// Each replica's budget admits exactly one "download" (0.5 cores per
		// JobResourceProfiles) at a time -- mirroring one container each got the same
		// modest cgroup allocation.
		ResourceAdmissionController controllerA = CreateController(cpuCores: 0.5, memoryBytes: 512L * 1024 * 1024);
		ResourceAdmissionController controllerB = CreateController(cpuCores: 0.5, memoryBytes: 512L * 1024 * 1024);

		JobDispatcherHostedService dispatcherA = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(30), MaxConcurrency = 4 }, controllerA, MakeHandler());
		JobDispatcherHostedService dispatcherB = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(30), MaxConcurrency = 4 }, controllerB, MakeHandler());

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

		foreach (Guid jobId in jobIds)
		{
			Assert.Equal(JobStates.Done, await GetJobStateAsync(jobId));
		}

		// The queue's SKIP LOCKED claim never let both replicas run the same job (each
		// terminal 'done' implies exactly one execution), and the combined system-wide
		// concurrency never exceeded 2 -- one job per replica's own 0.5-core budget --
		// even though up to 4 slots (MaxConcurrency) were available on each.
		Assert.True(maxObservedConcurrentExecutions <= 2, $"Observed {maxObservedConcurrentExecutions} concurrent executions across both replicas; each replica's own resource budget should cap this at 1, so combined should never exceed 2.");
	}

	private async Task<Guid> SeedQueuedJobAsync(string jobType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ($1, 1, 'queued') RETURNING id", connection);
		insert.Parameters.AddWithValue(jobType);
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
