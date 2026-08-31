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
using Waypoint.Core.Errors;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Secrets;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Repo store -&gt; credential binding CRUD (issue #1517, split from design record
/// #1043, migration 0103). This controller owns ONLY the store-binding record --
/// creating/rotating/deleting the underlying <see cref="CredentialTypes.RepoBasicAuth"/>
/// credential itself is the EXISTING <see cref="CredentialsController"/> surface
/// (POST/PUT/DELETE/test /api/v1/credentials), reused unmodified per issue #1517's own
/// "no new rotation mechanism" AC. The sibling B child (#1510, blocked on #1608) owns
/// the nginx-side enforcement that consumes this binding; the sibling D child (#1525)
/// owns the admin UI. Same write-only-secret contract as
/// <see cref="CredentialsController"/>: every response here carries a credential
/// reference and display fields only, never secret material.
/// </summary>
[ApiController]
[Route("api/v1/repo-credentials")]
public sealed class RepoCredentialsController : ControllerBase
{
	private readonly RepoCredentialBindingRepository _bindings;
	private readonly CredentialRepository _credentials;

	public RepoCredentialsController(RepoCredentialBindingRepository bindings, CredentialRepository credentials)
	{
		ArgumentNullException.ThrowIfNull(bindings);
		ArgumentNullException.ThrowIfNull(credentials);
		_bindings = bindings;
		_credentials = credentials;
	}

	/// <summary>
	/// Every repo store's current binding. A store with no binding yet is simply
	/// absent from the list -- not a null/placeholder entry.
	///
	/// <see cref="RequireAdminRoleAttribute"/>, not the
	/// <see cref="RequireViewerRoleAttribute"/> <see cref="TargetsController"/>'s own
	/// binding reads use: issue #1517's AC is explicit that a non-Admin cannot
	/// "create, read, or rotate a repo-serving credential" -- stricter than the
	/// generic credential surface's Viewer-readable metadata.
	/// </summary>
	[HttpGet]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RepoCredentialBindingResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<RepoCredentialBindingResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<RepoCredentialBinding> bindings = await _bindings.ListAsync(cancellationToken).ConfigureAwait(false);
		List<RepoCredentialBindingResponse> responses = [];
		foreach (RepoCredentialBinding binding in bindings)
		{
			responses.Add(await ToResponseAsync(binding, cancellationToken).ConfigureAwait(false));
		}

		return Ok(responses);
	}

	/// <summary>The binding for one store. 404 when that store's own name is not one of <see cref="RepoStores"/>'s closed set, or when it is valid but has no binding yet -- both read as "no binding here," the distinction is not meaningful to a caller of this endpoint.</summary>
	[HttpGet("{store}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RepoCredentialBindingResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RepoCredentialBindingResponse>> Get(string store, CancellationToken cancellationToken)
	{
		RepoCredentialBinding? binding = await _bindings.GetAsync(store, cancellationToken).ConfigureAwait(false);
		return binding is null ? throw NotFoundError(store) : Ok(await ToResponseAsync(binding, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Sets (creates or replaces/overrides) the binding for one store. Rejects an
	/// unrecognized store, an unknown credential, or a credential whose type is not
	/// <see cref="CredentialTypes.RepoBasicAuth"/> with a machine-readable code, the
	/// same shape <see cref="TargetsController.SetBinding"/> uses.
	/// </summary>
	[HttpPut("{store}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RepoCredentialBindingResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RepoCredentialBindingResponse>> Set(string store, [FromBody] RepoCredentialBindingSetBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.CredentialId is not { } credentialId)
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'credential_ref' is required.");
		}

		RepoCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(store, credentialId, cancellationToken).ConfigureAwait(false);
		ThrowIfWriteFailed(store, credentialId, outcome);

		RepoCredentialBinding binding = (await _bindings.GetAsync(store, cancellationToken).ConfigureAwait(false))!;
		return Ok(await ToResponseAsync(binding, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>Clears (removes) the binding for one store, if present.</summary>
	[HttpDelete("{store}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Clear(string store, CancellationToken cancellationToken)
	{
		RepoCredentialBindingDeleteOutcome outcome = await _bindings.ClearAsync(store, cancellationToken).ConfigureAwait(false);
		return outcome == RepoCredentialBindingDeleteOutcome.NotFound ? throw NotFoundError(store) : NoContent();
	}

	private static void ThrowIfWriteFailed(string store, Guid credentialId, RepoCredentialBindingWriteOutcome outcome)
	{
		switch (outcome)
		{
			case RepoCredentialBindingWriteOutcome.InvalidStore:
				throw new ApiException(
					HttpStatusCode.BadRequest, "invalid_store",
					$"'{store}' is not a recognized repo store. Must be one of: {string.Join(", ", RepoStores.All)}.");
			case RepoCredentialBindingWriteOutcome.CredentialNotFound:
				throw new ApiException(HttpStatusCode.BadRequest, "credential_not_found", $"No credential exists with id '{credentialId}'.");
			case RepoCredentialBindingWriteOutcome.IncompatibleCredentialType:
				throw new ApiException(
					HttpStatusCode.BadRequest, "incompatible_credential_type",
					$"The credential's type must be '{CredentialTypes.RepoBasicAuth}' to bind a repo store.");
		}
	}

	/// <summary>Enriches the binding with the referenced credential's display name (never secret material) for the response; falls back to null if the credential has since been deleted (should not happen given the RESTRICT FK, but defensive, same as <see cref="TargetsController"/>'s own binding enrichment).</summary>
	private async Task<RepoCredentialBindingResponse> ToResponseAsync(RepoCredentialBinding binding, CancellationToken cancellationToken)
	{
		CredentialResponse? credential = await _credentials.GetAsync(binding.CredentialId, cancellationToken).ConfigureAwait(false);
		return RepoCredentialBindingResponse.FromDomain(binding, credential?.Name);
	}

	private static ApiException NotFoundError(string store) =>
		new(HttpStatusCode.NotFound, "not_found", $"No repo credential binding exists for store '{store}'.");
}
