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
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.Runs;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #1062 (epic #726 sections 6/7): "An Admin can view/set the evidence
/// retention period; default six months" and "only Admin manages retention
/// administration". Both actions are Admin-only -- unlike
/// <see cref="RunsController.GetRetentionHold"/> (per-run hold status, Viewer+),
/// this is appliance-wide policy configuration, the same floor section 7 already
/// gives target/component persistent configuration and the retention-hold write
/// actions themselves.
/// </summary>
[ApiController]
[Route("api/v1/retention-policy")]
public sealed class RetentionPolicyController : ControllerBase
{
	private readonly RetentionPolicyService _policy;

	public RetentionPolicyController(RetentionPolicyService policy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		_policy = policy;
	}

	/// <summary>Reads the current evidence retention period.</summary>
	[HttpGet]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RetentionPolicyResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RetentionPolicyResponse>> Get(CancellationToken cancellationToken)
	{
		RetentionPolicy? policy = await _policy.GetAsync(cancellationToken).ConfigureAwait(false);
		if (policy is null)
		{
			// Migration 0078 seeds this row unconditionally and nothing deletes it --
			// reaching here means the singleton was removed out of band, a server-side
			// integrity problem, not a client error (matches SystemController.Get's
			// own appliance_state contract).
			throw new ApiException(HttpStatusCode.InternalServerError, "retention_policy_missing", "retention_policy row is missing.");
		}

		return Ok(Map(policy));
	}

	/// <summary>
	/// Sets the evidence retention period (AC1). <see cref="EvidenceRetentionSweepHostedService"/>
	/// re-reads this value fresh at the start of every sweep pass, so a change here
	/// takes effect on the sweep's next tick with no restart. Rejects a non-positive
	/// day count, and (issue #1109) a positive count below
	/// <see cref="RetentionPolicyService.MinimumEvidenceRetentionDays"/> -- both 400
	/// <c>validation_error</c>. Every accepted change, including a no-op that
	/// resubmits the current value, writes one <c>audit_log</c> row (issue #1109) in
	/// <see cref="RetentionPolicyRepository.SetAsync"/>, atomically with the update.
	/// </summary>
	[HttpPut]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RetentionPolicyResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RetentionPolicyResponse>> Put([FromBody] RetentionPolicyUpdateRequest? request, CancellationToken cancellationToken)
	{
		if (request?.EvidenceRetentionDays is not int days || days <= 0)
		{
			throw ApiException.Validation(
				"Retention period must be a positive number of days.",
				"Set \"evidence_retention_days\" to a positive integer in the request body.");
		}

		string actor = User.GetRequiredUsername();
		SetRetentionPolicyResult result = await _policy.SetRetentionAsync(days, actor, cancellationToken).ConfigureAwait(false);

		switch (result.Outcome)
		{
			case SetRetentionPolicyOutcome.InvalidRetentionDays:
				throw ApiException.Validation(
					"Retention period must be a positive number of days.",
					"Set \"evidence_retention_days\" to a positive integer in the request body.");
			case SetRetentionPolicyOutcome.BelowMinimum:
				// Issue #1109: below the floor, most likely a typo (a dropped digit)
				// rather than an intentional short-retention choice -- the sweep would
				// otherwise act on it immediately, with no confirmation step.
				throw ApiException.Validation(
					$"Retention period must be at least {RetentionPolicyService.MinimumEvidenceRetentionDays} days.",
					$"Set \"evidence_retention_days\" to {RetentionPolicyService.MinimumEvidenceRetentionDays} or more. If this value is intentional, contact the appliance owner -- this floor is a deliberate guard against a mistyped, near-zero retention period.");
			case SetRetentionPolicyOutcome.Updated:
			default:
				return Ok(Map(result.Policy!));
		}
	}

	private static RetentionPolicyResponse Map(RetentionPolicy policy) => new(
		EvidenceRetentionDays: policy.EvidenceRetentionDays,
		UpdatedBy: policy.UpdatedBy,
		UpdatedAt: policy.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
}
