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
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// End-to-end proof that <see cref="JobDispatcherHostedService"/> actually wires the
/// pieces <see cref="JobQueueRepositoryClaimTests"/>, <see cref="JobLeaseRecoveryTests"/>,
/// and <see cref="RunFanOutPauseAbortTests"/> prove individually into one working loop,
/// against a real PostgreSQL 16 container: claim -&gt; execute -&gt; complete, the
/// no-handler and handler-exception failure paths, heartbeat keeping a longer job's
/// lease alive, a paused run's job never executing, and abort cooperatively cancelling
/// in-flight work.
/// </summary>
[Collection("Postgres")]
public sealed class JobDispatcherHostedServiceTests : IAsyncLifetime
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;
	private CapturingLogger<JobDispatcherHostedService> _dispatcherLogger = null!;

	public JobDispatcherHostedServiceTests(PostgresFixture fixture)
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
		_dispatcherLogger = new CapturingLogger<JobDispatcherHostedService>();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>#147: the third consecutive auth failure trips the halt with NOTHING
	/// queued behind it -- the dispatcher must still emit the system.notice, because
	/// the durable credential state changed even though no rows were blocked.</summary>
	[Fact]
	public async Task AHaltWithNothingQueued_StillEmitsASystemNotice()
	{
		Guid credentialId = await SeedCredentialAsync();
		await SeedTerminalAuthFailedAsync(credentialId);
		await SeedTerminalAuthFailedAsync(credentialId);
		Guid jobId = await SeedQueuedJobAsync("download", credentialId);
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.AuthFailed("credential rejected (invented)")));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }, handler);
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "auth-failed");
			await PollUntilAsync(CountHaltNoticesAsync, count => count >= 1);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.True(await CountHaltNoticesAsync() >= 1);
	}

	private async Task<long> CountHaltNoticesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new(
			"SELECT count(*) FROM job_events WHERE event_type = 'system.notice' AND payload->>'blocked' = 'true'", connection);
		return (long)(await count.ExecuteScalarAsync())!;
	}

	private async Task SeedTerminalAuthFailedAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, credential_id, finished_at) VALUES ('download', 1, 'auth-failed', $1, now())", connection);
		insert.Parameters.AddWithValue(credentialId);
		await insert.ExecuteNonQueryAsync();
		await Task.Delay(TimeSpan.FromMilliseconds(5));
	}

	private JobDispatcherHostedService CreateDispatcher(JobEngineOptions? options, params IJobHandler[] handlers)
	{
		JobEngineOptions effective = options ?? new JobEngineOptions { Enabled = true };
		return new JobDispatcherHostedService(
			_repository, _repository, _events, new JobHandlerRegistry(handlers), Options.Create(effective), _dispatcherLogger);
	}

	[Fact]
	public async Task SuccessfulJob_IsClaimedExecutedAndCompleted()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded("ok")));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "done");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("done", await GetJobStateAsync(jobId));
	}

	[Fact]
	public async Task NoHandlerRegistered_JobFailsWithAClearNote()
	{
		Guid jobId = await SeedQueuedJobAsync("catalog-index");

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) });

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "failed");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		string? note = await GetJobNoteAsync(jobId);
		Assert.Contains("No handler registered", note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HandlerThrows_JobFailsRatherThanCrashingTheDispatcher()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => throw new InvalidOperationException("boom"));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "failed");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		string? note = await GetJobNoteAsync(jobId);
		Assert.Contains("boom", note, StringComparison.Ordinal);
	}

	/// <summary>A job that outlives its lease is kept alive by the heartbeat loop rather than being clawed back by recovery mid-execution.</summary>
	[Fact]
	public async Task LongRunningJob_HeartbeatKeepsItAlive_CompletesNormally()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			await release.Task.WaitAsync(ct);
			return JobExecutionOutcome.Succeeded();
		});

		JobEngineOptions options = new()
		{
			Enabled = true,
			PollInterval = TimeSpan.FromMilliseconds(50),
			LeaseDuration = TimeSpan.FromSeconds(1),
			HeartbeatInterval = TimeSpan.FromMilliseconds(200)
		};
		JobDispatcherHostedService dispatcher = CreateDispatcher(options, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "running");

			// Outlive the 1s lease by a comfortable margin, relying entirely on the
			// 200ms heartbeat cadence to keep it claimed.
			await Task.Delay(TimeSpan.FromSeconds(3));
			Assert.Equal("running", await GetJobStateAsync(jobId));

			release.SetResult();
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "done");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// Issue #637: a transient fault on a heartbeat tick (DB blip, timeout) must not
	/// kill the loop for the rest of a long-running job. This injects faults on the
	/// first two ticks (below <see cref="JobDispatcherHostedService.MaxConsecutiveHeartbeatTickFailures"/>)
	/// then lets ticks succeed again, and proves both halves of the regression: (1)
	/// lease renewal kept the job alive well past its short lease duration despite the
	/// faults, and (2) after the faults stop, a per-job cancel request is still
	/// observed and honored on a later tick -- abort/cancel observation was not
	/// permanently disabled by the earlier faults.
	/// </summary>
	[Fact]
	public async Task HeartbeatTransientFault_LogsAndRetries_RenewalAndCancelObservationSurvive()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return JobExecutionOutcome.Succeeded();
		});

		FaultingFirstNTicksRepository faultingRepository = new(_repository, faultCount: 2);

		JobEngineOptions options = new()
		{
			Enabled = true,
			PollInterval = TimeSpan.FromMilliseconds(50),
			LeaseDuration = TimeSpan.FromSeconds(1),
			HeartbeatInterval = TimeSpan.FromMilliseconds(150)
		};
		JobDispatcherHostedService dispatcher = new(
			faultingRepository, _repository, _events, new JobHandlerRegistry([handler]),
			Options.Create(options), _dispatcherLogger);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(PollTimeout);

			// Outlive the 1s lease by a comfortable margin -- if the faulted ticks had
			// killed the loop (pre-#637 behavior), the lease would expire and
			// lease-recovery would reclaim the job.
			await Task.Delay(TimeSpan.FromSeconds(3));
			Assert.Equal(JobStates.Running, await GetJobStateAsync(jobId));
			Assert.True(faultingRepository.FaultsInjected >= 2, "the injected transient faults never fired -- test did not exercise the bug");

			// Cancellation is still observed on a later (post-recovery) tick.
			JobCancelOutcome outcome = await _repository.CancelJobAsync(jobId, CancellationToken.None);
			Assert.Equal(JobCancelOutcome.CancelRequested, outcome);

			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Cancelled);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(JobStates.Cancelled, await GetJobStateAsync(jobId));
	}

	/// <summary>
	/// Issue #637: proves the bounded-escalation half of the fix -- once consecutive
	/// tick faults reach <see cref="JobDispatcherHostedService.MaxConsecutiveHeartbeatTickFailures"/>,
	/// the loop gives up (persistent fault, not a blip) rather than retrying forever,
	/// so a genuinely dead database does not heartbeat into the void indefinitely.
	/// </summary>
	[Fact]
	public async Task HeartbeatPersistentFault_GivesUpAfterConsecutiveThreshold()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return JobExecutionOutcome.Succeeded();
		});

		FaultingFirstNTicksRepository faultingRepository = new(_repository, faultCount: int.MaxValue);

		JobEngineOptions options = new()
		{
			Enabled = true,
			PollInterval = TimeSpan.FromMilliseconds(50),
			LeaseDuration = TimeSpan.FromSeconds(60),
			HeartbeatInterval = TimeSpan.FromMilliseconds(100)
		};
		JobDispatcherHostedService dispatcher = new(
			faultingRepository, _repository, _events, new JobHandlerRegistry([handler]),
			Options.Create(options), _dispatcherLogger);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(PollTimeout);

			// Every tick faults; the loop must stop retrying once it hits the bound
			// rather than continuing to call RenewLeaseAsync forever.
			using CancellationTokenSource timeout = new(PollTimeout);
			while (Volatile.Read(ref faultingRepository.FaultsInjectedField) < JobDispatcherHostedService.MaxConsecutiveHeartbeatTickFailures)
			{
				timeout.Token.ThrowIfCancellationRequested();
				await Task.Delay(TimeSpan.FromMilliseconds(50));
			}

			int faultsAtBound = faultingRepository.FaultsInjected;

			// Give the loop time to have given up; the fault count must not keep
			// climbing past the bound.
			await Task.Delay(TimeSpan.FromMilliseconds(500));
			Assert.Equal(faultsAtBound, faultingRepository.FaultsInjected);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>Delegates every call to the real repository except <see cref="RenewLeaseAsync"/>, which throws a transient DB-shaped exception for its first <c>faultCount</c> calls.</summary>
	private sealed class FaultingFirstNTicksRepository(IJobRunnerRepository inner, int faultCount) : IJobRunnerRepository
	{
		private int _renewCalls;

		public int FaultsInjectedField;

		public int FaultsInjected => Volatile.Read(ref FaultsInjectedField);

		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			int call = Interlocked.Increment(ref _renewCalls);
			if (call <= faultCount)
			{
				Interlocked.Increment(ref FaultsInjectedField);
				throw new InvalidOperationException("Injected transient heartbeat DB fault (issue #637 repro).");
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

		public Task RecordUploadAttemptAsync(Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken) =>
			inner.RecordUploadAttemptAsync(jobId, endpoint, collection, uploadStatus, detail, cancellationToken);

		public Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken) => inner.GetUploadAttemptsAsync(jobId, cancellationToken);

		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => inner.GetJobCredentialBindingsAsync(jobId, cancellationToken);
		public Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) => inner.FanOutAdditionalJobsAsync(runId, specs, createdBy, cancellationToken);
	}

	[Fact]
	public async Task PausedRun_ReleasesClaimsUntilResume_ThenExecutes()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		int calls = 0;
		FakeJobHandler handler = new("download", (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(JobExecutionOutcome.Succeeded()); });
		JobDispatcherHostedService dispatcher = CreateDispatcher(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25) }, handler);

		// Exercise both PauseRunAsync and ResumeRunAsync wrappers on the hosted service
		Assert.True(await dispatcher.PauseRunAsync(runId, CancellationToken.None));

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			// Deterministic in place of a fixed wall-clock delay (issue #428): the dispatcher
			// claims, checks pause, and releases a paused run's job on every poll -- so raw
			// DB state transiently reads "running" between claim and release on essentially
			// every cycle, and sampling it (even repeatedly) races that window under load.
			// What must hold is that the handler is never actually invoked; wait out several
			// of the dispatcher's own release-retry cycles and assert that directly.
			await AssertNeverExecutesWhilePausedAsync(() => Volatile.Read(ref calls), settleCycles: 5, JobDispatcherHostedService.PausedReleaseRetryDelay);
			Assert.Equal(0, Volatile.Read(ref calls));
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Queued);
			Assert.True(await dispatcher.ResumeRunAsync(runId, CancellationToken.None));
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Done);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task AbortRun_CancelsLocallyRunningHandlerAndJob()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return JobExecutionOutcome.Succeeded();
		});
		JobDispatcherHostedService dispatcher = CreateDispatcher(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25) }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(PollTimeout);
			await dispatcher.AbortRunAsync(runId, CancellationToken.None);
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Cancelled);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// Issue #234: a running job's per-job cancel_requested flag (set by
	/// <see cref="IJobControlRepository.CancelJobAsync"/>, the DELETE /downloads/{id} path)
	/// is observed by the heartbeat loop -- same tick as the pre-existing run-abort
	/// check -- and stops the handler promptly, releases the lease, and lands the job in
	/// 'cancelled' with a note that names cancel-by-request rather than run-abort. The
	/// run itself is untouched (mirrors AbortRun_CancelsLocallyRunningHandlerAndJob, but
	/// exercises the per-job signal instead of the run-scoped one).
	/// </summary>
	[Fact]
	public async Task CancelRequested_ObservedMidRun_StopsPromptlyAndReleasesLease()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return JobExecutionOutcome.Succeeded();
		});
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25), HeartbeatInterval = TimeSpan.FromMilliseconds(50) },
			handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(PollTimeout);

			// The DELETE /downloads/{id} path: request cancel of the running job directly,
			// without touching the run.
			JobCancelOutcome outcome = await _repository.CancelJobAsync(jobId, CancellationToken.None);
			Assert.Equal(JobCancelOutcome.CancelRequested, outcome);

			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Cancelled);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		string? note = await GetJobNoteAsync(jobId);
		Assert.Contains("Cancelled by request", note, StringComparison.Ordinal);

		// Result discarded, lease released, run untouched (per-job cancel never aborts the run).
		Assert.Null(await GetJobLeaseExpiresAtAsync(jobId));
		Assert.NotEqual("aborted", await GetRunStateAsync(runId));
	}

	/// <summary>Run-scoped abort must keep working unchanged now that a second, per-job cancel signal shares the same heartbeat tick.</summary>
	[Fact]
	public async Task RunScopedAbort_StillCancelsInFlightWork_AlongsidePerJobSignal()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return JobExecutionOutcome.Succeeded();
		});
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25), HeartbeatInterval = TimeSpan.FromMilliseconds(50) },
			handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(PollTimeout);
			await dispatcher.AbortRunAsync(runId, CancellationToken.None);
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Cancelled);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		string? note = await GetJobNoteAsync(jobId);
		Assert.Contains("run aborted", note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ThirdAuthFailure_BlocksRemainingQueueAndEmitsOperatorEvents()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("download", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalAuthFailureAsync(runId, credentialId);
		await SeedTerminalAuthFailureAsync(runId, credentialId);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec("download", 1, CredentialId: credentialId), new JobSpec("download", 2, CredentialId: credentialId)],
			"tester",
			CancellationToken.None);
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.AuthFailed("rejected")));
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25), MaxConcurrency = 1, ConsecutiveAuthFailureThreshold = 3 }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetRunBlockedAsync(runId), blocked => blocked);

			// The queue-blocked flag, the second job's own state transition, the
			// operator log entry, and the two emitted events all land on the same
			// background dispatch tick but are independently observable writes --
			// runs.blocked flipping true does not guarantee the rest have landed
			// yet. Poll each rather than asserting immediately, or this races the
			// loop under CI load (#341).
			await PollUntilAsync(() => GetJobStateAsync(jobIds[1]), state => state == JobStates.Blocked);
			await PollUntilAsync(
				() => Task.FromResult(_dispatcherLogger.Entries.Any(entry => entry.Message.Contains("queue halted", StringComparison.Ordinal))),
				found => found);
			await PollUntilAsync(() => EventTypeExistsAsync(JobEventTypes.QueueState, runId), exists => exists);
			await PollUntilAsync(() => EventTypeExistsAsync(JobEventTypes.SystemNotice, null), exists => exists);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(JobStates.AuthFailed, await GetJobStateAsync(jobIds[0]));
		Assert.Equal(JobStates.Blocked, await GetJobStateAsync(jobIds[1]));
		Assert.Contains(_dispatcherLogger.Entries, entry => entry.Message.Contains("queue halted", StringComparison.Ordinal));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.QueueState, runId));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.SystemNotice, null));
	}

	[Fact]
	public async Task FirstAuthFailure_DoesNotHaltOrEmitQueueEvents()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("download", "{}", credentialId, null, CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1, CredentialId: credentialId)], null, CancellationToken.None));
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.AuthFailed()));
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25), ConsecutiveAuthFailureThreshold = 3 }, handler);
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.AuthFailed);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
		Assert.False(await GetRunBlockedAsync(runId));
		Assert.False(await EventTypeExistsAsync(JobEventTypes.QueueState, runId));
		Assert.DoesNotContain(_dispatcherLogger.Entries, entry => entry.Message.Contains("queue halted", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AbortWithNoKnownOrLocallyOwnedWork_IsSafe()
	{
		JobDispatcherHostedService dispatcher = CreateDispatcher(new JobEngineOptions { Enabled = true });
		await dispatcher.AbortRunAsync(Guid.NewGuid(), CancellationToken.None);

		Guid runId = await _repository.CreateRunAsync("download", "{}", null, null, CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], null, CancellationToken.None));
		ClaimedJob claimed = Assert.IsType<ClaimedJob>(await _repository.ClaimJobAsync("other-worker", TimeSpan.FromMinutes(1), JobCapabilities.All, CancellationToken.None));
		Assert.Equal(jobId, claimed.Id);
		await dispatcher.AbortRunAsync(runId, CancellationToken.None);
		Assert.Equal(JobStates.Running, await GetJobStateAsync(jobId));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.RunProgress, runId));
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
		command.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task SeedTerminalAuthFailureAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id, finished_at) VALUES ($1, 'download', 1, 'auth-failed', $2, now())", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(credentialId);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> EventTypeExistsAsync(string eventType, Guid? runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT EXISTS (SELECT 1 FROM job_events WHERE event_type = $1 AND run_id IS NOT DISTINCT FROM $2)", connection);
		command.Parameters.AddWithValue(eventType);
		command.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	private Task<Guid> SeedQueuedJobAsync(string jobType) => SeedQueuedJobAsync(jobType, credentialId: null);

	private async Task<Guid> SeedQueuedJobAsync(string jobType, Guid? credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, credential_id) VALUES ($1, 1, 'queued', $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(jobType);
		insert.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
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

	private async Task<string?> GetJobNoteAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT note FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (string?)await command.ExecuteScalarAsync();
	}

	private async Task<bool> GetRunBlockedAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT blocked FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	private async Task<string> GetRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<DateTime?> GetJobLeaseExpiresAtAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT lease_expires_at FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		object? result = await command.ExecuteScalarAsync();
		return result as DateTime?;
	}

	/// <summary>
	/// Issue #428: a paused run's job is genuinely claimed, checked, and released back to
	/// <see cref="JobStates.Queued"/> on every dispatcher poll while paused (see
	/// <c>JobDispatcherHostedService</c>'s claim-then-release-if-paused path) -- so the DB
	/// row transiently reads <see cref="JobStates.Running"/> for the moment between claim
	/// and release, on essentially every cycle. Sampling raw state on a fixed cadence (the
	/// original 250ms single sample, or naive repeated sampling) races that transient
	/// window and is exactly what made this test flaky under load. What must actually never
	/// happen while paused is the handler executing -- <paramref name="callCount"/> stays
	/// zero for the whole window regardless of how many claim/release cycles occur -- so
	/// this waits out a deterministic number of the dispatcher's own release-retry cycles
	/// (<paramref name="settleCycles"/> x its known <c>PausedReleaseRetryDelay</c>) and
	/// asserts the handler was never invoked across that whole window, which is the
	/// property the test exists to prove.
	/// </summary>
	private static async Task AssertNeverExecutesWhilePausedAsync(Func<int> readCallCount, int settleCycles, TimeSpan releaseRetryDelay)
	{
		for (int i = 0; i < settleCycles; i++)
		{
			await Task.Delay(releaseRetryDelay);
			Assert.Equal(0, readCallCount());
		}
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
