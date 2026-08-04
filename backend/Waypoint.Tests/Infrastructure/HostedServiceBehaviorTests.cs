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
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure;

public sealed class HostedServiceBehaviorTests
{
	[Fact]
	public async Task DisabledServices_LogAndDoNotPoll()
	{
		FakeRepository repository = new(); FakeEvents events = new();
		CapturingLogger<JobDispatcherHostedService> dispatcherLog = new();
		CapturingLogger<LeaseRecoveryHostedService> recoveryLog = new();
		IOptions<JobEngineOptions> options = Options.Create(new JobEngineOptions { Enabled = false });
		await new JobDispatcherHostedService(repository, events, new JobHandlerRegistry([]), options, dispatcherLog).StartAsync(CancellationToken.None);
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
		await WaitAsync(() => repository.Releases > 0 || repository.Moves.Count > 0);
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
		await WaitAsync(() => repository.Claims > 1);
		await service.StopAsync(CancellationToken.None);
		Assert.Contains(logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("Claim attempt failed", StringComparison.Ordinal));
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
		await WaitAsync(() => events.Count > 0);
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

	private static JobDispatcherHostedService Dispatcher(FakeRepository repository, CapturingLogger<JobDispatcherHostedService> logger) =>
		new(repository, new FakeEvents(), new JobHandlerRegistry([]), Options.Create(new JobEngineOptions
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			LeaseDuration = TimeSpan.FromSeconds(1)
		}), logger);

	private static ClaimedJob Job(Guid runId) => new(Guid.NewGuid(), runId, "download", null, null, null, 1, "{}", 1, 3);
	private static async Task WaitAsync(Func<bool> condition)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private sealed class FakeEvents : IJobEventPublisher
	{
		public int Count { get; private set; }
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{ Count++; return Task.CompletedTask; }
	}

	private sealed class FakeRepository : IJobQueueRepository
	{
		public ClaimedJob? NextClaim { get; set; }
		public RunQueueState? RunState { get; set; }
		public IReadOnlyList<RecoveredJob> NextRecovery { get; set; } = [];
		public int ThrowClaims { get; set; }
		public int ThrowRecoveries { get; set; }
		public int Claims { get; private set; }
		public int Releases { get; private set; }
		public List<(string From, string To)> Moves { get; } = [];
		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			Claims++; if (ThrowClaims-- > 0)
			{
				throw new InvalidOperationException("claim failed");
			}

			ClaimedJob? value = NextClaim; NextClaim = null; return Task.FromResult(value);
		}
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken)
		{ Moves.Add((expectedFromState, toState)); return Task.FromResult(true); }
		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
		{
			if (ThrowRecoveries-- > 0)
			{
				throw new InvalidOperationException("recovery failed");
			}

			IReadOnlyList<RecoveredJob> value = NextRecovery; NextRecovery = []; return Task.FromResult(value);
		}
		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(RunState);
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
		{ Releases++; return Task.FromResult(true); }
	}
}
