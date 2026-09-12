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

/// <summary>Issue #1472 AC1: the pure fetch-set diff, in isolation from the lib.json pre-check.</summary>
public sealed class SubscriptionFetchSetCalculatorTests
{
	private readonly SubscriptionFetchSetCalculator _calculator = new(new SubscriptionLineEvaluator());

	private static Subscription MakeSubscription() => new(
		Id: Guid.NewGuid(), Product: "ACME", Lane: RepoStores.Depot, LineGranularity: SubscriptionLineGranularity.Minor,
		AnchorVersion: "11.4.7", PresetId: null, RefreshWindowDays: null, RetentionOverrideDays: null,
		IsEnabled: true, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

	private static DepotArtifact MakeArtifact(string product, string version, string status, string? bundleId = "bundle-1") => new(
		Id: Guid.NewGuid(), ExternalId: $"PROD/COMP/{product}/{product}-{version}.bin", Sha256: null, Status: status, Product: product,
		Version: version, MetadataJson: "{}", IndexedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
		SizeBytes: 10, BundleId: bundleId);

	[Fact]
	public void ComputeFetchSet_DifferentProduct_IsExcluded()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact[] artifacts = [MakeArtifact("OTHER", "11.4.9", DepotArtifactStatuses.Indexed)];

		Assert.Empty(_calculator.ComputeFetchSet(subscription, artifacts));
	}

	[Fact]
	public void ComputeFetchSet_OutOfLineVersion_IsExcluded()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact[] artifacts = [MakeArtifact("ACME", "12.0.0", DepotArtifactStatuses.Indexed)];

		Assert.Empty(_calculator.ComputeFetchSet(subscription, artifacts));
	}

	[Fact]
	public void ComputeFetchSet_QuarantinedVersion_IsExcluded()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact[] artifacts = [MakeArtifact("ACME", "not-a-version", DepotArtifactStatuses.Indexed)];

		Assert.Empty(_calculator.ComputeFetchSet(subscription, artifacts));
	}

	[Fact]
	public void ComputeFetchSet_InLineAndNotPresent_IsIncluded()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact artifact = MakeArtifact("ACME", "11.4.9", DepotArtifactStatuses.Missing);

		SubscriptionFetchItem item = Assert.Single(_calculator.ComputeFetchSet(subscription, [artifact]));
		Assert.Equal(artifact.Id, item.DepotArtifactId);
		Assert.Equal(artifact.BundleId, item.BundleId);
	}

	[Fact]
	public void ComputeFetchSet_ResultIsOrderedByExternalId()
	{
		Subscription subscription = MakeSubscription();
		DepotArtifact b = MakeArtifact("ACME", "11.4.9", DepotArtifactStatuses.Indexed);
		DepotArtifact a = MakeArtifact("ACME", "11.4.8", DepotArtifactStatuses.Indexed);

		IReadOnlyList<SubscriptionFetchItem> items = _calculator.ComputeFetchSet(subscription, [b, a]);

		Assert.Equal(2, items.Count);
		Assert.True(string.CompareOrdinal(items[0].ExternalId, items[1].ExternalId) <= 0);
	}
}
