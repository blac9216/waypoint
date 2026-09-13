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
/// Issue #1534 (epic #1180, split from #1042): one store's disk-usage source, the unit
/// <see cref="IArtifactStoreDiskUsageProvider"/>'s composite implementation aggregates
/// over. Each registered source knows exactly one store's name+path (the depot/artifact
/// store today; a later wave's sidecar volume -- UMDS/Photon/VMTools/VKS/content-library
/// -- registers another one with no change to the composite, <c>/system</c>, or the
/// frontend).
/// </summary>
public interface INamedDiskUsageSource
{
	/// <summary>
	/// Returns this store's current usage, or <c>null</c> when the store's directory
	/// does not exist yet or its filesystem cannot be statted -- the same fail-open
	/// posture the M1 single-store provider established (a fresh appliance, or a wave
	/// not yet reached, must not turn <c>GET /system</c> into a 500). The composite
	/// provider omits a <c>null</c> result from its aggregate list rather than
	/// propagating the failure.
	/// </summary>
	ArtifactStoreUsage? GetUsage();
}
