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
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Errors;
using Waypoint.Infrastructure.ComplianceContent;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The missing operator surface for issue #731's staged/activate lifecycle. Round-5
/// live-lab validation (epic #726) found the domain logic complete but unreachable:
/// <see cref="IBaselineRepository.CreateStagedBaselineAsync"/> and
/// <see cref="BaselineActivationService.ActivateAsync"/> had zero non-test callers and
/// no route, so <c>baselines</c> stayed empty after every successful content promote
/// and the planner honestly reported <c>no_active_baseline</c> for every linked
/// component. This controller wires the existing repository/service -- it invents no
/// new activation semantics, only the HTTP surface docs/api-contract.md names
/// (<c>GET /baselines</c>, <c>POST /baselines/{id}/rollback</c>) plus the stage-create
/// and activate routes the contract's "Content: activate/roll back a baseline" RBAC
/// row implies but does not spell out as its own path -- see this PR's body for that
/// naming assumption.
///
/// RBAC floor: every write here is Admin-only (docs/api-contract.md's RBAC summary row
/// "Content: activate/roll back a baseline; waive a candidate test" lists only ✅ under
/// Admin; `/candidate-content/{id}/activate` and `/baselines/{id}/rollback` are both
/// documented Admin-only). Reads are Viewer+ (`GET /baselines` is documented
/// "Viewer+"), matching every other read surface's floor in this codebase
/// (EndpointRoleMatrixTests). Staging a baseline (binding an already-staged content
/// revision to an execution profile, issue #731's "candidate lifecycle" step before
/// activation) is gated Admin here too -- it is the same trust boundary as content-pull
/// (<c>ComplianceContentController.Pull</c>, Admin) and activation itself, and
/// ADR-0022's "the activation boundary is exclusive" already restricts every
/// <c>baselines</c>-table write to this same actor class.
/// </summary>
[ApiController]
[Route("api/v1/baselines")]
public sealed class BaselinesController : ControllerBase
{
	private readonly IBaselineRepository _baselines;
	private readonly BaselineActivationService _activation;

	public BaselinesController(IBaselineRepository baselines, BaselineActivationService activation)
	{
		ArgumentNullException.ThrowIfNull(baselines);
		ArgumentNullException.ThrowIfNull(activation);
		_baselines = baselines;
		_activation = activation;
	}

