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

namespace Waypoint.Infrastructure.SystemState;

/// <inheritdoc cref="IArtifactStoreDiskUsageProvider"/>
/// <remarks>
/// Issue #1534 (epic #1180, split from #1042): aggregates every registered
/// <see cref="INamedDiskUsageSource"/> into the single flat list <c>GET /system</c> and
/// <see cref="Waypoint.Core.Capacity.IDiskAdmissionService"/> already consume, so adding
/// a source (a later wave's sidecar volume) changes nothing about the controller, the
/// frontend, or <see cref="Waypoint.Infrastructure.Capacity.DiskAdmissionService"/>'s
/// store-by-name lookup. Today exactly one source is registered
/// (<see cref="ArtifactStoreDiskUsageSource"/>), so <c>/system</c>'s <c>stores</c> array
/// shape and content are unchanged from the pre-#1534 single-store provider (issue
/// #1534 AC1).
/// </remarks>
public sealed class CompositeDiskUsageProvider : IArtifactStoreDiskUsageProvider
{
	private readonly IEnumerable<INamedDiskUsageSource> _sources;

	public CompositeDiskUsageProvider(IEnumerable<INamedDiskUsageSource> sources)
	{
		ArgumentNullException.ThrowIfNull(sources);
		_sources = sources;
	}

	/// <summary>
	/// Queries every registered source and returns the non-null results. A source that
	/// returns <c>null</c> (its store's directory does not exist yet, or its filesystem
	/// cannot be statted) is silently omitted, matching the fail-open posture the M1
	/// single-store provider already established -- one unavailable store must not turn
	/// the whole aggregate into a 500, nor hide the other stores that are readable.
	/// </summary>
	public IReadOnlyList<ArtifactStoreUsage> GetUsage() =>
		[.. _sources.Select(source => source.GetUsage()).Where(usage => usage is not null).Select(usage => usage!)];
}
