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

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for <c>GET /api/v1/system</c> (api-contract.md "System, users,
/// audit": "Version/build, mode, uptime, disk usage by store, depot sync, update
/// availability"). Issue #226 covers version/build/mode/update_available (already
/// consumed by the frontend's <c>SystemInfo</c> shape) plus <see cref="Stores"/> --
/// the depot-sync figure stays a follow-up (no depot-sync mechanism exists to report
/// on yet; tracked separately from this disk-usage slice).
/// </summary>
public sealed record SystemResponse(
	string Version,
	string Build,
	string Mode,
	string? UpdateAvailable,
	IReadOnlyList<SystemStoreUsageResponse> Stores)
{
	public static SystemResponse Create(ApplianceState state, string buildSha, IReadOnlyList<ArtifactStoreUsage> stores) => new(
		Version: state.Version,
		Build: buildSha,
		Mode: state.Mode,
		UpdateAvailable: state.UpdateAvailableVersion,
		Stores: stores.Select(SystemStoreUsageResponse.FromDomain).ToArray());
}

/// <summary>One store's disk-usage figures, in bytes, per api-contract.md "disk usage by store".</summary>
public sealed record SystemStoreUsageResponse(string Name, string Path, long TotalBytes, long UsedBytes, long FreeBytes)
{
	public static SystemStoreUsageResponse FromDomain(ArtifactStoreUsage usage) =>
		new(usage.Name, usage.Path, usage.TotalBytes, usage.UsedBytes, usage.FreeBytes);
}
