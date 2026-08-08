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
/// The api-contract.md `/sites/{id}/targets` · `/targets/{id}` surface (issue #19,
/// epic #13): kind (`vsphere`|`nsx-api`|`ssh`), connection.host, credential_ref,
/// discovery_status, last_refreshed. Reads are Viewer+, every mutation is Admin-only
/// (same "Admin writes" gate the contract states for `/sites`). Kind is validated
/// against the closed <see cref="TargetKinds"/> set, and `connection` is rejected with
/// 400 if it names a secret-shaped key (docs/domain-model.md: "connection secrets are
/// NEVER embedded in the target, only referenced by ID").
/// </summary>
[ApiController]
public sealed class TargetsController : ControllerBase
{
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;

	public TargetsController(SiteRepository sites, TargetRepository targets)
	{
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		_sites = sites;
		_targets = targets;
	}

	[HttpGet("api/v1/sites/{siteId:guid}/targets")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(TargetResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<TargetResponse>>> ListForSite(Guid siteId, [FromQuery] PageRequest page, CancellationToken cancellationToken)
	{
		if (await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false) is null)
		{
			throw SiteNotFoundError(siteId);
		}

		(IReadOnlyList<Target> items, long totalCount) = await _targets.ListAsync(siteId, page, cancellationToken).ConfigureAwait(false);
		Response.Headers["X-Total-Count"] = totalCount.ToString(CultureInfo.InvariantCulture);
		return Ok(items.Select(TargetResponse.FromDomain).ToArray());
	}

	[HttpGet("api/v1/targets/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(TargetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<TargetResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Target? target = await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return target is null ? throw NotFoundError(id) : Ok(TargetResponse.FromDomain(target));
	}

	[HttpPost("api/v1/sites/{siteId:guid}/targets")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(TargetResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<TargetResponse>> Create(Guid siteId, [FromBody] TargetCreateBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (string.IsNullOrWhiteSpace(request.Name))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'name' is required.");
		}

		if (!TargetKinds.IsValid(request.Kind))
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "invalid_kind",
				$"'kind' must be one of: {string.Join(", ", TargetKinds.All)}.");
		}

		string? forbiddenKey = TargetConnectionValidator.FindForbiddenKey(request.Connection);
		if (forbiddenKey is not null)
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "secret_in_connection",
				$"'connection' must not carry secret material; found forbidden key '{forbiddenKey}'. Reference a stored credential via 'credential_ref' instead.");
		}

		string connectionJson = request.Connection?.GetRawText() ?? "{}";

		(TargetWriteOutcome outcome, Guid? id) = await _targets
			.CreateAsync(siteId, request.Kind!, request.Name, connectionJson, request.CredentialId, cancellationToken)
			.ConfigureAwait(false);

		switch (outcome)
		{
			case TargetWriteOutcome.SiteNotFound:
				throw SiteNotFoundError(siteId);
			case TargetWriteOutcome.NameTaken:
				throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A target named '{request.Name}' already exists under this site.");
			case TargetWriteOutcome.CredentialNotFound:
				throw new ApiException(HttpStatusCode.BadRequest, "credential_not_found", $"No credential exists with id '{request.CredentialId}'.");
		}

		TargetResponse created = TargetResponse.FromDomain((await _targets.GetAsync(id!.Value, cancellationToken).ConfigureAwait(false))!);
		return CreatedAtAction(nameof(Get), new { id = id.Value }, created);
	}

	[HttpPut("api/v1/targets/{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(TargetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<TargetResponse>> Update(Guid id, [FromBody] TargetUpdateBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Kind is not null && !TargetKinds.IsValid(request.Kind))
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "invalid_kind",
				$"'kind' must be one of: {string.Join(", ", TargetKinds.All)}.");
		}

		string? forbiddenKey = TargetConnectionValidator.FindForbiddenKey(request.Connection);
		if (forbiddenKey is not null)
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "secret_in_connection",
				$"'connection' must not carry secret material; found forbidden key '{forbiddenKey}'. Reference a stored credential via 'credential_ref' instead.");
		}

		string? connectionJson = request.Connection?.GetRawText();

		TargetWriteOutcome outcome = await _targets
			.UpdateAsync(id, request.Kind, request.Name, connectionJson, request.CredentialId, request.ClearCredential, cancellationToken)
			.ConfigureAwait(false);

		switch (outcome)
		{
			case TargetWriteOutcome.NotFound:
				throw NotFoundError(id);
			case TargetWriteOutcome.NameTaken:
				throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A target named '{request.Name}' already exists under this site.");
			case TargetWriteOutcome.CredentialNotFound:
				throw new ApiException(HttpStatusCode.BadRequest, "credential_not_found", $"No credential exists with id '{request.CredentialId}'.");
		}

		return Ok(TargetResponse.FromDomain((await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false))!));
	}

	[HttpDelete("api/v1/targets/{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		return await _targets.DeleteAsync(id, cancellationToken).ConfigureAwait(false) switch
		{
			TargetDeleteOutcome.Deleted => NoContent(),
			_ => throw NotFoundError(id),
		};
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No target exists with id '{id}'.");

	private static ApiException SiteNotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No site exists with id '{id}'.");
}
