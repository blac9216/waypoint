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
using Waypoint.Core.Downloads;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.SystemState;

namespace Waypoint.Tests.Infrastructure.SystemState;

/// <summary>
/// Real-filesystem coverage for issue #226's disk-usage computation: figures are
/// filesystem-dependent (CI, a devcontainer, and a laptop all report different
/// absolute capacities for the same temp directory), so these tests assert
/// structure -- fields present, non-negative, and total ~= used + free -- never exact
/// byte counts. The controller-level shape/gate assertions live in
/// <c>Waypoint.Tests.Api.SystemEndpointTests</c>, against a fake of this interface.
/// </summary>
public sealed class ArtifactStoreDiskUsageProviderTests : IDisposable
{
	private readonly string _tempStoreRoot;

	public ArtifactStoreDiskUsageProviderTests()
	{
		_tempStoreRoot = Path.Combine(Path.GetTempPath(), "waypoint-system-tests-" + Guid.NewGuid().ToString("N"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempStoreRoot))
		{
			Directory.Delete(_tempStoreRoot, recursive: true);
		}
	}

	[Fact]
	public void GetUsage_ForConfiguredStorePath_ReturnsOneStoreWithSaneFigures()
	{
		IOptions<DownloadOptions> options = Options.Create(new DownloadOptions { ArtifactStorePath = _tempStoreRoot });
		ArtifactStoreDiskUsageProvider provider = new(options, NullLogger<ArtifactStoreDiskUsageProvider>.Instance);

		IReadOnlyList<ArtifactStoreUsage> stores = provider.GetUsage();

		ArtifactStoreUsage store = Assert.Single(stores);
		Assert.False(string.IsNullOrWhiteSpace(store.Name));
		Assert.Equal(_tempStoreRoot, store.Path);
		Assert.True(store.TotalBytes >= 0, "TotalBytes must be non-negative.");
		Assert.True(store.UsedBytes >= 0, "UsedBytes must be non-negative.");
		Assert.True(store.FreeBytes >= 0, "FreeBytes must be non-negative.");

		// total ~= used + free: exact equality is not guaranteed (filesystem metadata,
		// reserved blocks), so this allows a 1% tolerance rather than asserting exact
		// bytes, which would be filesystem-dependent and flaky.
		long reconstructed = store.UsedBytes + store.FreeBytes;
		long tolerance = Math.Max(1, store.TotalBytes / 100);
		Assert.InRange(reconstructed, store.TotalBytes - tolerance, store.TotalBytes + tolerance);
	}

	[Fact]
	public void GetUsage_CreatesTheStoreDirectoryIfMissing()
	{
		Assert.False(Directory.Exists(_tempStoreRoot));

		IOptions<DownloadOptions> options = Options.Create(new DownloadOptions { ArtifactStorePath = _tempStoreRoot });
		ArtifactStoreDiskUsageProvider provider = new(options, NullLogger<ArtifactStoreDiskUsageProvider>.Instance);

		provider.GetUsage();

		Assert.True(Directory.Exists(_tempStoreRoot));
	}
}