	/// <summary>Every baseline, active/superseded/staged (docs/api-contract.md "GET /baselines ... Viewer+").</summary>
	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(IReadOnlyList<BaselineResponse>), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<BaselineResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<Baseline> baselines = await _baselines.ListAllBaselinesAsync(cancellationToken).ConfigureAwait(false);
		return Ok(baselines.Select(BaselineResponse.FromDomain).ToList());
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BaselineResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<BaselineResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Baseline? baseline = await _baselines.GetBaselineAsync(id, cancellationToken).ConfigureAwait(false);
		return baseline is null
			? throw ApiException.NotFound($"No baseline exists with id '{id}'.")
			: Ok(BaselineResponse.FromDomain(baseline));
	}

	/// <summary>
	/// Stages a new baseline candidate binding an already-staged
	/// <see cref="ContentRevision"/> to a catalog execution profile
	/// (<see cref="IBaselineRepository.CreateStagedBaselineAsync"/>). Does not activate
	/// it -- multiple staged baselines may coexist for the same execution profile
	/// (ADR-0022 "independent candidate lifecycles"); a separate <c>POST .../activate</c>
	/// call is required before it becomes scan-eligible.
	/// </summary>
	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(BaselineResponse), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<BaselineResponse>> Create([FromBody] CreateBaselineRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.ContentRevisionId is null || request.ContentRevisionId == Guid.Empty)
		{
			throw ApiException.Validation("'content_revision_id' is required.");
		}

		if (request.CatalogExecutionProfileId is null || request.CatalogExecutionProfileId == Guid.Empty)
		{
			throw ApiException.Validation("'catalog_execution_profile_id' is required.");
		}

		ContentRevision? revision = await _baselines.GetRevisionAsync(request.ContentRevisionId.Value, cancellationToken).ConfigureAwait(false);
		if (revision is null)
		{
			throw ApiException.NotFound($"No content revision exists with id '{request.ContentRevisionId}'.");
		}

		if (revision.Status == ContentRevisionStatuses.Rejected)
		{
			throw ApiException.Validation(
				"The referenced content revision was rejected and cannot be staged into a baseline.",
				$"content_revision_id '{revision.Id}' has status '{revision.Status}'.");
		}

		Baseline created = await _baselines.CreateStagedBaselineAsync(
			request.ContentRevisionId.Value, request.CatalogExecutionProfileId.Value, request.BenchmarkRevisionId, cancellationToken)
			.ConfigureAwait(false);

		return CreatedAtAction(nameof(Get), new { id = created.Id }, BaselineResponse.FromDomain(created));
	}

	/// <summary>
	/// Issue #731 AC "operators see a deterministic impact diff before activation" --
	/// this slice's profile-identity-level diff (<see cref="BaselineActivationService.ComputeImpactDiffAsync"/>);
	/// a full per-control semantic diff is <c>/candidate-content/{id}/diff</c>'s
	/// separate, already-planned concern (issue #730).
	/// </summary>
	[HttpGet("{id:guid}/impact-diff")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BaselineImpactDiffResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<BaselineImpactDiffResponse>> ImpactDiff(Guid id, CancellationToken cancellationToken)
	{
		try
		{
			BaselineImpactDiff diff = await _activation.ComputeImpactDiffAsync(id, cancellationToken).ConfigureAwait(false);
			return Ok(BaselineImpactDiffResponse.FromDomain(diff));
		}
		catch (InvalidOperationException)
		{
			throw ApiException.NotFound($"No baseline exists with id '{id}'.");
		}
	}

	/// <summary>
	/// Admin-only atomic activation (docs/api-contract.md's confirmation-phrase
	/// convention, matching <c>/candidate-content/{id}/activate</c>'s
	/// <c>{ confirmation: "ACTIVATE" }</c> shape). Supersedes any existing active
	/// baseline for the SAME execution profile -- see
	/// <see cref="IBaselineRepository.ActivateAsync"/> for the atomicity guarantee.
	/// </summary>
	[HttpPost("{id:guid}/activate")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(BaselineResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<BaselineResponse>> Activate(Guid id, [FromBody] BaselineActivationRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!string.Equals(request.Confirmation, "ACTIVATE", StringComparison.Ordinal))
		{
			throw ApiException.Validation("'confirmation' must be exactly \"ACTIVATE\".");
		}

		string activatedBy = User.GetRequiredUsername();
		BaselineActivationOutcome outcome = await _activation.ActivateAsync(id, activatedBy, cancellationToken).ConfigureAwait(false);
		return await ResolveOutcomeAsync(id, outcome, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Admin-only atomic rollback to a previously-activated (now superseded) baseline
	/// for the same execution profile (docs/api-contract.md <c>{ confirmation:
	/// "ROLLBACK" }</c>). Same underlying atomic operation as activate -- see
	/// <see cref="IBaselineRepository.RollbackAsync"/>.
	/// </summary>
	[HttpPost("{id:guid}/rollback")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(BaselineResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<BaselineResponse>> Rollback(Guid id, [FromBody] BaselineActivationRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!string.Equals(request.Confirmation, "ROLLBACK", StringComparison.Ordinal))
		{
			throw ApiException.Validation("'confirmation' must be exactly \"ROLLBACK\".");
		}

		string activatedBy = User.GetRequiredUsername();
		BaselineActivationOutcome outcome = await _activation.RollbackAsync(id, activatedBy, cancellationToken).ConfigureAwait(false);
		return await ResolveOutcomeAsync(id, outcome, cancellationToken).ConfigureAwait(false);
	}

	private async Task<ActionResult<BaselineResponse>> ResolveOutcomeAsync(Guid id, BaselineActivationOutcome outcome, CancellationToken cancellationToken)
	{
		switch (outcome)
		{
			case BaselineActivationOutcome.NotFound:
				throw ApiException.NotFound($"No baseline exists with id '{id}'.");
			case BaselineActivationOutcome.RevisionNotEligible:
				throw new ApiException(
					HttpStatusCode.Conflict, "baseline_not_ready",
					"The baseline's content revision is not eligible for activation.",
					"The referenced content revision was rejected.");
			case BaselineActivationOutcome.AlreadyActive:
			case BaselineActivationOutcome.Activated:
			default:
				Baseline? current = await _baselines.GetBaselineAsync(id, cancellationToken).ConfigureAwait(false);
				return current is null
					? throw ApiException.NotFound($"No baseline exists with id '{id}'.")
					: Ok(BaselineResponse.FromDomain(current));
		}
	}
}
