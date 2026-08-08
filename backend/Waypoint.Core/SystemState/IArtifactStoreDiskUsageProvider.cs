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
/// to (issue #226). M1 has exactly one store -- the download slice's
/// <c>DownloadOptions.ArtifactStorePath</c> (issue #10/#228) -- so this returns a
/// single-element list today; the api-contract.md "disk usage by store" shape allows
/// more (content library, Photon repository -- see the UI prototype's LOCAL STORES
/// rail) once those stores exist in later milestones.
/// </summary>
public interface IArtifactStoreDiskUsageProvider
{
	/// <summary>
	/// Returns one <see cref="ArtifactStoreUsage"/> per configured store. Synchronous
	/// by nature (a filesystem stat call), but returns a list so this composes
	/// naturally with whatever else <c>GET /system</c> gathers.
	/// </summary>
	IReadOnlyList<ArtifactStoreUsage> GetUsage();
}
