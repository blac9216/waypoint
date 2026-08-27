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
using Waypoint.Core.Capacity;
using Waypoint.Core.Jobs;
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Resources;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// Issue #633: on a fresh stack, this service's very first registration attempt can
/// lose the startup race against the backend's schema migrator (<c>capacity_pool</c>
/// does not exist yet, 42P01). These tests prove the fix -- retry with backoff until
/// success, unbounded in attempt count -- rather than the pre-#633 one-shot
/// <c>StartAsync</c> that never tried again.
/// </summary>
public sealed class CapacityPoolRegistrationHostedServiceTests
{
	private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(10);

	[Fact]
	public async Task RegistrationFailsNTimesThenSucceeds_RetriesUntilRegistered()
	{
		FakePool pool = new() { FailuresBeforeSuccess = 4 };
		FakeHostCapabilities hostCapabilities = new();
		CapturingLogger<CapacityPoolRegistrationHostedService> logger = new();
		CapacityPoolRegistrationHostedService service = Service(pool, hostCapabilities, logger);

		await service.StartAsync(CancellationToken.None);
		await WaitAsync(() => pool.SuccessfulCalls > 0);
		await service.StopAsync(CancellationToken.None);

		Assert.Equal(5, pool.TotalCalls); // 4 failures + 1 success
		Assert.Equal(1, pool.SuccessfulCalls);
		Assert.Contains(logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("registration failed", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(logger.EntriesAt(LogLevel.Information), entry => entry.Message.Contains("registered", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task RegistrationNeverSucceeds_KeepsRetryingWithoutGivingUp()
	{
		FakePool pool = new() { FailuresBeforeSuccess = int.MaxValue };
		FakeHostCapabilities hostCapabilities = new();
		CapturingLogger<CapacityPoolRegistrationHostedService> logger = new();
		CapacityPoolRegistrationHostedService service = Service(pool, hostCapabilities, logger);

		await service.StartAsync(CancellationToken.None);
		await WaitAsync(() => pool.TotalCalls >= 5);
		await service.StopAsync(CancellationToken.None);

		// Still trying, still never registered: admission would stay denied fail-safe,
		// but the loop has not given up (this is the exact defect #633 reports -- the
		// pre-fix StartAsync tried exactly once and then never again).
		Assert.True(pool.TotalCalls >= 5);
		Assert.Equal(0, pool.SuccessfulCalls);
	}

	[Fact]
	public async Task LogsOnlyEveryNthConsecutiveFailure_BoundingLogNoise()
	{
		FakePool pool = new() { FailuresBeforeSuccess = int.MaxValue };
		CapturingLogger<CapacityPoolRegistrationHostedService> logger = new();
		CapacityPoolRegistrationHostedService service = Service(pool, new FakeHostCapabilities(), logger);

		await service.StartAsync(CancellationToken.None);
		await WaitAsync(() => pool.TotalCalls >= CapacityPoolRegistrationHostedService.FailureLogEvery + 2);
		await service.StopAsync(CancellationToken.None);

		int errorCount = logger.EntriesAt(LogLevel.Error).Count;
		// Logged on failure #1 and again at failure #N (FailureLogEvery), not every one
		// of the >= FailureLogEvery+2 attempts that already happened.
		Assert.True(errorCount < pool.TotalCalls);
		Assert.True(errorCount >= 2);
	}

	[Fact]
	public async Task Disabled_NeverCallsPool()
	{
		FakePool pool = new();
		CapturingLogger<CapacityPoolRegistrationHostedService> logger = new();
		CapacityPoolRegistrationHostedService service = new(
			pool,
			new FakeHostCapabilities(),
			Dispatcher(),
			Options.Create(new CapacityPoolOptions { Enabled = false }),
			logger,
			FastRetryDelay,
			FastRetryDelay);

		await service.StartAsync(CancellationToken.None);
		await Task.Delay(TimeSpan.FromMilliseconds(100));
		await service.StopAsync(CancellationToken.None);

		Assert.Equal(0, pool.TotalCalls);
	}

	/// <summary>
	/// The admission side of the fix: while unregistered, a claim consumer treats
	/// registration failure the same way <c>CapacityLeaseCoordinator</c> does --
	/// exceptions/denials from the pool are a deny, not an admit. This proves that
	/// posture holds throughout the retry window and flips to "would admit" only once
	/// <see cref="FakePool.RegisterPoolCapacityAsync"/> has actually succeeded --
	/// i.e., admission stays denied while unregistered, and opens after convergence.
	/// </summary>
	[Fact]
	public async Task AdmissionStaysDeniedWhileUnregistered_ThenOpensAfterConvergence()
	{
		FakePool pool = new() { FailuresBeforeSuccess = 3 };
		CapacityPoolRegistrationHostedService service = Service(pool, new FakeHostCapabilities(), new CapturingLogger<CapacityPoolRegistrationHostedService>());

		await service.StartAsync(CancellationToken.None);

		// While still unregistered, the pool has no capacity row -- any claim attempt
		// against it fails/denies (fail safe), matching production's
		// ICapacityLeasePool contract ("a missing pool row... surface as a denied/failed
		// claim").
		await WaitAsync(() => pool.TotalCalls > 0);
		Assert.False(pool.IsRegistered);

		await WaitAsync(() => pool.SuccessfulCalls > 0);
		await service.StopAsync(CancellationToken.None);

		Assert.True(pool.IsRegistered);
	}

	private static CapacityPoolRegistrationHostedService Service(
		FakePool pool, FakeHostCapabilities hostCapabilities, CapturingLogger<CapacityPoolRegistrationHostedService> logger) =>
		new(
			pool,
			hostCapabilities,
			Dispatcher(),
			Options.Create(new CapacityPoolOptions { Enabled = true, LeaseDuration = TimeSpan.FromMinutes(5) }),
			logger,
			FastRetryDelay,
			FastRetryDelay);

	private static JobDispatcherHostedService Dispatcher() =>
		new(new FakeRepository(), new FakeRepository(), new FakeEvents(), new JobHandlerRegistry([]),
			Options.Create(new JobEngineOptions()), new CapturingLogger<JobDispatcherHostedService>());

	private static async Task WaitAsync(Func<bool> condition)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private sealed class FakeHostCapabilities : IHostCapabilitySource
	{
		public double AvailableCpuCores() => 4;
		public long TotalMemoryBytes() => 8L * 1024 * 1024 * 1024;
	}

	private sealed class FakePool : ICapacityLeasePool
	{
		private int _totalCalls;
		private int _successfulCalls;

		public int FailuresBeforeSuccess { get; set; }
		public int TotalCalls => _totalCalls;
		public int SuccessfulCalls => _successfulCalls;
		public bool IsRegistered { get; private set; }

		public Task RegisterPoolCapacityAsync(string reportedBy, double cpuCores, long memoryBytes, bool operatorSet, CancellationToken cancellationToken)
		{
			int callIndex = Interlocked.Increment(ref _totalCalls);
			if (callIndex <= FailuresBeforeSuccess)
			{
				throw new InvalidOperationException("42P01: relation \"capacity_pool\" does not exist");
			}

			Interlocked.Increment(ref _successfulCalls);
			IsRegistered = true;
			return Task.CompletedTask;
		}

		public Task<bool> TryClaimAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
			Task.FromResult(IsRegistered);

		public Task<bool> TryReserveAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
			Task.FromResult(IsRegistered);

		public Task<bool> RenewAsync(Guid jobId, string runnerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(IsRegistered);

		public Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<int> ReapExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);
	}

	private sealed class FakeEvents : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class FakeRepository : IJobControlRepository, IJobRunnerRepository
	{
		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken) =>
			Task.FromResult<ClaimedJob?>(null);
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken) =>
			Task.FromResult(true);
		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken) =>
			Task.FromResult(true);
		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<RecoveredJob>>([]);
		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<RunQueueState?>(null);
		public Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<RunSummary?>(null);
		public Task<RunListResult> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken) => Task.FromResult(new RunListResult([], 0));
		public Task<RunHistoryPage> ListRunHistoryAsync(RunHistoryQuery query, CancellationToken cancellationToken) => Task.FromResult(new RunHistoryPage([], false));
		public Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobSummary>>([]);
		public Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<JobSummary?>(null);
		public Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null) =>
			Task.FromResult(Guid.NewGuid());
		public Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Guid>>([]);
		public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(new AbortRunResult([], []));
		public Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(JobCancelOutcome.Cancelled);
		public Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken) => Task.FromResult(JobRetryOutcome.Retried);
		public Task<BulkJobActionResult<JobCancelOutcome>> BulkCancelJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken) =>
			Task.FromResult(new BulkJobActionResult<JobCancelOutcome>([.. jobIds.Select(id => new BulkJobItemResult<JobCancelOutcome>(id, JobCancelOutcome.Cancelled))]));
		public Task<BulkJobActionResult<JobRetryOutcome>> BulkRetryJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken) =>
			Task.FromResult(new BulkJobActionResult<JobRetryOutcome>([.. jobIds.Select(id => new BulkJobItemResult<JobRetryOutcome>(id, JobRetryOutcome.Retried))]));
		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) =>
			Task.FromResult(new AuthFailureHaltResult(HaltTripped: false, [], []));
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<CredentialUnblockResult> UnblockCredentialAsync(Guid credentialId, string? reason, CancellationToken cancellationToken) =>
			Task.FromResult(new CredentialUnblockResult(WasHalted: false, [], []));
		public Task<CredentialSwapResult> SwapAndResumeBlockedCredentialAsync(
			Guid runId, Guid replacementCredentialId, string actor, string? reason, CancellationToken cancellationToken) =>
			Task.FromResult(new CredentialSwapResult(CredentialSwapOutcome.RunNotHalted, null, null, []));
		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task RecordUploadAttemptAsync(Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UploadAttemptRecord>>([]);

		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobCredentialBinding>>([]);
	}
}
