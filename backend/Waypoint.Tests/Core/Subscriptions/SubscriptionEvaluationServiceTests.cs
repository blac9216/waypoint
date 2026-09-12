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

using Waypoint.Core.Catalog;
using Waypoint.Core.Secrets;
using Waypoint.Core.Subscriptions;
using Xunit;

namespace Waypoint.Tests.Core.Subscriptions;

/// <summary>
/// Issue #1472 AC1/AC2/Risks: the fetch-set/projected-bytes diff, the lib.json
/// version-counter short-circuit (fail-open on a pre-check error), and the honest
/// "unknown" projected-bytes total. Fixture products/versions are invented (repo
/// convention).
/// </summary>
public sealed class SubscriptionEvaluationServiceTests
{
	private static Subscription MakeSubscription(string lane = RepoStores.Depot, string anchorVersion = "11.4.7") => new(
		Id: Guid.NewGuid(), Product: "ACME", Lane: lane, LineGranularity: SubscriptionLineGranularity.Minor,
		AnchorVersion: anchorVersion, PresetId: null, RefreshWindowDays: null, RetentionOverrideDays: null,
		IsEnabled: true, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

	private static DepotArtifact MakeArtifact(string version, string status, long? sizeBytes = 100, string? bundleId = "bundle-1") => new(
		Id: Guid.NewGuid(), ExternalId: $"PROD/COMP/ACME/acme-{version}.bin", Sha256: null, Status: status, Product: "ACME",
		Version: version, MetadataJson: "{}", IndexedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
		SizeBytes: sizeBytes, BundleId: bundleId);

	private sealed class FakeGateway : ILibraryVersionCounterGateway
	{
		public LibraryVersionCounterResult Result { get; set; } = new(true, 1, null);
		public int CallCount { get; private set; }

		public Task<LibraryVersionCounterResult> GetVersionCounterAsync(string product, string lane, CancellationToken cancellationToken)
		{
			CallCount++;
			return Task.FromResult(Result);
		}
	}

	/// <summary>Spy calculator proving AC2's "diff method is not called" on a short-circuited evaluation.</summary>
	private sealed class SpyFetchSetCalculator : ISubscriptionFetchSetCalculator
	{
		private readonly SubscriptionFetchSetCalculator _inner = new(new SubscriptionLineEvaluator());
		public int CallCount { get; private set; }

		public IReadOnlyList<SubscriptionFetchItem> ComputeFetchSet(Subscription subscription, IReadOnlyList<DepotArtifact> candidateArtifacts)
		{
			CallCount++;
			return _inner.ComputeFetchSet(subscription, candidateArtifacts);
		}
	}

	[Fact]
	public async Task EvaluateAsync_NewerVersionOnLine_ComputesNonEmptyFetchSetWithProjectedBytes()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact[] artifacts = [MakeArtifact("11.4.9", DepotArtifactStatuses.Indexed, sizeBytes: 250)];
		SubscriptionEvaluationService service = new(new SpyFetchSetCalculator(), new FakeGateway());

		SubscriptionEvaluationOutcome outcome = await service.EvaluateAsync(subscription, artifacts, priorState: null, CancellationToken.None);

		Assert.False(outcome.Skipped);
		Assert.Single(outcome.FetchSet);
		Assert.Equal(250, outcome.ProjectedBytes);
	}

	[Fact]
	public async Task EvaluateAsync_PresentArtifact_NeverEntersFetchSet()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact[] artifacts = [MakeArtifact("11.4.9", DepotArtifactStatuses.Present)];
		SubscriptionEvaluationService service = new(new SpyFetchSetCalculator(), new FakeGateway());

		SubscriptionEvaluationOutcome outcome = await service.EvaluateAsync(subscription, artifacts, priorState: null, CancellationToken.None);

