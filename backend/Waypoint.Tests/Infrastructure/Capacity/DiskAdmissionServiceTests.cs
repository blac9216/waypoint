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

using Waypoint.Core.Capacity;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Capacity;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Capacity;

/// <summary>
/// Issue #1531 (epic #1180, split from #1042, AC4): the pure admission decision
/// against a fake <see cref="IArtifactStoreDiskUsageProvider"/> and a fake
/// <see cref="IDiskAdmissionPolicyRepository"/> -- no live filesystem, no live
/// Postgres, exactly the "unit-testable without either" bar this issue's AC sets.
/// </summary>
public sealed class DiskAdmissionServiceTests
{
	private const string StoreName = ArtifactStoreNames.Default;

	private sealed class FakeDiskUsageProvider : IArtifactStoreDiskUsageProvider
	{
		private readonly IReadOnlyList<ArtifactStoreUsage> _usage;

		public FakeDiskUsageProvider(IReadOnlyList<ArtifactStoreUsage> usage) => _usage = usage;

		public IReadOnlyList<ArtifactStoreUsage> GetUsage() => _usage;
	}

	private sealed class FakePolicyRepository : IDiskAdmissionPolicyRepository
	{
		private readonly long _reserveBytes;

		public FakePolicyRepository(long reserveBytes) => _reserveBytes = reserveBytes;

		public Task<DiskAdmissionPolicy?> GetAsync(CancellationToken cancellationToken) =>
			Task.FromResult<DiskAdmissionPolicy?>(new DiskAdmissionPolicy(_reserveBytes, UpdatedBy: null, UpdatedAt: DateTimeOffset.UtcNow));

		public Task<DiskAdmissionPolicy> SetAsync(long reserveBytes, string actor, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by these tests.");
	}

	[Fact]
	public async Task AdmitAsync_ProjectedWithinFreeMinusReserve_Allows()
	{
		DiskAdmissionService service = new(
			new FakeDiskUsageProvider([new ArtifactStoreUsage(StoreName, "/store", TotalBytes: 1_000, UsedBytes: 200, FreeBytes: 800)]),
			new FakePolicyRepository(reserveBytes: 100));

		DiskAdmissionResult result = await service.AdmitAsync(projectedBytes: 700, StoreName, CancellationToken.None);

		Assert.True(result.Allowed);
		Assert.Equal(0, result.ShortfallBytes);
		Assert.Equal(800, result.FreeBytes);
		Assert.Equal(100, result.ReserveBytes);
	}

	[Fact]
	public async Task AdmitAsync_ProjectedWouldBreachReserve_Denies()
	{
		DiskAdmissionService service = new(
			new FakeDiskUsageProvider([new ArtifactStoreUsage(StoreName, "/store", TotalBytes: 1_000, UsedBytes: 200, FreeBytes: 800)]),
			new FakePolicyRepository(reserveBytes: 100));

		DiskAdmissionResult result = await service.AdmitAsync(projectedBytes: 750, StoreName, CancellationToken.None);

		Assert.False(result.Allowed);
		Assert.Equal(50, result.ShortfallBytes);
	}

	[Fact]
	public async Task AdmitAsync_ZeroReservePolicy_AdmitsUpToFreeBytesExactly()
	{
		DiskAdmissionService service = new(
			new FakeDiskUsageProvider([new ArtifactStoreUsage(StoreName, "/store", TotalBytes: 1_000, UsedBytes: 500, FreeBytes: 500)]),
			new FakePolicyRepository(reserveBytes: 0));

		DiskAdmissionResult exact = await service.AdmitAsync(projectedBytes: 500, StoreName, CancellationToken.None);
		DiskAdmissionResult overBy1 = await service.AdmitAsync(projectedBytes: 501, StoreName, CancellationToken.None);

		Assert.True(exact.Allowed);
		Assert.False(overBy1.Allowed);
		Assert.Equal(1, overBy1.ShortfallBytes);
	}

	[Fact]
	public async Task AdmitAsync_StoreNotReportingUsage_FailsOpen()
	{
		// GetUsage() omits a store whose directory does not exist yet or is unreadable
		// (ArtifactStoreDiskUsageProvider's own fail-open contract) -- this decision must
		// not become the thing that blocks a fresh appliance's first-ever download.
		DiskAdmissionService service = new(new FakeDiskUsageProvider([]), new FakePolicyRepository(reserveBytes: 100));

		DiskAdmissionResult result = await service.AdmitAsync(projectedBytes: 1_000_000, "unknown-store", CancellationToken.None);

		Assert.True(result.Allowed);
	}
}
