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

using System.Linq;
using Waypoint.Core.SystemState;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for <c>GET /api/v1/system</c> (api-contract.md "System, users,
/// audit": "Version/build, mode, uptime, disk usage by store, depot sync, update
/// availability"). Issue #226 covers version/build/mode/update_available (already
/// consumed by the frontend's <c>SystemInfo</c> shape) plus <see cref="Stores"/> --
/// the depot-sync figure stays a follow-up (no depot-sync mechanism exists to report
/// on yet; tracked separately from this disk-usage slice). Issue #443 adds
/// <see cref="Runners"/>: this response distinguishes API/control-plane health (a 200
/// from this endpoint at all) from execution-domain availability (each entry here) --
/// the API process itself no longer executes anything, so "is the API up" and "can a
/// scan/discover/download job actually run right now" are two different questions,
/// and this is where the second one is answered.
/// </summary>
public sealed record SystemResponse(
	string Version,
	string Build,
	string Mode,
	string? UpdateAvailable,
	IReadOnlyList<SystemStoreUsageResponse> Stores,
	IReadOnlyList<SystemRunnerStatusResponse> Runners)
{
	public static SystemResponse Create(
		ApplianceState state,
		string buildSha,
		IReadOnlyList<ArtifactStoreUsage> stores,
		IReadOnlyList<SystemRunnerStatusResponse> runners) => new(
		Version: state.Version,
		Build: buildSha,
		Mode: state.Mode,
		UpdateAvailable: state.UpdateAvailableVersion,
		Stores: stores.Select(SystemStoreUsageResponse.FromDomain).ToArray(),
		Runners: runners);
}

/// <summary>One store's disk-usage figures, in bytes, per api-contract.md "disk usage by store".</summary>
public sealed record SystemStoreUsageResponse(string Name, string Path, long TotalBytes, long UsedBytes, long FreeBytes)
{
	public static SystemStoreUsageResponse FromDomain(ArtifactStoreUsage usage) =>
		new(usage.Name, usage.Path, usage.TotalBytes, usage.UsedBytes, usage.FreeBytes);
}

/// <summary>
/// One runner process's last-known liveness (issue #443, migration 0026
/// <c>worker_registry</c>). <see cref="WorkerId"/> and <see cref="LastSeenAt"/> are
/// operator-facing diagnostics ("which container, how long ago"); <see cref="Available"/>
/// is the derived verdict the UI should key its readiness indicator on --
/// <see cref="WorkerHeartbeat.Ready"/> AND not stale (see
/// <c>Waypoint.Core.SystemState.WorkerRegistryOptions.StaleAfter</c>).
/// </summary>
/// <param name="StarvedJobTypes">
/// Issue #467 (migration 0029): job types this worker was denying resource admission to
/// as of its last heartbeat -- "these job types cannot currently be admitted on this
/// runner," with <see cref="SystemStarvedJobTypeResponse.Permanent"/> distinguishing a
/// budget the type can never fit from transient contention that self-resolves. Empty
/// when nothing is starved, including for a worker whose <see cref="LastSeenAt"/> row
/// predates migration 0029 (defaults to no starvation reported rather than failing).
/// </param>
public sealed record SystemRunnerStatusResponse(
	string WorkerId,
	IReadOnlyList<string> JobTypes,
	bool Available,
	DateTimeOffset LastSeenAt,
	IReadOnlyList<SystemStarvedJobTypeResponse> StarvedJobTypes)
{
	public static SystemRunnerStatusResponse FromDomain(WorkerHeartbeat heartbeat, bool available) =>
		new(
			heartbeat.WorkerId,
			heartbeat.JobTypes,
			available,
			heartbeat.LastSeenAt,
			[.. heartbeat.StarvedJobTypes.Select(SystemStarvedJobTypeResponse.FromDomain)]);
}

/// <summary>One job type a runner is currently denying resource admission to (issue #467).</summary>
public sealed record SystemStarvedJobTypeResponse(string JobType, bool Permanent)
{
	public static SystemStarvedJobTypeResponse FromDomain(StarvedWorkerJobType starved) => new(starved.JobType, starved.Permanent);
}