		Assert.Empty(outcome.FetchSet);
		Assert.Equal(0, outcome.ProjectedBytes);
	}

	[Fact]
	public async Task EvaluateAsync_AnyFetchSetItemMissingSize_ProjectsUnknownNotPartialSum()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact[] artifacts =
		[
			MakeArtifact("11.4.9", DepotArtifactStatuses.Indexed, sizeBytes: 250),
			new(Guid.NewGuid(), "PROD/COMP/ACME/acme-11.4.10.bin", null, DepotArtifactStatuses.Indexed, "ACME", "11.4.10", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SizeBytes: null),
		];
		SubscriptionEvaluationService service = new(new SpyFetchSetCalculator(), new FakeGateway());

		SubscriptionEvaluationOutcome outcome = await service.EvaluateAsync(subscription, artifacts, priorState: null, CancellationToken.None);

		Assert.Equal(2, outcome.FetchSet.Count);
		Assert.Null(outcome.ProjectedBytes);
	}

	[Fact]
	public async Task EvaluateAsync_NonLibraryLane_NeverCallsVersionCounterGateway()
	{
		Subscription subscription = MakeSubscription(lane: RepoStores.Depot);
		FakeGateway gateway = new();
		SubscriptionEvaluationService service = new(new SpyFetchSetCalculator(), gateway);

		await service.EvaluateAsync(subscription, [], priorState: null, CancellationToken.None);

		Assert.Equal(0, gateway.CallCount);
	}

	[Fact]
	public async Task EvaluateAsync_LibraryMirrorLane_UnchangedCounter_SkipsDiff_DoesNotCallFetchSetCalculator()
	{
		Subscription subscription = MakeSubscription(lane: RepoStores.ContentLibraries);
		SubscriptionEvaluationState priorState = new(subscription.Id, LastSeenLibVersionCounter: 42, LastEvaluatedAt: DateTimeOffset.UtcNow, LastFetchSetCount: 0, LastProjectedBytes: 0);
		FakeGateway gateway = new() { Result = new LibraryVersionCounterResult(true, 42, null) };
		SpyFetchSetCalculator calculator = new();
		SubscriptionEvaluationService service = new(calculator, gateway);

		SubscriptionEvaluationOutcome outcome = await service.EvaluateAsync(
			subscription, [MakeArtifact("11.4.9", DepotArtifactStatuses.Indexed)], priorState, CancellationToken.None);

		Assert.True(outcome.Skipped);
		Assert.Empty(outcome.FetchSet);
		Assert.Equal(42, outcome.VersionCounter);
		Assert.Equal(0, calculator.CallCount);
	}

	[Fact]
	public async Task EvaluateAsync_LibraryMirrorLane_ChangedCounter_RunsFullDiff()
	{
		Subscription subscription = MakeSubscription(lane: RepoStores.ContentLibraries);
		SubscriptionEvaluationState priorState = new(subscription.Id, LastSeenLibVersionCounter: 41, LastEvaluatedAt: DateTimeOffset.UtcNow, LastFetchSetCount: 0, LastProjectedBytes: 0);
		FakeGateway gateway = new() { Result = new LibraryVersionCounterResult(true, 42, null) };
		SpyFetchSetCalculator calculator = new();
		SubscriptionEvaluationService service = new(calculator, gateway);

		SubscriptionEvaluationOutcome outcome = await service.EvaluateAsync(
			subscription, [MakeArtifact("11.4.9", DepotArtifactStatuses.Indexed)], priorState, CancellationToken.None);

		Assert.False(outcome.Skipped);
		Assert.Single(outcome.FetchSet);
		Assert.Equal(42, outcome.VersionCounter);
		Assert.Equal(1, calculator.CallCount);
	}

	[Fact]
	public async Task EvaluateAsync_LibraryMirrorLane_PreCheckFails_FailsOpenAndRunsDiff_KeepsPriorCounter()
	{
		Subscription subscription = MakeSubscription(lane: RepoStores.ContentLibraries);
		SubscriptionEvaluationState priorState = new(subscription.Id, LastSeenLibVersionCounter: 41, LastEvaluatedAt: DateTimeOffset.UtcNow, LastFetchSetCount: 0, LastProjectedBytes: 0);
		FakeGateway gateway = new() { Result = new LibraryVersionCounterResult(false, null, "network error") };
		SpyFetchSetCalculator calculator = new();
		SubscriptionEvaluationService service = new(calculator, gateway);

		SubscriptionEvaluationOutcome outcome = await service.EvaluateAsync(
			subscription, [MakeArtifact("11.4.9", DepotArtifactStatuses.Indexed)], priorState, CancellationToken.None);

		Assert.False(outcome.Skipped);
		Assert.Single(outcome.FetchSet);
		Assert.Equal(41, outcome.VersionCounter);
		Assert.Equal(1, calculator.CallCount);
	}
}
