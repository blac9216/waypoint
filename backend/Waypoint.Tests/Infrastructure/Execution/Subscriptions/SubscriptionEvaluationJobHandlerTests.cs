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
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.Secrets;
using Waypoint.Core.Subscriptions;
using Waypoint.Infrastructure.Execution.Subscriptions;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution.Subscriptions;

/// <summary>
/// Issue #1472: the <c>subscription-evaluate</c> handler's orchestration -- payload
/// validation, the disabled/missing-subscription no-ops, and persisting the
/// <see cref="SubscriptionEvaluationService"/> outcome -- fully fake-able without
/// Postgres, mirroring <c>RetentionSweepJobHandlerTests</c>'s convention.
/// </summary>
public sealed class SubscriptionEvaluationJobHandlerTests
{
	private sealed class FakeSubscriptionRepository(Subscription? subscription) : ISubscriptionRepository
	{
		public Task<Guid> CreateAsync(Subscription subscription, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(subscription);
		public Task<IReadOnlyList<Subscription>> ListAllAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<IReadOnlyList<Subscription>> ListEnabledByLaneAsync(string lane, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeArtifactRepository(IReadOnlyList<DepotArtifact> artifacts) : IDepotArtifactRepository
	{
		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<IReadOnlyList<DepotArtifact>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new InvalidOperationException();

		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken)
			=> Task.FromResult<(IReadOnlyList<DepotArtifact>, long)>((artifacts, artifacts.Count));

		public Task<bool> SupersedeCatalogDocumentRowAsync(string catalogDocumentRelativePath, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeStateRepository : ISubscriptionEvaluationStateRepository
	{
		public SubscriptionEvaluationState? Upserted { get; private set; }
		public IReadOnlyList<SubscriptionFetchItem>? PersistedFetchSet { get; private set; }

		public Task<SubscriptionEvaluationState?> GetAsync(Guid subscriptionId, CancellationToken cancellationToken) => Task.FromResult<SubscriptionEvaluationState?>(null);

		public Task UpsertAsync(SubscriptionEvaluationState state, CancellationToken cancellationToken)
		{
			Upserted = state;
			return Task.CompletedTask;
		}

		public Task SetFetchSetAsync(Guid subscriptionId, IReadOnlyList<SubscriptionFetchItem> fetchSet, CancellationToken cancellationToken)
		{
			PersistedFetchSet = fetchSet;
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<SubscriptionFetchItem>?> GetPendingFetchSetAsync(Guid subscriptionId, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task MarkFannedOutAsync(Guid subscriptionId, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeGateway : ILibraryVersionCounterGateway
	{
		public Task<LibraryVersionCounterResult> GetVersionCounterAsync(string product, string lane, CancellationToken cancellationToken)
			=> Task.FromResult(new LibraryVersionCounterResult(true, 1, null));
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: Guid.NewGuid(), JobType: "subscription-evaluate", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private static Subscription MakeSubscription(bool enabled = true) => new(
		Id: Guid.NewGuid(), Product: "ACME", Lane: RepoStores.Depot, LineGranularity: SubscriptionLineGranularity.Minor,
		AnchorVersion: "11.4.7", PresetId: null, RefreshWindowDays: null, RetentionOverrideDays: null,
		IsEnabled: enabled, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

	[Fact]
	public async Task ExecuteAsync_MalformedPayload_Fails()
	{
		SubscriptionEvaluationJobHandler handler = new(
			new FakeSubscriptionRepository(null), new FakeArtifactRepository([]), new FakeStateRepository(),
			new SubscriptionEvaluationService(new SubscriptionFetchSetCalculator(new SubscriptionLineEvaluator()), new FakeGateway()));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("not-json"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_MissingSubscription_Fails()
	{
		SubscriptionEvaluationJobHandler handler = new(
			new FakeSubscriptionRepository(null), new FakeArtifactRepository([]), new FakeStateRepository(),
			new SubscriptionEvaluationService(new SubscriptionFetchSetCalculator(new SubscriptionLineEvaluator()), new FakeGateway()));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($"{{\"subscription_id\":\"{Guid.NewGuid()}\"}}"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_DisabledSubscription_SucceedsAsNoOp()
	{
		Subscription subscription = MakeSubscription(enabled: false);
		FakeStateRepository state = new();
		SubscriptionEvaluationJobHandler handler = new(
			new FakeSubscriptionRepository(subscription), new FakeArtifactRepository([]), state,
			new SubscriptionEvaluationService(new SubscriptionFetchSetCalculator(new SubscriptionLineEvaluator()), new FakeGateway()));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($"{{\"subscription_id\":\"{subscription.Id}\"}}"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Null(state.Upserted);
	}

	[Fact]
	public async Task ExecuteAsync_EnabledSubscriptionWithFetchSet_PersistsStateAndFetchSet()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact artifact = new(
			Guid.NewGuid(), "PROD/COMP/ACME/acme-11.4.9.bin", null, DepotArtifactStatuses.Indexed, "ACME", "11.4.9", "{}",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SizeBytes: 500, BundleId: "bundle-1");
		FakeStateRepository state = new();
		SubscriptionEvaluationJobHandler handler = new(
			new FakeSubscriptionRepository(subscription), new FakeArtifactRepository([artifact]), state,
			new SubscriptionEvaluationService(new SubscriptionFetchSetCalculator(new SubscriptionLineEvaluator()), new FakeGateway()));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($"{{\"subscription_id\":\"{subscription.Id}\"}}"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.NotNull(state.Upserted);
		Assert.Equal(1, state.Upserted!.LastFetchSetCount);
		Assert.Equal(500, state.Upserted.LastProjectedBytes);
		Assert.Single(state.PersistedFetchSet!);
	}
}
