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
using IApplianceStateRepository = Waypoint.Core.SystemState.IApplianceStateRepository;
using IArtifactStoreDiskUsageProvider = Waypoint.Core.SystemState.IArtifactStoreDiskUsageProvider;

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
/// </summary>
[ApiController]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
	private readonly IApplianceStateRepository _applianceState;
	private readonly IArtifactStoreDiskUsageProvider _diskUsage;
	private readonly IOptionsMonitor<WaypointBuildOptions> _buildOptions;

	public SystemController(
		IApplianceStateRepository applianceState,
		IArtifactStoreDiskUsageProvider diskUsage,
		IOptionsMonitor<WaypointBuildOptions> buildOptions)
	{
		ArgumentNullException.ThrowIfNull(applianceState);
		ArgumentNullException.ThrowIfNull(diskUsage);
		ArgumentNullException.ThrowIfNull(buildOptions);
		_applianceState = applianceState;
		_diskUsage = diskUsage;
		_buildOptions = buildOptions;
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

		return Ok(SystemResponse.Create(state, _buildOptions.CurrentValue.Sha, stores));
	}
}
