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
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md depot catalog surface (issue #193, epic #9 slice 1):
/// <c>GET /catalog/artifacts</c> (viewer-gated, filtered + paginated), <c>POST
/// /catalog/sync</c> (admin-gated, local credential-free re-index, issue #690 AC),
/// and the issue #687 connected surface -- <c>GET /catalog/pull</c> (readiness plus
/// last attempt/success facts) and <c>POST /catalog/pull</c> (fans a distinct
/// <c>catalog-pull</c> job). A connected pull is disabled (409) until issue #691's
/// <c>depot_enrollment</c> state is <c>validated</c> -- tool installed, Depot ID
/// generated, and a matching Activation Code accepted and proven against the tool.
/// </summary>
[ApiController]
[Route("api/v1/catalog")]
public sealed class CatalogController : ControllerBase
{
	/// <summary>
	/// <c>catalog-index</c>/<c>catalog-pull</c> carry no per-target work
	/// (docs/domain-model.md's six scan priorities do not apply here), so both always
	/// run at the highest priority so a manually triggered re-sync/pull isn't starved
	/// behind a large scan run.
	/// </summary>
	private const short CatalogIndexPriority = 1;

	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobControlRepository _jobs;
	private readonly ICatalogPullStateRepository _pullState;
	private readonly IDepotEnrollmentRepository _enrollment;

	public CatalogController(
		IDepotArtifactRepository artifacts,
		IJobControlRepository jobs,
		ICatalogPullStateRepository pullState,
		IDepotEnrollmentRepository enrollment)
	{
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(pullState);
		ArgumentNullException.ThrowIfNull(enrollment);
		_artifacts = artifacts;
		_jobs = jobs;
		_pullState = pullState;
		_enrollment = enrollment;
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

	/// <summary>
	/// Connected catalog-pull readiness plus the most recent attempt/success facts
	/// (issue #687). Viewer+, matching <see cref="ListArtifacts"/> -- reading status
	/// is not a state-changing action. <see cref="CatalogPullStatusResponse.Ready"/>
	/// is <see langword="false"/> whenever <see cref="DepotEnrollmentStates.Validated"/>
	/// has not been reached, with an operator-actionable reason so the Download
	/// Catalog screen can disable "Pull vendor catalog" with an explanation rather
	/// than a bare disabled button.
	/// </summary>
	[HttpGet("pull")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CatalogPullStatusResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<CatalogPullStatusResponse>> PullStatus(CancellationToken cancellationToken)
	{
		CatalogPullState? state = await _pullState.GetAsync(cancellationToken).ConfigureAwait(false);
		(bool ready, string? reason) = await EvaluateReadinessAsync(cancellationToken).ConfigureAwait(false);
		return Ok(CatalogPullStatusResponse.FromDomain(state, ready, reason));
	}

	/// <summary>
	/// Kick off a connected vendor catalog pull. Admin -- creates a run with one
	/// <c>catalog-pull</c> job (distinct job type from <c>catalog-index</c>) and
	/// returns 202 immediately; progress/logs are on the event stream, same
	/// convention as <see cref="Sync"/>. Refuses with 409 before ever queuing the job
	/// when the #691 enrollment gate is not satisfied, so an operator gets an
	/// immediate, actionable reason instead of a job that fails later on the runner.
	/// </summary>
	[HttpPost("pull")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CatalogPullStartedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<CatalogPullStartedResponse>> Pull(CancellationToken cancellationToken)
	{
		(bool ready, string? reason) = await EvaluateReadinessAsync(cancellationToken).ConfigureAwait(false);
		if (!ready)
		{
			throw new ApiException(HttpStatusCode.Conflict, "catalog_pull_not_ready", reason!);
		}

		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		Guid runId = await _jobs.CreateRunAsync("catalog-pull", "{}", credentialId: null, initiatedBy, cancellationToken)
			.ConfigureAwait(false);

		JobSpec[] specs = [new JobSpec("catalog-pull", CatalogIndexPriority, TargetName: "depot")];
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

		return Accepted(new CatalogPullStartedResponse(runId.ToString(), jobIds[0].ToString()));
	}

	private async Task<(bool Ready, string? Reason)> EvaluateReadinessAsync(CancellationToken cancellationToken)
	{
		DepotEnrollment? enrollment = await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false);
		if (enrollment is null || !string.Equals(enrollment.State, DepotEnrollmentStates.Validated, StringComparison.Ordinal))
		{
			return (false,
				"Connected catalog pull is disabled until the managed tool is installed, a Software Depot ID is generated, " +
				"and a matching Activation Code has been validated (see Depot & Tokens enrollment).");
		}

		return (true, null);
	}
}
