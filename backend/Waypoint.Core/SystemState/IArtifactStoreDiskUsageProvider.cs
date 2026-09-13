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

namespace Waypoint.Core.SystemState;

/// <summary>
/// Reports live filesystem disk usage for the artifact store(s) the appliance writes
/// to (issue #226). M1 shipped exactly one store -- the download slice's
/// <c>DownloadOptions.ArtifactStorePath</c> (issue #10/#228). Issue #1534 (epic #1180)
/// made the implementation a composite over <see cref="INamedDiskUsageSource"/>, so
/// this interface's shape is unchanged (still one <see cref="ArtifactStoreUsage"/> per
/// store, still a plain synchronous list) while the number of stores it can report is
/// no longer hard-coded to one -- a later wave's sidecar volume (content library,
/// Photon repository -- see the UI prototype's LOCAL STORES rail) registers another
/// <see cref="INamedDiskUsageSource"/> and appears in this list with no change to
/// <c>GET /system</c>, its controller, or the frontend.
/// </summary>
public interface IArtifactStoreDiskUsageProvider
{
	/// <summary>
	/// Returns one <see cref="ArtifactStoreUsage"/> per configured store that currently
	/// reports usage successfully (a store whose source returned <c>null</c> is
	/// omitted -- see <see cref="INamedDiskUsageSource.GetUsage"/>). Synchronous by
	/// nature (a filesystem stat call per store), but returns a list so this composes
	/// naturally with whatever else <c>GET /system</c> gathers.
	/// </summary>
	IReadOnlyList<ArtifactStoreUsage> GetUsage();
}
