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
using Waypoint.Core.Pagination;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/sites` surface (issue #19, epic #13): "Site: name,
/// description, stigman_override?", "Admin writes". Reads are Viewer+, every mutation
/// is Admin-only, matching the credentials/catalog controllers' role-gate shape.
/// </summary>
[ApiController]
[Route("api/v1/sites")]
public sealed class SitesController : ControllerBase
{
	private readonly SiteRepository _sites;

	public SitesController(SiteRepository sites)
	{
		ArgumentNullException.ThrowIfNull(sites);
		_sites = sites;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(SiteResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<SiteResponse>>> List([FromQuery] PageRequest page, CancellationToken cancellationToken)
	{
		(IReadOnlyList<Site> items, long totalCount) = await _sites.ListAsync(page, cancellationToken).ConfigureAwait(false);
		Response.Headers["X-Total-Count"] = totalCount.ToString(CultureInfo.InvariantCulture);
		return Ok(items.Select(SiteResponse.FromDomain).ToArray());
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(SiteResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<SiteResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Site? site = await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return site is null ? throw NotFoundError(id) : Ok(SiteResponse.FromDomain(site));
	}

	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(SiteResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<SiteResponse>> Create([FromBody] SiteCreateBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.Name))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'name' is required.");
		}

		Guid? id = await _sites.CreateAsync(request.Name, request.Description, request.StigmanOverride, cancellationToken).ConfigureAwait(false);
		if (id is not Guid createdId)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A site named '{request.Name}' already exists.");
		}

		SiteResponse created = SiteResponse.FromDomain((await _sites.GetAsync(createdId, cancellationToken).ConfigureAwait(false))!);
		return CreatedAtAction(nameof(Get), new { id = createdId }, created);
	}

	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(SiteResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<SiteResponse>> Update(Guid id, [FromBody] SiteUpdateBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		SiteWriteOutcome outcome = await _sites.UpdateAsync(
			id, request.Name, request.Description, request.StigmanOverride,
			request.ClearDescription, request.ClearStigmanOverride, cancellationToken).ConfigureAwait(false);

		switch (outcome)
		{
			case SiteWriteOutcome.NotFound:
				throw NotFoundError(id);
			case SiteWriteOutcome.NameTaken:
				throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A site named '{request.Name}' already exists.");
		}

		return Ok(SiteResponse.FromDomain((await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false))!));
	}

	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force, CancellationToken cancellationToken)
	{
		return await _sites.DeleteAsync(id, force, cancellationToken).ConfigureAwait(false) switch
		{
			SiteDeleteOutcome.Deleted => NoContent(),
			SiteDeleteOutcome.InUse => throw new ApiException(
				HttpStatusCode.Conflict, "site_in_use",
				"This site still has targets. Delete them first, or pass ?force=true to cascade-delete the site's targets."),
			_ => throw NotFoundError(id),
		};
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No site exists with id '{id}'.");
}
