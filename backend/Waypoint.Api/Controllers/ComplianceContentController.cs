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
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/compliance-content` surface (issue #40, backend slice 1 of
/// 2 -- the Config screen itself is a follow-up): singleton repo/ref config (GET/PUT,
/// Admin writes), pull history, and <c>POST /pull</c> which fans a <c>content-pull</c>
/// job (ADR-0017: executed by compliance-runner). <c>POST /compliance-content/check</c>
/// (a no-mutation upstream diff) and <c>POST /compliance-content/import</c> (air-gapped
/// bundle import -> <c>content-import</c>) are out of scope for this slice -- see this
/// PR's body for what remains.
/// </summary>
[ApiController]
[Route("api/v1/compliance-content")]
public sealed class ComplianceContentController : ControllerBase
{
	/// <summary>content-pull carries no per-target work, so it always runs at the highest priority (matches <c>CatalogController.CatalogIndexPriority</c>'s reasoning).</summary>
	private const short ContentPullPriority = 1;

	private readonly IComplianceContentRepository _content;
	private readonly IJobControlRepository _jobs;

	public ComplianceContentController(IComplianceContentRepository content, IJobControlRepository jobs)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(jobs);
		_content = content;
		_jobs = jobs;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComplianceContentResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ComplianceContentResponse>> Get(CancellationToken cancellationToken)
	{
		ComplianceContentConfig? config = await _content.GetConfigAsync(cancellationToken).ConfigureAwait(false);
		return config is null
			? throw new ApiException(HttpStatusCode.NotFound, "not_found", "No compliance-content repository is configured.")
			: Ok(ComplianceContentResponse.FromDomain(config));
	}

	[HttpPut]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ComplianceContentResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ComplianceContentResponse>> Put([FromBody] ComplianceContentBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (string.IsNullOrWhiteSpace(request.RepositoryUrl) || !Uri.TryCreate(request.RepositoryUrl, UriKind.Absolute, out _))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'repository_url' is required and must be an absolute URI.");
		}

		if (!ComplianceContentRefTypes.IsValid(request.RefType))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'ref_type' must be 'tag' or 'branch'.");
		}

		if (string.IsNullOrWhiteSpace(request.RefValue))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'ref_value' is required.");
		}

		ComplianceContentConfig saved = await _content
			.PutConfigAsync(request.RepositoryUrl, request.RefType!, request.RefValue, cancellationToken)
			.ConfigureAwait(false);
		return Ok(ComplianceContentResponse.FromDomain(saved));
	}

	/// <summary>Pull history (issue #40 AC "who/when/commit"). Newest first, bounded to the 50 most recent attempts.</summary>
	[HttpGet("pulls")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComplianceContentPullResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ComplianceContentPullResponse>>> ListPulls(CancellationToken cancellationToken)
	{
		IReadOnlyList<ComplianceContentPull> pulls = await _content.ListPullsAsync(50, cancellationToken).ConfigureAwait(false);
		return Ok(pulls.Select(ComplianceContentPullResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Kicks off a pull. Admin -- creates a run with one <c>content-pull</c> job and
	/// returns 202 immediately; progress is on the event stream (same convention as
	/// <c>CatalogController.Sync</c>).
	/// </summary>
	[HttpPost("pull")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ContentPullStartedResponse), StatusCodes.Status202Accepted)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<ContentPullStartedResponse>> Pull(CancellationToken cancellationToken)
	{
		ComplianceContentConfig? config = await _content.GetConfigAsync(cancellationToken).ConfigureAwait(false);
		if (config is null)
		{
			throw new ApiException(HttpStatusCode.Conflict, "not_configured", "No compliance-content repository is configured; PUT /compliance-content first.");
		}

		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		Guid runId = await _jobs.CreateRunAsync("content-pull", "{}", credentialId: null, initiatedBy, cancellationToken)
			.ConfigureAwait(false);

		JobSpec[] specs = [new JobSpec("content-pull", ContentPullPriority, TargetName: "compliance-content")];
		await _jobs.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

		return Accepted(new ContentPullStartedResponse(runId.ToString()));
	}
}
