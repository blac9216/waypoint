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

using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.SystemState;

namespace Waypoint.Tests.Infrastructure.SystemState;

/// <summary>
/// Issue #1534 AC2: registering a second <see cref="INamedDiskUsageSource"/> produces a
/// second entry in the composite's aggregate list, proven with fake sources (no real
/// sidecar volume exists yet) -- and AC1: a single registered source still reports one
/// entry, unchanged from the pre-#1534 provider's shape.
/// </summary>
public sealed class CompositeDiskUsageProviderTests
{
	private sealed class FakeDiskUsageSource(ArtifactStoreUsage? usage) : INamedDiskUsageSource
	{
		public ArtifactStoreUsage? GetUsage() => usage;
	}

	[Fact]
	public void GetUsage_OneRegisteredSource_ReturnsOneStore()
	{
		ArtifactStoreUsage depot = new(ArtifactStoreNames.Default, "/var/lib/waypoint/artifacts", 1000, 400, 600);
		CompositeDiskUsageProvider provider = new([new FakeDiskUsageSource(depot)]);

		IReadOnlyList<ArtifactStoreUsage> stores = provider.GetUsage();

		ArtifactStoreUsage store = Assert.Single(stores);
		Assert.Equal(depot, store);
	}

	[Fact]
	public void GetUsage_TwoRegisteredSources_ReturnsBothStoresWithNoOtherChange()
	{
		ArtifactStoreUsage depot = new(ArtifactStoreNames.Default, "/var/lib/waypoint/artifacts", 1000, 400, 600);
		ArtifactStoreUsage sidecar = new("Content library", "/var/lib/waypoint/content-libraries", 2000, 500, 1500);
		CompositeDiskUsageProvider provider = new([new FakeDiskUsageSource(depot), new FakeDiskUsageSource(sidecar)]);

		IReadOnlyList<ArtifactStoreUsage> stores = provider.GetUsage();

		Assert.Equal(2, stores.Count);
		Assert.Contains(depot, stores);
		Assert.Contains(sidecar, stores);
	}

	[Fact]
	public void GetUsage_ASourceReturningNull_IsOmittedRatherThanFailingTheWholeAggregate()
	{
		ArtifactStoreUsage depot = new(ArtifactStoreNames.Default, "/var/lib/waypoint/artifacts", 1000, 400, 600);
		CompositeDiskUsageProvider provider = new([new FakeDiskUsageSource(depot), new FakeDiskUsageSource(usage: null)]);

		IReadOnlyList<ArtifactStoreUsage> stores = provider.GetUsage();

		ArtifactStoreUsage store = Assert.Single(stores);
		Assert.Equal(depot, store);
	}

	[Fact]
	public void GetUsage_NoRegisteredSources_ReturnsEmptyList()
	{
		CompositeDiskUsageProvider provider = new([]);

		Assert.Empty(provider.GetUsage());
	}
}
