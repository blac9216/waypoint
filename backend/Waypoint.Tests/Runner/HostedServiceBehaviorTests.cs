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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Runner;

public sealed class HostedServiceBehaviorTests
{
	[Fact]
	public async Task DisabledServices_LogAndDoNotPoll()
	{
		FakeRepository repository = new(); FakeEvents events = new();
		CapturingLogger<JobDispatcherHostedService> dispatcherLog = new();
		CapturingLogger<LeaseRecoveryHostedService> recoveryLog = new();
		IOptions<JobEngineOptions> options = Options.Create(new JobEngineOptions { Enabled = false });
		await new JobDispatcherHostedService(repository, repository, events, new JobHandlerRegistry([]), options, dispatcherLog).StartAsync(CancellationToken.None);
		await new LeaseRecoveryHostedService(repository, events, options, recoveryLog).StartAsync(CancellationToken.None);
		Assert.Contains("disabled", dispatcherLog.OnlyEntryAt(LogLevel.Information).Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("disabled", recoveryLog.OnlyEntryAt(LogLevel.Information).Message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(0, repository.Claims);
	}

	[Theory]
	[InlineData("running", true, false, true, false)]
	[InlineData("running", false, true, true, false)]
	[InlineData("aborted", false, false, false, true)]
	public async Task DispatcherHandlesEveryHaltedClaim(string state, bool paused, bool blocked, bool released, bool cancelled)
	{
		FakeRepository repository = new()
		{
			NextClaim = Job(Guid.NewGuid()),
			RunState = new RunQueueState(state, paused, blocked, "reason")
		};
		CapturingLogger<JobDispatcherHostedService> logger = new();
		JobDispatcherHostedService service = Dispatcher(repository, logger);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(repository.Progress, () => repository.Releases > 0 || repository.Moves.Count > 0);
		await service.StopAsync(CancellationToken.None);
		Assert.Equal(released, repository.Releases == 1);
		Assert.Equal(cancelled, repository.Moves.Any(move => move.To == JobStates.Cancelled));
		Assert.Contains("starting", logger.OnlyEntryAt(LogLevel.Information).Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ClaimFailure_IsLoggedThenPollingContinues()
	{
		FakeRepository repository = new() { ThrowClaims = 1 };
		CapturingLogger<JobDispatcherHostedService> logger = new();
		JobDispatcherHostedService service = Dispatcher(repository, logger);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(repository.Progress, () => repository.Claims > 1);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("Claim attempt failed", StringComparison.Ordinal));
	}

	[Fact]
	public async Task MissingHandlerAndIllegalSuccess_AreLoggedAndFailed()
	{
		FakeRepository missingRepository = new() { NextClaim = Job(null) };
		CapturingLogger<JobDispatcherHostedService> missingLog = new();
		JobDispatcherHostedService missing = Dispatcher(missingRepository, missingLog);
		await missing.StartAsync(CancellationToken.None);
		await WaitAsync(missingRepository.Progress, () => missingRepository.Moves.Count > 0);
		await missing.StopAsync(CancellationToken.None);
		Assert.Contains(missingLog.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("no handler", StringComparison.OrdinalIgnoreCase));

		FakeRepository illegalRepository = new() { NextClaim = Job(null, "scan") };
		CapturingLogger<JobDispatcherHostedService> illegalLog = new();
		FakeJobHandler scan = new("scan", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		JobDispatcherHostedService illegal = Dispatcher(illegalRepository, illegalLog, scan);
		await illegal.StartAsync(CancellationToken.None);
		await WaitAsync(illegalRepository.Progress, () => illegalRepository.Moves.Count > 0);
		await illegal.StopAsync(CancellationToken.None);
		Assert.Contains(illegalLog.EntriesAt(LogLevel.Warning), entry => entry.Message.Contains("illegal transition", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task CompletionLostAndHandlerException_AreLogged()
	{
		FakeRepository repository = new() { NextClaim = Job(null), AdvanceResult = false };
		CapturingLogger<JobDispatcherHostedService> logger = new();
		FakeJobHandler handler = new("download", (_, _) => throw new InvalidOperationException("boom"));
		JobDispatcherHostedService service = Dispatcher(repository, logger, handler);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(repository.Progress, () => repository.Moves.Count > 0);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("handler threw", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(logger.EntriesAt(LogLevel.Warning), entry => entry.Message.Contains("lost the race", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task HeartbeatObservesAbortAndCancelsHandler()
	{
		Guid runId = Guid.NewGuid();
		FakeRepository repository = new() { NextClaim = Job(runId), RunState = new RunQueueState("running", false, false, null) };
		CapturingLogger<JobDispatcherHostedService> logger = new();
		FakeJobHandler handler = new("download", async (_, token) =>
		{
			repository.RunState = new RunQueueState("aborted", false, false, null);
			await Task.Delay(TimeSpan.FromSeconds(2), token);
			return JobExecutionOutcome.Succeeded();
		});
		JobDispatcherHostedService service = Dispatcher(repository, logger, handler);
		await service.StartAsync(CancellationToken.None);
		// Issue #1118: wait for the job to reach ANY terminal state, not specifically the
		// cancelled one. Both sides of the behaviour under test produce that event -- a
		// working heartbeat aborts the handler and writes Cancelled, a broken one lets the
		// handler run to completion and writes its success state -- so this wait ends on
		// the code under test's own signal either way, and the assertion below decides
		// which happened. A regression fails here by assertion, not by a timeout.
		await WaitAsync(repository.Progress, () => repository.Moves.Count > 0);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(repository.Moves, move => move.To == JobStates.Cancelled);
		Assert.DoesNotContain(repository.Moves, move => move.To is JobStates.Done or JobStates.Uploaded);
		Assert.Contains(logger.EntriesAt(LogLevel.Warning), entry => entry.Message.Contains("observed run", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task HeartbeatOwnershipLoss_IsLoggedWhileHandlerFinishes()
	{
		FakeRepository repository = new() { NextClaim = Job(null), RenewResult = false };
		CapturingLogger<JobDispatcherHostedService> logger = new();
		FakeJobHandler handler = new("download", async (_, _) => { await Task.Delay(80, CancellationToken.None); return JobExecutionOutcome.Succeeded(); });
		JobDispatcherHostedService service = Dispatcher(repository, logger, handler);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(repository.Progress, () => repository.Moves.Count > 0);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(logger.EntriesAt(LogLevel.Warning), entry => entry.Message.Contains("ownership lost", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Issue #631: a heartbeat loop that faults (transient DB error on a per-tick call)
	/// must NOT prevent the successful handler's terminal state write. Before the fix the
	/// faulted heartbeat Task, awaited in the completion finally, rethrew past
	/// AdvanceStateAsync -- so no running-&gt;done Move was ever recorded and the job hung
	/// at running until lease-recovery reclaimed it. Issue #637 strengthened this
	/// further: a single transient tick fault no longer faults the heartbeat Task at
	/// all -- it is logged per-tick ("heartbeat tick failed") and retried on the next
	/// tick, so renewal and abort/cancel observation continue. The terminal-write
	/// guarantee this test pins is unchanged; only the expected log line moved.
	/// </summary>
	[Fact]
	public async Task HeartbeatFault_DoesNotSkipTerminalStateWrite()
	{
		FakeRepository repository = new() { NextClaim = Job(null), ThrowRenews = 1 };
		CapturingLogger<JobDispatcherHostedService> logger = new();
		FakeJobHandler handler = new("download", async (_, _) =>
		{
			// Outlast the 10ms heartbeat interval so the faulting renewal fires mid-run.
			await Task.Delay(60, CancellationToken.None);
			return JobExecutionOutcome.Succeeded();
		});
		JobDispatcherHostedService service = Dispatcher(repository, logger, handler);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(repository.Progress, () => repository.Moves.Any(move => move.To == JobStates.Done));
		await service.StopAsync(CancellationToken.None);

		Assert.Contains(repository.Moves, move => move is { From: JobStates.Running, To: JobStates.Done });
		Assert.Contains(logger.EntriesAt(LogLevel.Warning), entry => entry.Message.Contains("heartbeat tick failed", StringComparison.OrdinalIgnoreCase));
	}

	[Theory]
	[InlineData(JobOutcomeKind.Failed, JobStates.Failed)]
	[InlineData(JobOutcomeKind.AuthFailed, JobStates.AuthFailed)]
	public async Task DispatcherMapsNonSuccessOutcomes(JobOutcomeKind kind, string expectedState)
	{
		FakeRepository repository = new() { NextClaim = Job(null) };
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(new JobExecutionOutcome(kind)));
		JobDispatcherHostedService service = Dispatcher(repository, new CapturingLogger<JobDispatcherHostedService>(), handler);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(repository.Progress, () => repository.Moves.Count > 0);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(repository.Moves, move => move.To == expectedState);
	}

	[Fact]
	public async Task StopCancelsBlockedClaimAndRecoveryLoops()
	{
		FakeRepository claimRepository = new() { BlockClaims = true };
		JobDispatcherHostedService dispatcher = Dispatcher(claimRepository, new CapturingLogger<JobDispatcherHostedService>());
		await dispatcher.StartAsync(CancellationToken.None);
		await WaitAsync(claimRepository.Progress, () => claimRepository.Claims > 0);
		await dispatcher.StopAsync(CancellationToken.None);

		FakeRepository recoveryRepository = new() { BlockRecoveries = true };
		LeaseRecoveryHostedService recovery = new(recoveryRepository, new FakeEvents(),
			Options.Create(new JobEngineOptions { RecoveryInterval = TimeSpan.FromMilliseconds(10) }), new CapturingLogger<LeaseRecoveryHostedService>());
		await recovery.StartAsync(CancellationToken.None);
		await WaitAsync(recoveryRepository.Progress, () => recoveryRepository.Recoveries > 0);
		await recovery.StopAsync(CancellationToken.None);
	}

	[Fact]
	public async Task ShutdownWaitsForInFlightHandler()
	{
		FakeRepository repository = new() { NextClaim = Job(null) };
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, _) =>
		{
			started.SetResult(); await Task.Delay(100, CancellationToken.None); return JobExecutionOutcome.Succeeded();
		});
		CapturingLogger<JobDispatcherHostedService> logger = new();
		JobDispatcherHostedService service = Dispatcher(repository, logger, handler);
		await service.StartAsync(CancellationToken.None);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(logger.EntriesAt(LogLevel.Information), entry => entry.Message.Contains("in-flight", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task RecoveryLoop_LogsFailureThenRecoversAndEmits()
	{
		FakeRepository repository = new()
		{
			ThrowRecoveries = 1,
			NextRecovery = [new RecoveredJob(Guid.NewGuid(), Guid.NewGuid(), JobStates.Queued, 1, 3)]
		};
		FakeEvents events = new(); CapturingLogger<LeaseRecoveryHostedService> logger = new();
		LeaseRecoveryHostedService service = new(repository, events,
			Options.Create(new JobEngineOptions { RecoveryInterval = TimeSpan.FromMilliseconds(10), RecoveryBatchSize = 5 }), logger);
		await service.StartAsync(CancellationToken.None);
		await WaitAsync(events.Progress, () => events.Count > 0);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("sweep failed", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(logger.EntriesAt(LogLevel.Information), entry => entry.Message.Contains("sweeping", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void OutcomeFactories_CoverEveryKind()
	{
		Assert.Equal(JobOutcomeKind.Succeeded, JobExecutionOutcome.Succeeded().Kind);
		Assert.Equal(JobOutcomeKind.Failed, JobExecutionOutcome.Failed().Kind);
		Assert.Equal(JobOutcomeKind.AuthFailed, JobExecutionOutcome.AuthFailed().Kind);
	}

	private static JobDispatcherHostedService Dispatcher(FakeRepository repository, CapturingLogger<JobDispatcherHostedService> logger, params IJobHandler[] handlers) =>
		new(repository, repository, new FakeEvents(), new JobHandlerRegistry(handlers), Options.Create(new JobEngineOptions
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			LeaseDuration = TimeSpan.FromSeconds(1),
			HeartbeatInterval = TimeSpan.FromMilliseconds(10)
		}), logger);

	private static ClaimedJob Job(Guid? runId, string jobType = "download") => new(Guid.NewGuid(), runId, jobType, null, null, null, 1, "{}", 1, 3);

	/// <summary>
	/// Issue #1118: this helper used to poll <paramref name="condition"/> every 10ms
	/// under a 3s wall-clock ceiling, so a loaded full-suite run could starve the poll
	/// past the ceiling before the condition was ever observed -- surfacing as an
	/// uncaught TaskCanceledException rather than a real assertion failure. There is no
	/// wall-clock window here any more, in either direction: every fake in this file
	/// releases <see cref="ProgressSignal"/> on each observable state change it makes,
	/// and this helper re-evaluates the condition only when one of those signals
	/// arrives. A passing run therefore completes on the event itself, at whatever
	/// moment the code under test produces it, and no amount of host load can move it
	/// past a deadline -- there is no deadline. The wait subscribes to the next signal
	/// BEFORE it evaluates the condition, so a signal raised concurrently with that
	/// evaluation is never lost.
	/// <para>
	/// Trade-off, stated deliberately: with no ceiling, a genuine regression that stops
	/// the service making progress hangs this wait instead of failing it after 3s. That
	/// is the standard cost of the deterministic <c>await signal</c> idiom (the same one
	/// <c>JobStageDispatcherTests</c>' handler gates use) and it is why the one test
	/// #1118 was filed against -- <see cref="HeartbeatObservesAbortAndCancelsHandler"/>
	/// -- waits on an event that occurs on BOTH sides of the behaviour it pins (any
	/// terminal move) and asserts on which one arrived: a broken abort observation there
	/// fails by assertion, promptly, not by hanging.
	/// </para>
	/// </summary>
	private static async Task WaitAsync(ProgressSignal progress, Func<bool> condition)
	{
		while (true)
		{
			// Subscribe first, evaluate second: a signal raised between the two completes
			// the already-captured task rather than being dropped on the floor.
			Task nextSignal = progress.NextAsync();
			if (condition())
			{
				return;
			}

			await nextSignal;
		}
	}

	/// <summary>
	/// A counting "something observable happened" signal (issue #1118). Each fake in
	/// this file raises it after every state change a test can assert on, so waits are
	/// driven by the code under test rather than by elapsed time.
	/// </summary>
	private sealed class ProgressSignal
	{
		private readonly object _lock = new();
		private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Completes every task handed out by <see cref="NextAsync"/> so far.</summary>
		public void Signal()
		{
			TaskCompletionSource current;
			lock (_lock)
			{
				current = _next;
				_next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			current.TrySetResult();
		}

		/// <summary>A task that completes on the next <see cref="Signal"/> after this call.</summary>
		public Task NextAsync()
		{
			lock (_lock)
			{
				return _next.Task;
			}
		}
	}

	private sealed class FakeEvents : IJobEventPublisher
	{
		public int Count { get; private set; }
		public ProgressSignal Progress { get; } = new();
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{ Count++; Progress.Signal(); return Task.CompletedTask; }
	}

	private sealed class FakeRepository : IJobControlRepository, IJobRunnerRepository
	{
		public ClaimedJob? NextClaim { get; set; }
		public RunQueueState? RunState { get; set; }
		public IReadOnlyList<RecoveredJob> NextRecovery { get; set; } = [];
		public int ThrowClaims { get; set; }
		public int ThrowRecoveries { get; set; }
		public bool BlockClaims { get; set; }
		public bool BlockRecoveries { get; set; }
		public int Recoveries { get; private set; }
		public int Claims { get; private set; }
		public int Releases { get; private set; }
		public bool RenewResult { get; set; } = true;
		public bool AdvanceResult { get; set; } = true;
		public List<(string From, string To)> Moves { get; } = [];
		public ProgressSignal Progress { get; } = new();
		public async Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken)
		{
			_ = allowedJobTypes;
			Claims++;
			Progress.Signal();
			if (BlockClaims)
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}

			if (ThrowClaims-- > 0)
			{
				throw new InvalidOperationException("claim failed");
			}

			ClaimedJob? value = NextClaim; NextClaim = null; return value;
		}
		public int ThrowRenews { get; set; }
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			if (ThrowRenews-- > 0)
			{
				throw new InvalidOperationException("renew failed");
			}

			return Task.FromResult(RenewResult);
		}
		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken)
		{ Moves.Add((expectedFromState, toState)); Progress.Signal(); return Task.FromResult(AdvanceResult); }
		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken)
		{ Moves.Add((expectedFromState, JobStates.Queued)); Progress.Signal(); return Task.FromResult(AdvanceResult); }
		public async Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
		{
			Recoveries++;
			Progress.Signal();
			if (BlockRecoveries)
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}

			if (ThrowRecoveries-- > 0)
			{
				throw new InvalidOperationException("recovery failed");
			}

			IReadOnlyList<RecoveredJob> value = NextRecovery; NextRecovery = []; return value;
		}
		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(RunState);
		public Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<RunSummary?>(null);
		public Task<RunListResult> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken) => Task.FromResult(new RunListResult([], 0));
		public Task<RunHistoryPage> ListRunHistoryAsync(RunHistoryQuery query, CancellationToken cancellationToken) => Task.FromResult(new RunHistoryPage([], false));
		public Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobSummary>>([]);
		public Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<JobSummary?>(null);
		public Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null) => Task.FromResult(Guid.NewGuid());
		public Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
		public Task<bool> CompleteEmptyRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(new AbortRunResult([], []));

		public Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(JobCancelOutcome.Cancelled);
		public Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken) => Task.FromResult(JobRetryOutcome.Retried);
		public Task<BulkJobActionResult<JobCancelOutcome>> BulkCancelJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken) =>
			Task.FromResult(new BulkJobActionResult<JobCancelOutcome>([.. jobIds.Select(id => new BulkJobItemResult<JobCancelOutcome>(id, JobCancelOutcome.Cancelled))]));
		public Task<BulkJobActionResult<JobRetryOutcome>> BulkRetryJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken) =>
			Task.FromResult(new BulkJobActionResult<JobRetryOutcome>([.. jobIds.Select(id => new BulkJobItemResult<JobRetryOutcome>(id, JobRetryOutcome.Retried))]));
		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) => Task.FromResult(new AuthFailureHaltResult(HaltTripped: false, [], []));
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
		{ Releases++; Progress.Signal(); return Task.FromResult(true); }
		public Task<CredentialUnblockResult> UnblockCredentialAsync(Guid credentialId, string? reason, CancellationToken cancellationToken)
		=> Task.FromResult(new CredentialUnblockResult(WasHalted: false, [], []));

		public Task<CredentialSwapResult> SwapAndResumeBlockedCredentialAsync(
			Guid runId, Guid replacementCredentialId, string actor, string? reason, CancellationToken cancellationToken)
		=> Task.FromResult(new CredentialSwapResult(CredentialSwapOutcome.RunNotHalted, null, null, []));

		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken)
		=> Task.CompletedTask;

		public Task RecordUploadAttemptAsync(Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken)
		=> Task.CompletedTask;

		public Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UploadAttemptRecord>>([]);

		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobCredentialBinding>>([]);
	public Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) => throw new NotSupportedException("fan-out not exercised by this fake");
	}
}
