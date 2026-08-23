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
using Waypoint.Core.Capacity;
using Waypoint.Core.SystemState;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for <c>GET /api/v1/system</c> (api-contract.md "System, users,
/// audit": "Version/build, mode, uptime, disk usage by store, depot sync, update
/// availability"). Issue #226 covers version/build/mode/update_available (already
/// consumed by the frontend's <c>SystemInfo</c> shape) plus <see cref="Stores"/>.
/// Issue #443 adds <see cref="Runners"/>: this response distinguishes
/// API/control-plane health (a 200 from this endpoint at all) from execution-domain
/// availability (each entry here) -- the API process itself no longer executes
/// anything, so "is the API up" and "can a scan/discover/download job actually run
/// right now" are two different questions, and this is where the second one is
/// answered. Issue #241 closes out the two fields #226 deliberately deferred:
/// <see cref="UptimeSeconds"/> (trivial, but unconsumed until now) and
/// <see cref="DepotSync"/> (no sync mechanism existed to report on until
/// <c>catalog-index</c> runs, issue #194, gave it one). Both are additive --
/// existing consumers of this response are unaffected.
/// </summary>
public sealed record SystemResponse(
	string Version,
	string Build,
	string Mode,
	string? UpdateAvailable,
	IReadOnlyList<SystemStoreUsageResponse> Stores,
	IReadOnlyList<SystemRunnerStatusResponse> Runners,
	long UptimeSeconds,
	SystemDepotSyncResponse? DepotSync,
	SystemCapacityPoolResponse? CapacityPool = null)
{
	public static SystemResponse Create(
		ApplianceState state,
		string buildSha,
		IReadOnlyList<ArtifactStoreUsage> stores,
		IReadOnlyList<SystemRunnerStatusResponse> runners,
		TimeSpan uptime,
		DepotSyncStatus? depotSync,
		CapacityPoolStatus? capacityPool = null) => new(
		Version: state.Version,
		Build: buildSha,
		Mode: state.Mode,
		UpdateAvailable: state.UpdateAvailableVersion,
		Stores: stores.Select(SystemStoreUsageResponse.FromDomain).ToArray(),
		Runners: runners,
		UptimeSeconds: (long)uptime.TotalSeconds,
		DepotSync: depotSync is null ? null : SystemDepotSyncResponse.FromDomain(depotSync),
		CapacityPool: capacityPool is null ? null : SystemCapacityPoolResponse.FromDomain(capacityPool));
}

/// <summary>
/// The shared capacity lease pool's live state (issue #569, ADR-0020): total shareable
/// appliance capacity, what unexpired active leases currently hold, and every waiting
/// anti-starvation reservation. Absent (not an error, same convention as
/// <see cref="SystemResponse.DepotSync"/>) when no runner has registered the pool yet
/// -- e.g. a stack whose runners predate migration 0036 or run with
/// <c>CapacityPool:Enabled=false</c>.
/// </summary>
/// <param name="Source"><c>operator</c> (explicit configuration, authoritative) or <c>derived</c> (host-derived, ADR-0018 discovery).</param>
/// <param name="Reservations">
/// Each entry is a job currently starved of pool capacity whose runner has parked a
/// reservation holding freed capacity for it (ADR-0020 fairness) -- the "starvation
/// reasons" surface the issue #569 acceptance criteria call for.
/// </param>
public sealed record SystemCapacityPoolResponse(
	double CpuCores,
	long MemoryBytes,
	string Source,
	DateTimeOffset UpdatedAt,
	double LeasedCpuCores,
	long LeasedMemoryBytes,
	int ActiveLeaseCount,
	IReadOnlyList<SystemCapacityReservationResponse> Reservations)
{
	public static SystemCapacityPoolResponse FromDomain(CapacityPoolStatus status) => new(
		status.CpuCores,
		status.MemoryBytes,
		status.Source,
		status.UpdatedAt,
		status.LeasedCpuCores,
		status.LeasedMemoryBytes,
		status.ActiveLeaseCount,
		[.. status.Reservations.Select(SystemCapacityReservationResponse.FromDomain)]);
}

/// <summary>One waiting anti-starvation reservation (issue #569, ADR-0020).</summary>
public sealed record SystemCapacityReservationResponse(
	Guid JobId,
	string JobType,
	string RunnerId,
	double CpuCores,
	long MemoryBytes,
	DateTimeOffset WaitingSince)
{
	public static SystemCapacityReservationResponse FromDomain(CapacityReservation reservation) => new(
		reservation.JobId,
		reservation.JobType,
		reservation.RunnerId,
		reservation.CpuCores,
		reservation.MemoryBytes,
		reservation.WaitingSince);
}

/// <summary>
/// The depot's last completed <c>catalog-index</c> run (issue #241), per
/// api-contract.md's "depot sync" field. Absent (not an error) when no
/// <c>catalog-index</c> run has ever completed -- see
/// <see cref="IDepotSyncStatusRepository.GetLastSyncAsync"/>.
/// </summary>
public sealed record SystemDepotSyncResponse(DateTimeOffset LastSyncAt, bool Succeeded)
{
	public static SystemDepotSyncResponse FromDomain(DepotSyncStatus status) => new(status.CompletedAt, status.Succeeded);
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
