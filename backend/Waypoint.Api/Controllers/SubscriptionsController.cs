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
using Waypoint.Core.Errors;
using Waypoint.Core.Subscriptions;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #1450 (epic #1182, split from design record #1045): subscription CRUD +
/// adopt-a-preset, over #1421's domain model. RBAC decision R2-10: Viewer+ reads,
/// Admin-only writes -- matching <see cref="RetentionPolicyController"/>'s own
/// Admin-write shape. Deliberately never enqueues a download or evaluation job on
/// create/update/adopt (#1046's evaluation job's own job, not this one's).
/// </summary>
[ApiController]
[Route("api/v1/subscriptions")]
public sealed class SubscriptionsController : ControllerBase
{
	private readonly SubscriptionService _service;

	public SubscriptionsController(SubscriptionService service)
	{
		ArgumentNullException.ThrowIfNull(service);
		_service = service;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(IReadOnlyList<SubscriptionResponse>), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<SubscriptionResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<Subscription> subscriptions = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(subscriptions.Select(Map).ToList());
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<SubscriptionResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Subscription? subscription = await _service.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (subscription is null)
		{
			throw ApiException.NotFound($"Subscription '{id}' was not found.");
		}
		return Ok(Map(subscription));
	}

	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<SubscriptionResponse>> Create([FromBody] SubscriptionWriteRequest? request, CancellationToken cancellationToken)
	{
		if (request is null)
		{
			throw ApiException.Validation("A request body is required.");
		}

		SubscriptionWriteResult result = await _service.CreateAsync(ToFields(request), cancellationToken).ConfigureAwait(false);
		return result.Outcome switch
		{
			SubscriptionWriteOutcome.Ok => CreatedAtAction(nameof(Get), new { id = result.Subscription!.Id }, Map(result.Subscription)),
			SubscriptionWriteOutcome.PresetNotFound => throw ApiException.NotFound(result.Error!),
			_ => throw ApiException.Validation(result.Error!),
		};
	}

	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(SubscriptionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<SubscriptionResponse>> Update(Guid id, [FromBody] SubscriptionWriteRequest? request, CancellationToken cancellationToken)
	{
		if (request is null)
		{
			throw ApiException.Validation("A request body is required.");
		}

		SubscriptionWriteResult result = await _service.UpdateAsync(id, ToFields(request), cancellationToken).ConfigureAwait(false);
		return result.Outcome switch
		{
			SubscriptionWriteOutcome.Ok => Ok(Map(result.Subscription!)),
			SubscriptionWriteOutcome.NotFound => throw ApiException.NotFound(result.Error!),
			SubscriptionWriteOutcome.PresetNotFound => throw ApiException.NotFound(result.Error!),
			_ => throw ApiException.Validation(result.Error!),
		};
	}

	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		bool deleted = await _service.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
		if (!deleted)
		{
			throw ApiException.NotFound($"Subscription '{id}' was not found.");
		}
		return NoContent();
	}

	private static SubscriptionWriteFields ToFields(SubscriptionWriteRequest request) => new(
		request.Product, request.Lane, request.LineGranularity, request.AnchorVersion,
		request.PresetId, request.RefreshWindowDays, request.RetentionOverrideDays, request.IsEnabled);

	private static SubscriptionResponse Map(Subscription subscription) => new(
		Id: subscription.Id,
		Product: subscription.Product,
		Lane: subscription.Lane,
		LineGranularity: SubscriptionLineGranularityValues.ToDbValue(subscription.LineGranularity),
		AnchorVersion: subscription.AnchorVersion,
		PresetId: subscription.PresetId,
		RefreshWindowDays: subscription.RefreshWindowDays,
		RetentionOverrideDays: subscription.RetentionOverrideDays,
		IsEnabled: subscription.IsEnabled,
		CreatedAt: subscription.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
		UpdatedAt: subscription.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
}
