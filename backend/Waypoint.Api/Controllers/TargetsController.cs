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
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/sites/{id}/targets` · `/targets/{id}` surface (issue #19,
/// epic #13): kind (`vsphere`|`nsx-api`|`ssh`), connection.host, credential_ref,
/// discovery_status, last_refreshed, bindings. Reads are Viewer+, every mutation is
/// Admin-only (same "Admin writes" gate the contract states for `/sites`). Kind is
/// validated against the closed <see cref="TargetKinds"/> set, and `connection` is
/// rejected with 400 if it names a secret-shaped key (docs/domain-model.md:
/// "connection secrets are NEVER embedded in the target, only referenced by ID").
///
/// Issue #584 (epic #582, ADR-0021) adds the purpose-specific credential binding
/// surface (`/targets/{id}/credential-bindings`) alongside the legacy
/// `credential_ref` field -- see <see cref="TargetResponse"/>'s doc comment for the
/// deprecation note and migration 0043 for the dual-write contract that keeps both
/// representations consistent.
/// </summary>
[ApiController]
public sealed class TargetsController : ControllerBase
{
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;
	private readonly TargetCredentialBindingRepository _bindings;
	private readonly CredentialRepository _credentials;

	public TargetsController(SiteRepository sites, TargetRepository targets, TargetCredentialBindingRepository bindings, CredentialRepository credentials)
	{
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(bindings);
		ArgumentNullException.ThrowIfNull(credentials);
		_sites = sites;
		_targets = targets;
		_bindings = bindings;
		_credentials = credentials;
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

		IReadOnlyDictionary<Guid, IReadOnlyList<TargetCredentialBinding>> bindingsByTarget = await _bindings
			.ListForTargetsAsync(items.Select(t => t.Id).ToArray(), cancellationToken).ConfigureAwait(false);
		return Ok(await Task.WhenAll(items.Select(t => ToResponseAsync(t, bindingsByTarget.GetValueOrDefault(t.Id, []), cancellationToken))).ConfigureAwait(false));
	}

	[HttpGet("api/v1/targets/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(TargetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<TargetResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Target? target = await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			throw NotFoundError(id);
		}

		return Ok(await ToResponseAsync(target, await _bindings.ListForTargetAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
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

		Target created = (await _targets.GetAsync(id!.Value, cancellationToken).ConfigureAwait(false))!;
		TargetResponse response = await ToResponseAsync(created, await _bindings.ListForTargetAsync(id.Value, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
		return CreatedAtAction(nameof(Get), new { id = id.Value }, response);
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

		Target updated = (await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(await ToResponseAsync(updated, await _bindings.ListForTargetAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
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

	/// <summary>
	/// Issue #584: sets (creates or replaces/overrides, ADR-0021 §4) the binding for
	/// one <c>(target, purpose)</c> pair. Rejects an invalid/inapplicable purpose or an
	/// incompatible credential type with a machine-readable code rather than a bare
	/// foreign-key failure, matching every other validated-write endpoint's shape.
	/// </summary>
	[HttpPut("api/v1/targets/{id:guid}/credential-bindings/{purpose}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(TargetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<TargetResponse>> SetBinding(Guid id, string purpose, [FromBody] TargetCredentialBindingSetBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.CredentialId is not { } credentialId)
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'credential_ref' is required.");
		}

		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(id, purpose, credentialId, cancellationToken).ConfigureAwait(false);
		ThrowIfBindingWriteFailed(id, purpose, credentialId, outcome);

		Target target = (await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(await ToResponseAsync(target, await _bindings.ListForTargetAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
	}

	/// <summary>Issue #584: clears (removes) the binding for one <c>(target, purpose)</c> pair, if present.</summary>
	[HttpDelete("api/v1/targets/{id:guid}/credential-bindings/{purpose}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(TargetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<TargetResponse>> ClearBinding(Guid id, string purpose, CancellationToken cancellationToken)
	{
		if (await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
		{
			throw NotFoundError(id);
		}

		TargetCredentialBindingDeleteOutcome outcome = await _bindings.ClearAsync(id, purpose, cancellationToken).ConfigureAwait(false);
		if (outcome == TargetCredentialBindingDeleteOutcome.NotFound)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No '{purpose}' credential binding exists for target '{id}'.");
		}

		Target target = (await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(await ToResponseAsync(target, await _bindings.ListForTargetAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
	}

	private static void ThrowIfBindingWriteFailed(Guid targetId, string purpose, Guid credentialId, TargetCredentialBindingWriteOutcome outcome)
	{
		switch (outcome)
		{
			case TargetCredentialBindingWriteOutcome.TargetNotFound:
				throw NotFoundError(targetId);
			case TargetCredentialBindingWriteOutcome.CredentialNotFound:
				throw new ApiException(HttpStatusCode.BadRequest, "credential_not_found", $"No credential exists with id '{credentialId}'.");
			case TargetCredentialBindingWriteOutcome.InvalidPurpose:
				throw new ApiException(
					HttpStatusCode.BadRequest, "invalid_purpose",
					$"'{purpose}' is not a recognized credential purpose. Must be one of: {string.Join(", ", CredentialPurposes.All)}.");
			case TargetCredentialBindingWriteOutcome.PurposeNotApplicable:
				throw new ApiException(
					HttpStatusCode.BadRequest, "purpose_not_applicable",
					$"'{purpose}' does not apply to this target's kind.");
			case TargetCredentialBindingWriteOutcome.IncompatibleCredentialType:
				throw new ApiException(
					HttpStatusCode.BadRequest, "incompatible_credential_type",
					$"The credential's type does not satisfy the '{purpose}' purpose.");
		}
	}

	/// <summary>Enriches each binding with the referenced credential's display fields (name/type -- never secret material) for the response; falls back to null if the credential has since been deleted (should not happen given the RESTRICT FK, but defensive).</summary>
	private async Task<TargetResponse> ToResponseAsync(Target target, IReadOnlyList<TargetCredentialBinding> bindings, CancellationToken cancellationToken)
	{
		List<TargetCredentialBindingResponse> enriched = [];
		foreach (TargetCredentialBinding binding in bindings)
		{
			CredentialResponse? credential = await _credentials.GetAsync(binding.CredentialId, cancellationToken).ConfigureAwait(false);
			enriched.Add(TargetCredentialBindingResponse.FromDomain(binding, credential?.Name, credential?.CredentialType));
		}

		return TargetResponse.FromDomain(target) with { Bindings = enriched };
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No target exists with id '{id}'.");

	private static ApiException SiteNotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No site exists with id '{id}'.");
}
