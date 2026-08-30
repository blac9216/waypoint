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
using Waypoint.Core.ComplianceContent;
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
///
/// Issue #728 (epic #726 Wave 1 remainder) adds the unrelated normalized compliance
/// *execution* catalog read surface, <c>GET /catalog/products</c> and <c>GET
/// /catalog/products/{id}</c> -- the closed, versioned execution-catalog vocabulary
/// (products/exact versions, component transport/selector, credential purposes,
/// priority, benchmark, remediation capability) shipped in this repository and backed
/// by <see cref="ICatalogRepository"/> (migration 0050, PR #822). It shares this
/// controller/route prefix with the depot-catalog surface above only because both are
/// named "catalog" in docs/api-contract.md -- they are otherwise unrelated resources
/// (depot artifacts to download vs. the compliance execution-catalog vocabulary) with
/// no shared repository or state.
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
	private readonly IUnknownCatalogFileRepository _unknownFiles;
	private readonly IJobControlRepository _jobs;
	private readonly ICatalogPullStateRepository _pullState;
	private readonly IDepotEnrollmentRepository _enrollment;
	private readonly ICatalogRepository _executionCatalog;

	public CatalogController(
		IDepotArtifactRepository artifacts,
		IUnknownCatalogFileRepository unknownFiles,
		IJobControlRepository jobs,
		ICatalogPullStateRepository pullState,
		IDepotEnrollmentRepository enrollment,
		ICatalogRepository executionCatalog)
	{
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(unknownFiles);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(pullState);
		ArgumentNullException.ThrowIfNull(enrollment);
		ArgumentNullException.ThrowIfNull(executionCatalog);
		_artifacts = artifacts;
		_unknownFiles = unknownFiles;
		_jobs = jobs;
		_pullState = pullState;
		_enrollment = enrollment;
		_executionCatalog = executionCatalog;
	}

	/// <summary>
	/// Issue #728: the full closed, versioned execution-catalog vocabulary, one row per
	/// execution profile (component bound to one exact content release), fully joined
	/// with its owning product/version/component/content-release/report-group identity
	/// plus credential requirements, benchmark reference, and remediation capability.
	/// Viewer+ -- read-only reflection of the reviewed catalog shipped in this
	/// repository (ADR-0022: "Operators cannot upload executable plugins, scripts, or
	/// catalog mappings"); there is no write endpoint.
	/// </summary>
	[HttpGet("products")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CatalogProductResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<CatalogProductResponse>>> ListProducts(CancellationToken cancellationToken)
	{
		IReadOnlyList<CatalogExecutionProfileDetail> details = await _executionCatalog
			.ListAllExecutionProfilesAsync(cancellationToken)
			.ConfigureAwait(false);
		return Ok(details.Select(CatalogProductResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Issue #728: single execution-profile detail by id -- same joined shape as
	/// <see cref="ListProducts"/>'s rows. Viewer+. 404 when the id does not name a
	/// known execution profile.
	/// </summary>
	[HttpGet("products/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CatalogProductResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<CatalogProductResponse>> GetProduct(Guid id, CancellationToken cancellationToken)
	{
		CatalogExecutionProfileDetail? detail = await _executionCatalog.GetExecutionProfileAsync(id, cancellationToken).ConfigureAwait(false);
		if (detail is null)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No catalog execution profile exists with id '{id}'.");
		}

		return Ok(CatalogProductResponse.FromDomain(detail));
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
	/// Issue #1495 AC2: files a depot share holds that the authenticated vendor
	/// catalog does not describe (migration 0100, issue #1488's
	/// <c>unknown_catalog_files</c>) -- visible on the API surface rather than only
	/// logged, per decision Q11's "alert instead of drop" pattern. Viewer+, matching
	/// <see cref="ListArtifacts"/> -- this is a read of already-recorded facts, not a
	/// state-changing action. No filter/pagination (same "read side is small" call as
	/// <see cref="IUnknownCatalogFileRepository.ListAsync"/>'s own doc comment) --
	/// populated by the real presence sweep in #1503/#1512; this slice proves the
	/// storage shape and the read contract those children will call.
	/// </summary>
	[HttpGet("unknown-files")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CatalogUnknownFileResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<CatalogUnknownFileResponse>>> ListUnknownFiles(CancellationToken cancellationToken)
	{
		IReadOnlyList<UnknownCatalogFile> items = await _unknownFiles.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(items.Select(CatalogUnknownFileResponse.FromDomain).ToArray());
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
