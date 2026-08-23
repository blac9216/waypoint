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

using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;
using Waypoint.Core.Errors;
using ApplianceState = Waypoint.Core.SystemState.ApplianceState;
using ArtifactStoreUsage = Waypoint.Core.SystemState.ArtifactStoreUsage;
using CapacityPoolStatus = Waypoint.Core.Capacity.CapacityPoolStatus;
using DepotSyncStatus = Waypoint.Core.SystemState.DepotSyncStatus;
using IApplianceStateRepository = Waypoint.Core.SystemState.IApplianceStateRepository;
using IApplianceUptimeProvider = Waypoint.Core.SystemState.IApplianceUptimeProvider;
using IArtifactStoreDiskUsageProvider = Waypoint.Core.SystemState.IArtifactStoreDiskUsageProvider;
using ICapacityPoolStatusReader = Waypoint.Core.Capacity.ICapacityPoolStatusReader;
using IDepotSyncStatusRepository = Waypoint.Core.SystemState.IDepotSyncStatusRepository;
using IWorkerRegistryReader = Waypoint.Core.SystemState.IWorkerRegistryReader;
using WorkerHeartbeat = Waypoint.Core.SystemState.WorkerHeartbeat;
using WorkerRegistryOptions = Waypoint.Core.SystemState.WorkerRegistryOptions;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md "System, users, audit" <c>/system</c> surface (issue #226):
/// "Version/build, mode, uptime, disk usage by store, depot sync, update
/// availability." This slice covers version/build/mode/update_available -- the
/// subset the frontend's <c>SystemInfo</c> (frontend/src/lib/system.tsx) already
/// consumes -- plus <c>stores</c>, the artifact-store disk-usage figures this issue
/// exists to add (queue rail "LOCAL STORES", UI prototype). Uptime and depot-sync are
/// not reported yet: there is no depot-sync mechanism to report on until a scheduled
/// catalog sync exists, and uptime was judged not worth a fabricated process-start
/// clock in the same PR as real disk figures -- both are honest follow-ups, not
/// silently dropped scope.
///
/// Issue #443 adds <c>runners</c>: since the API process no longer executes anything
/// (ADR-0013 §1), this endpoint is now the only place "is a scan/discover/download job
/// actually able to run right now" is answered. A 200 from this endpoint proves
/// control-plane health regardless of what <c>runners</c> says -- a stopped or
/// unreachable compliance-runner never turns this into a 5xx, it is reported as an
/// unavailable entry in the list (or a missing one, once its heartbeat ages past
/// <see cref="WorkerRegistryOptions.StaleAfter"/> and it stops appearing at all).
///
/// Issue #241 adds the two fields #226 deferred: <c>uptime_seconds</c> (this API
/// process's wall-clock uptime) and <c>depot_sync</c> (the most recent completed
/// <c>catalog-index</c> run, derived from the <c>runs</c> table -- absent, not an
/// error, when none has ever completed).
/// </summary>
[ApiController]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
	private readonly IApplianceStateRepository _applianceState;
	private readonly IArtifactStoreDiskUsageProvider _diskUsage;
	private readonly IWorkerRegistryReader _workerRegistry;
	private readonly IApplianceUptimeProvider _uptime;
	private readonly IDepotSyncStatusRepository _depotSync;
	private readonly ICapacityPoolStatusReader _capacityPool;
	private readonly IOptionsMonitor<WaypointBuildOptions> _buildOptions;
	private readonly IOptionsMonitor<WorkerRegistryOptions> _workerRegistryOptions;

	public SystemController(
		IApplianceStateRepository applianceState,
		IArtifactStoreDiskUsageProvider diskUsage,
		IWorkerRegistryReader workerRegistry,
		IApplianceUptimeProvider uptime,
		IDepotSyncStatusRepository depotSync,
		ICapacityPoolStatusReader capacityPool,
		IOptionsMonitor<WaypointBuildOptions> buildOptions,
		IOptionsMonitor<WorkerRegistryOptions> workerRegistryOptions)
	{
		ArgumentNullException.ThrowIfNull(applianceState);
		ArgumentNullException.ThrowIfNull(diskUsage);
		ArgumentNullException.ThrowIfNull(workerRegistry);
		ArgumentNullException.ThrowIfNull(uptime);
		ArgumentNullException.ThrowIfNull(depotSync);
		ArgumentNullException.ThrowIfNull(capacityPool);
		ArgumentNullException.ThrowIfNull(buildOptions);
		ArgumentNullException.ThrowIfNull(workerRegistryOptions);
		_applianceState = applianceState;
		_diskUsage = diskUsage;
		_workerRegistry = workerRegistry;
		_uptime = uptime;
		_depotSync = depotSync;
		_capacityPool = capacityPool;
		_buildOptions = buildOptions;
		_workerRegistryOptions = workerRegistryOptions;
	}

	/// <summary>
	/// Viewer+ -- matches the health probe's "no secrets here" posture; disk usage and
	/// version/mode are operational chrome, not privileged data (same gate as
	/// <c>GET /catalog/artifacts</c>).
	/// </summary>
	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(SystemResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<SystemResponse>> Get(CancellationToken cancellationToken)
	{
		ApplianceState? state = await _applianceState.GetAsync(cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			// Migration 0001 seeds this row unconditionally and nothing deletes it --
			// reaching here means the singleton was removed out of band, which is a
			// server-side integrity problem, not a client error.
			throw new ApiException(HttpStatusCode.InternalServerError, "system_state_missing", "appliance_state row is missing.");
		}

		IReadOnlyList<ArtifactStoreUsage> stores = _diskUsage.GetUsage();

		IReadOnlyList<WorkerHeartbeat> heartbeats = await _workerRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		TimeSpan staleAfter = _workerRegistryOptions.CurrentValue.StaleAfter;
		SystemRunnerStatusResponse[] runners = [.. heartbeats.Select(heartbeat =>
		{
			bool stale = now - heartbeat.LastSeenAt > staleAfter;
			return SystemRunnerStatusResponse.FromDomain(heartbeat, available: heartbeat.Ready && !stale);
		})];

		DepotSyncStatus? depotSync = await _depotSync.GetLastSyncAsync(cancellationToken).ConfigureAwait(false);

		// Issue #569 (ADR-0020): pool capacity, active reservations, and starvation
		// reasons. Absent (null) when no runner has registered the pool -- graceful
		// empty state, same convention as depot_sync.
		CapacityPoolStatus? capacityPool = await _capacityPool.GetStatusAsync(cancellationToken).ConfigureAwait(false);

		return Ok(SystemResponse.Create(state, _buildOptions.CurrentValue.Sha, stores, runners, _uptime.GetUptime(), depotSync, capacityPool));
	}
}
