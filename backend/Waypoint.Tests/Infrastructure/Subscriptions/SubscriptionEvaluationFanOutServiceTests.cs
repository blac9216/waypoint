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

using Waypoint.Core.Jobs;
using Waypoint.Core.Subscriptions;
using Waypoint.Infrastructure.Subscriptions;
using Waypoint.Tests.Api;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Subscriptions;

/// <summary>
/// Issue #1472 AC3: one run, one <c>binaries-download</c> job per fetch-set item, via
/// the same <see cref="IJobControlRepository.CreateRunAsync"/> + <see cref="IJobControlRepository.FanOutJobsAsync"/>
/// shape <c>DownloadsController.QueueBinariesDownload</c> uses.
/// </summary>
public sealed class SubscriptionEvaluationFanOutServiceTests
{
	private sealed class FakeStateRepository : ISubscriptionEvaluationStateRepository
	{
		private IReadOnlyList<SubscriptionFetchItem>? _pending;
		public bool FannedOut { get; private set; }

		public void SeedPending(IReadOnlyList<SubscriptionFetchItem> items) => _pending = items;

		public Task<SubscriptionEvaluationState?> GetAsync(Guid subscriptionId, CancellationToken cancellationToken) => Task.FromResult<SubscriptionEvaluationState?>(null);
		public Task UpsertAsync(SubscriptionEvaluationState state, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task SetFetchSetAsync(Guid subscriptionId, IReadOnlyList<SubscriptionFetchItem> fetchSet, CancellationToken cancellationToken)
		{
			_pending = fetchSet;
			FannedOut = false;
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<SubscriptionFetchItem>?> GetPendingFetchSetAsync(Guid subscriptionId, CancellationToken cancellationToken)
			=> Task.FromResult(FannedOut ? null : _pending);

		public Task MarkFannedOutAsync(Guid subscriptionId, CancellationToken cancellationToken)
		{
			FannedOut = true;
			return Task.CompletedTask;
		}
	}

	[Fact]
	public async Task FanOutAsync_NoPendingFetchSet_ReturnsNullAndCreatesNoRun()
	{
		FakeStateRepository state = new();
		CatalogFakeJobQueueRepository jobs = new();
		SubscriptionEvaluationFanOutService service = new(state, jobs);

		Guid? runId = await service.FanOutAsync(Guid.NewGuid(), "system", CancellationToken.None);

		Assert.Null(runId);
		Assert.Null(jobs.LastCreateRun);
	}

	[Fact]
	public async Task FanOutAsync_PendingFetchSet_CreatesOneRunAndOneJobPerItem_ThenMarksConsumed()
	{
		FakeStateRepository state = new();
		state.SeedPending([
			new SubscriptionFetchItem(Guid.NewGuid(), "PROD/COMP/ACME/a-1.bin", "1.0", "bundle-a", 10),
			new SubscriptionFetchItem(Guid.NewGuid(), "PROD/COMP/ACME/a-2.bin", "1.1", "bundle-b", 20),
		]);
		CatalogFakeJobQueueRepository jobs = new();
		SubscriptionEvaluationFanOutService service = new(state, jobs);

		Guid? runId = await service.FanOutAsync(Guid.NewGuid(), "system", CancellationToken.None);

		Assert.NotNull(runId);
		Assert.Equal(RunTypes.BinariesDownload, jobs.LastCreateRun!.Value.RunType);
		Assert.NotNull(jobs.LastFanOut);
		Assert.Equal(2, jobs.LastFanOut!.Count);
		Assert.All(jobs.LastFanOut, spec => Assert.Equal(RunTypes.BinariesDownload, spec.JobType));
		Assert.True(state.FannedOut);

		// A second call finds nothing pending (already consumed) -- no duplicate run.
		Guid? secondRunId = await service.FanOutAsync(Guid.NewGuid(), "system", CancellationToken.None);
		Assert.Null(secondRunId);
	}

	[Fact]
	public async Task FanOutAsync_ItemsMissingBundleId_AreSkippedButOthersStillFanOut()
	{
		FakeStateRepository state = new();
		state.SeedPending([
			new SubscriptionFetchItem(Guid.NewGuid(), "PROD/COMP/ACME/a-1.bin", "1.0", BundleId: null, SizeBytes: 10),
			new SubscriptionFetchItem(Guid.NewGuid(), "PROD/COMP/ACME/a-2.bin", "1.1", "bundle-b", 20),
		]);
		CatalogFakeJobQueueRepository jobs = new();
		SubscriptionEvaluationFanOutService service = new(state, jobs);

		Guid? runId = await service.FanOutAsync(Guid.NewGuid(), "system", CancellationToken.None);

		Assert.NotNull(runId);
		Assert.Single(jobs.LastFanOut!);
	}

	[Fact]
	public async Task FanOutAsync_EveryItemMissingBundleId_MarksConsumedWithoutCreatingARun()
	{
		FakeStateRepository state = new();
		state.SeedPending([new SubscriptionFetchItem(Guid.NewGuid(), "PROD/COMP/ACME/a-1.bin", "1.0", BundleId: null, SizeBytes: 10)]);
		CatalogFakeJobQueueRepository jobs = new();
		SubscriptionEvaluationFanOutService service = new(state, jobs);

		Guid? runId = await service.FanOutAsync(Guid.NewGuid(), "system", CancellationToken.None);

		Assert.Null(runId);
		Assert.Null(jobs.LastCreateRun);
		Assert.True(state.FannedOut);
	}
}
