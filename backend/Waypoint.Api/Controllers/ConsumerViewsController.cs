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
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #1464 (epic #1183, split from design record #1162's closing comment):
/// ConsumerView CRUD -- named operator-defined ESX platform-set views. Admin-only for
/// every verb, INCLUDING read (decision R2-10: "serving/auth dials" are Admin --
/// matches <c>RepoCredentialsController</c>'s own stricter-than-catalog-reads floor),
/// unlike <c>EsxAcquisitionController</c>'s Viewer-readable subscriptions. No
/// generation or serving logic reads this model yet (Children B/D, out of scope).
/// </summary>
[ApiController]
[Route("api/v1/consumer-views")]
public sealed class ConsumerViewsController : ControllerBase
{
	private readonly IConsumerViewRepository _views;

	public ConsumerViewsController(IConsumerViewRepository views)
	{
		ArgumentNullException.ThrowIfNull(views);
		_views = views;
	}

	[HttpGet]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ConsumerViewResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ConsumerViewResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<ConsumerView> items = await _views.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(items.Select(ConsumerViewResponse.FromDomain).ToArray());
	}

	[HttpGet("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ConsumerViewResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ConsumerViewResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		ConsumerView? view = await _views.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (view is null)
		{
			throw ApiException.NotFound("Consumer view not found.", $"Consumer view '{id}' does not exist.");
		}

		return Ok(ConsumerViewResponse.FromDomain(view));
	}

	/// <summary>
	/// Creates a view. Every requested platform key must be a member of the static
	/// <see cref="ConsumerViewPlatformVocabulary"/> (issue #1464 AC) -- an unrecognized
	/// key is a 400, not a silently accepted value. <c>is_default: true</c> is
	/// rejected with 409 when a different view already holds the default (issue #1464
	/// AC "exactly one default" -- checked here AND enforced at the database by
	/// migration 0131's partial unique index; a same-request race lands on the
	/// database's own 23505 via <see cref="ConsumerViewDefaultConflictException"/>).
	/// </summary>
	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ConsumerViewResponse), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<ConsumerViewResponse>> Create(CreateConsumerViewRequest request, CancellationToken cancellationToken)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Name))
		{
			throw ApiException.Validation("A view name is required.");
		}

		IReadOnlyList<string> platforms = request.Platforms ?? [];
		ValidateAgainstVocabulary(platforms);

		bool isDefault = request.IsDefault ?? false;
		if (isDefault)
		{
			await EnsureNoOtherDefaultAsync(currentId: null, cancellationToken).ConfigureAwait(false);
		}

		ConsumerView created;
		try
		{
			created = await _views.CreateAsync(request.Name, platforms, isDefault, cancellationToken).ConfigureAwait(false);
		}
		catch (ConsumerViewNameConflictException ex)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", ex.Message);
		}
		catch (ConsumerViewDefaultConflictException ex)
		{
			throw new ApiException(HttpStatusCode.Conflict, "default_already_set", ex.Message);
		}

		ConsumerViewResponse response = ConsumerViewResponse.FromDomain(created);
		return CreatedAtAction(nameof(Get), new { id = created.Id }, response);
	}

	/// <summary>
	/// Partial update, same leave-unspecified-columns-alone convention as
	/// <see cref="EsxAcquisitionController.UpdateSubscription"/>. Setting
	/// <c>is_default: true</c> is rejected with 409 unless this row is already the
	/// default (issue #1464 AC).
	/// </summary>
	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ConsumerViewResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<ConsumerViewResponse>> Update(Guid id, UpdateConsumerViewRequest request, CancellationToken cancellationToken)
	{
		if (request is null)
		{
			throw ApiException.Validation("A request body is required.");
		}

		if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
		{
			throw ApiException.Validation("View name cannot be blank.");
		}

		if (request.Platforms is not null)
		{
			ValidateAgainstVocabulary(request.Platforms);
		}

		if (request.IsDefault == true)
		{
			await EnsureNoOtherDefaultAsync(currentId: id, cancellationToken).ConfigureAwait(false);
		}

		ConsumerView? updated;
		try
		{
			updated = await _views
				.UpdateAsync(id, request.Name, request.Platforms, request.IsDefault, cancellationToken).ConfigureAwait(false);
		}
		catch (ConsumerViewNameConflictException ex)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", ex.Message);
		}
		catch (ConsumerViewDefaultConflictException ex)
		{
			throw new ApiException(HttpStatusCode.Conflict, "default_already_set", ex.Message);
		}

		if (updated is null)
		{
			throw ApiException.NotFound("Consumer view not found.", $"Consumer view '{id}' does not exist.");
		}

		return Ok(ConsumerViewResponse.FromDomain(updated));
	}

	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		bool deleted = await _views.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
		if (!deleted)
		{
			throw ApiException.NotFound("Consumer view not found.", $"Consumer view '{id}' does not exist.");
		}

		return NoContent();
	}

	private static void ValidateAgainstVocabulary(IReadOnlyList<string> platforms)
	{
		if (platforms.Count == 0)
		{
			return;
		}

		HashSet<string> valid = new(ConsumerViewPlatformVocabulary.All, StringComparer.Ordinal);
		string[] unknown = [.. platforms.Where(platform => !valid.Contains(platform)).Distinct(StringComparer.Ordinal)];
		if (unknown.Length > 0)
		{
			throw ApiException.Validation(
				"One or more platform keys are not in the known platform vocabulary.",
				$"Unknown platform key(s): {string.Join(", ", unknown)}.");
		}
	}

	/// <summary>
	/// API-layer half of the exactly-one-default guarantee (issue #1464 AC): 409 when
	/// a different row already has <c>is_default = true</c>. <paramref name="currentId"/>
	/// is null on create (any existing default is a conflict) or the row's own id on
	/// update (that row itself is not a conflict with itself).
	/// </summary>
	private async Task EnsureNoOtherDefaultAsync(Guid? currentId, CancellationToken cancellationToken)
	{
		IReadOnlyList<ConsumerView> views = await _views.ListAsync(cancellationToken).ConfigureAwait(false);
		bool otherDefaultExists = views.Any(view => view.IsDefault && view.Id != currentId);
		if (otherDefaultExists)
		{
			throw new ApiException(
				HttpStatusCode.Conflict, "default_already_set",
				"Another consumer view is already marked as the default. Unset it before marking a new default.");
		}
	}
}
