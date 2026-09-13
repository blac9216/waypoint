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

namespace Waypoint.Infrastructure.Capacity;

/// <inheritdoc cref="IDiskAdmissionService"/>
public sealed class DiskAdmissionService : IDiskAdmissionService
{
	private readonly IArtifactStoreDiskUsageProvider _diskUsage;
	private readonly IDiskAdmissionPolicyRepository _policy;

	public DiskAdmissionService(IArtifactStoreDiskUsageProvider diskUsage, IDiskAdmissionPolicyRepository policy)
	{
		ArgumentNullException.ThrowIfNull(diskUsage);
		ArgumentNullException.ThrowIfNull(policy);
		_diskUsage = diskUsage;
		_policy = policy;
	}

	/// <summary>
	/// A store <paramref name="storeName"/> does not currently report usage for
	/// (directory not yet created, or unreadable -- see
	/// <see cref="Waypoint.Infrastructure.SystemState.ArtifactStoreDiskUsageProvider.GetUsage"/>'s
	/// own fail-open doc comment) admits by design: this decision must not become the
	/// thing that blocks a fresh appliance's very first download before its store
	/// directory exists, matching the same fail-open posture <c>GetUsage</c> already
	/// established for this exact condition.
	/// </summary>
	public async Task<DiskAdmissionResult> AdmitAsync(long projectedBytes, string storeName, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

		DiskAdmissionPolicy? policy = await _policy.GetAsync(cancellationToken).ConfigureAwait(false);
		long reserveBytes = policy?.ReserveBytes ?? 0;

		ArtifactStoreUsage? usage = _diskUsage.GetUsage().FirstOrDefault(store => store.Name == storeName);
		if (usage is null)
		{
			return new DiskAdmissionResult(Allowed: true, projectedBytes, FreeBytes: 0, reserveBytes, ShortfallBytes: 0);
		}

		long admissibleBytes = usage.FreeBytes - reserveBytes;
		long shortfallBytes = Math.Max(0, projectedBytes - admissibleBytes);
		bool allowed = shortfallBytes == 0;

		return new DiskAdmissionResult(allowed, projectedBytes, usage.FreeBytes, reserveBytes, shortfallBytes);
	}
}
