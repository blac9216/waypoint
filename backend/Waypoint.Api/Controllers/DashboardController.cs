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

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Infrastructure.Runs;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/dashboard` surface (issue #513, prototype screen 2): "Aggregate:
/// KPI tiles, site posture, recent runs, attention items." Reads are Viewer+, matching
/// every other list/get endpoint. See <see cref="DashboardAggregateService"/> for the
/// aggregation itself and <see cref="DashboardResponse"/> for why the SCHEDULES/APPLIANCE
/// sidebar cards are NOT part of this payload (frontend composes those from the existing
/// `/schedules` and `/system` endpoints instead of duplicating them here).
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
public sealed class DashboardController : ControllerBase
{
	private readonly DashboardAggregateService _aggregate;

	public DashboardController(DashboardAggregateService aggregate)
	{
		ArgumentNullException.ThrowIfNull(aggregate);
		_aggregate = aggregate;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(DashboardResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DashboardResponse>> Get(CancellationToken cancellationToken)
	{
		DashboardAggregate aggregate = await _aggregate.BuildAsync(cancellationToken).ConfigureAwait(false);

		return Ok(new DashboardResponse(
			Targets: new DashboardTargetsResponse(aggregate.TargetTotal, aggregate.Sites.Count, aggregate.TargetsByKind),
			Findings: new DashboardFindingsResponse(
				aggregate.FindingsCountsAvailable, aggregate.FindingsCatIOpen, aggregate.FindingsCatIIOpen, aggregate.FindingsCatIIIOpen),
			Sites: [.. aggregate.Sites.Select(MapSite)],
			RecentRuns: [.. aggregate.RecentRuns.Select(MapRun)],
			Attention: [.. aggregate.Attention.Select(a => new DashboardAttentionResponse(a.Kind, a.Text, a.Meta))]));
	}

	private static DashboardSiteResponse MapSite(DashboardSiteRow site) => new(
		Id: site.Id.ToString(),
		Name: site.Name,
		TargetCount: site.TargetCount,
		CompliancePercent: site.CompliancePercent,
		CatIOpen: site.CatIOpen,
		CatIIOpen: site.CatIIOpen,
		CatIIIOpen: site.CatIIIOpen,
		LastScanAt: site.LastScanAt?.ToString("O", CultureInfo.InvariantCulture));

	private static DashboardRunResponse MapRun(Core.Jobs.RunSummary run) => new(
		Id: run.Id.ToString(),
		RunType: run.RunType,
		State: run.State,
		Scope: run.ScopeJson,
		JobCount: run.JobCount,
		CreatedAt: run.CreatedAt ?? string.Empty);
}
