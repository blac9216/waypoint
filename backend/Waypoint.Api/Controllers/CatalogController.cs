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
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md depot catalog surface (issue #193, epic #9 slice 1):
/// <c>GET /catalog/artifacts</c> (viewer-gated, filtered + paginated) and
/// <c>POST /catalog/sync</c> (admin-gated, fans a <c>catalog-index</c> job through
/// the #5 engine). No <c>catalog-index</c> handler is registered yet -- the job
/// engine dispatcher fails an unhandled job type cleanly (see
/// <c>JobHandlerRegistry</c>'s doc comment), which is the correct, documented
/// behaviour for this slice; the handler itself is issue #194.
/// </summary>
[ApiController]
[Route("api/v1/catalog")]
public sealed class CatalogController : ControllerBase
{
	/// <summary>
	/// <c>catalog-index</c> carries no per-target work (docs/domain-model.md's six
	/// scan priorities do not apply here), so it always runs at the highest priority
	/// so a manually triggered re-sync isn't starved behind a large scan run.
	/// </summary>
	private const short CatalogIndexPriority = 1;

	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobControlRepository _jobs;

	public CatalogController(IDepotArtifactRepository artifacts, IJobControlRepository jobs)
	{
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(jobs);
		_artifacts = artifacts;
		_jobs = jobs;
	}

	/// <summary>
	/// List the indexed depot catalog. Viewer+ -- browsable without the download tool
	/// installed (docs/domain-model.md open question 4).
	/// </summary>
	[HttpGet("artifacts")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CatalogArtifactResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<CatalogArtifactResponse>>> ListArtifacts(
		[FromQuery] string? product,
		[FromQuery] string? version,
		[FromQuery] string? status,
		[FromQuery] PageRequest page,
		CancellationToken cancellationToken)
	{
		DepotArtifactFilter filter = new(product, version, status);
		(IReadOnlyList<DepotArtifact> items, long totalCount) = await _artifacts
			.ListAsync(filter, page, cancellationToken)
			.ConfigureAwait(false);

		Response.Headers["X-Total-Count"] = totalCount.ToString(CultureInfo.InvariantCulture);
		return Ok(items.Select(CatalogArtifactResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Kick off a depot re-index. Admin -- creates a run with one <c>catalog-index</c>
	/// job and returns 202 immediately; progress is on the event stream, per the
	/// contract's "long-running operations return 202" convention.
	/// </summary>
	[HttpPost("sync")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CatalogSyncStartedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<CatalogSyncStartedResponse>> Sync(CancellationToken cancellationToken)
	{
		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		Guid runId = await _jobs.CreateRunAsync("catalog-index", "{}", credentialId: null, initiatedBy, cancellationToken)
			.ConfigureAwait(false);

		JobSpec[] specs = [new JobSpec("catalog-index", CatalogIndexPriority, TargetName: "depot")];
		await _jobs.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

		return Accepted(new CatalogSyncStartedResponse(runId.ToString()));
	}
}
