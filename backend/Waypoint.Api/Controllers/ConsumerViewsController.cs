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
	/// key is a 400, not a silently accepted value. <c>is_default: true</c> MOVES the
	/// default onto the new view: the repository demotes the incumbent default and
	/// inserts this row already-default inside one transaction, so the "exactly one
	/// default at any time" invariant (issue #1464 AC 2) holds throughout and a caller
	/// never has to unset the old default first. Migration 0131's partial unique index
	/// still backstops it: a writer that bypasses the repository's transfer lock and
	/// races in a second default lands on the database's own 23505, surfaced as 409
	/// <c>default_already_set</c> via <see cref="ConsumerViewDefaultConflictException"/>.
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
	/// <c>is_default: true</c> is the supported way to MOVE the default: the repository
	/// demotes whichever row currently holds it and promotes this one inside a single
	/// transaction, so the move is atomic and needs no prior unset (200, not 409).
	/// Explicitly setting <c>is_default: false</c> on the sole default row is still
	/// rejected with 409 <c>default_required</c>, and so is deleting it -- those really
	/// would leave zero defaults, whereas a move never does (issue #1464 AC 2 "exactly
	/// one default at any time": the default can be moved, never cleared outright).
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
		catch (ConsumerViewSoleDefaultException ex)
		{
			throw new ApiException(HttpStatusCode.Conflict, "default_required", ex.Message);
		}

		if (updated is null)
		{
			throw ApiException.NotFound("Consumer view not found.", $"Consumer view '{id}' does not exist.");
		}

		return Ok(ConsumerViewResponse.FromDomain(updated));
	}

	/// <summary>
	/// Deletes a view. The view currently holding the default can never be deleted (409
	/// <c>default_required</c>, issue #1464 AC "exactly one default at any time") --
	/// mark a different view as the default first (a single
	/// <c>PUT {"is_default": true}</c> on that other view, which moves the default
	/// atomically), then delete this one.
	/// </summary>
	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		bool deleted;
		try
		{
			deleted = await _views.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
		}
		catch (ConsumerViewSoleDefaultException ex)
		{
			throw new ApiException(HttpStatusCode.Conflict, "default_required", ex.Message);
		}

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
}
