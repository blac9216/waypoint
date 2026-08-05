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
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Secrets;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md credentials surface (epic #8 slice 3): metadata out, secret
/// material in only. Every response type is <see cref="CredentialResponse"/>, which
/// has no secret field to leak -- write-only is enforced by the shape of the DTO,
/// not by masking (security.md control 3). Secret writes are audited by the store
/// and stamp <c>rotated_at</c>.
/// </summary>
[ApiController]
[Route("api/v1/credentials")]
public sealed class CredentialsController : ControllerBase
{
	private readonly CredentialRepository _credentials;
	private readonly ICredentialSecretStore _secrets;

	public CredentialsController(CredentialRepository credentials, ICredentialSecretStore secrets)
	{
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(secrets);
		_credentials = credentials;
		_secrets = secrets;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CredentialResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<CredentialResponse>>> List(CancellationToken cancellationToken)
	{
		return Ok(await _credentials.ListAsync(cancellationToken));
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CredentialResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<CredentialResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		CredentialResponse? credential = await _credentials.GetAsync(id, cancellationToken);
		return credential is null ? throw NotFoundError(id) : Ok(credential);
	}

	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CredentialResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<CredentialResponse>> Create([FromBody] CredentialCreateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.CredentialType))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "Both 'name' and 'credential_type' are required.");
		}

		Guid? id = await _credentials.CreateAsync(request.Name, request.CredentialType, request.Owner ?? "shared", cancellationToken);
		if (id is not Guid createdId)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A credential named '{request.Name}' already exists.");
		}

		if (!string.IsNullOrEmpty(request.Secret))
		{
			await StoreSecretAsync(createdId, request.Secret, cancellationToken);
		}

		CredentialResponse created = (await _credentials.GetAsync(createdId, cancellationToken))!;
		return CreatedAtAction(nameof(Get), new { id = createdId }, created);
	}

	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CredentialResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<CredentialResponse>> Update(Guid id, [FromBody] CredentialUpdateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (await _credentials.GetAsync(id, cancellationToken) is null)
		{
			throw NotFoundError(id);
		}

		if (!string.IsNullOrWhiteSpace(request.Name) && !await _credentials.RenameAsync(id, request.Name, cancellationToken))
		{
			throw NotFoundError(id);
		}

		if (!string.IsNullOrEmpty(request.Secret))
		{
			await StoreSecretAsync(id, request.Secret, cancellationToken);
		}

		return Ok((await _credentials.GetAsync(id, cancellationToken))!);
	}

	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		string actor = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";
		return await _credentials.DeleteAsync(id, actor, cancellationToken) ? NoContent() : throw NotFoundError(id);
	}

	/// <summary>Secret write + rotation stamp. The store audits with the caller's identity; the value never lands in any response.</summary>
	private async Task StoreSecretAsync(Guid id, string secret, CancellationToken cancellationToken)
	{
		string actor = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";
		byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
		try
		{
			await _secrets.StoreAsync(id, secretBytes, actor, cancellationToken);
		}
		finally
		{
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(secretBytes);
		}

		await _credentials.StampRotatedAsync(id, cancellationToken);
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No credential exists with id '{id}'.");
}
