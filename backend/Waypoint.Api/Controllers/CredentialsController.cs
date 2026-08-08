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

		if (!CredentialTypes.IsValid(request.CredentialType))
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "invalid_credential_type",
				$"'credential_type' must be one of: {string.Join(", ", CredentialTypes.All)}.");
		}

		string owner = request.Owner ?? CredentialOwners.Shared;
		if (!CredentialOwners.All.Contains(owner))
		{
			// ADR-0011: shared/service credentials only in v1 -- there is no
			// personal-credential row to create, so any other owner value is
			// rejected rather than silently coerced to 'shared'.
			throw new ApiException(
				HttpStatusCode.BadRequest, "invalid_owner",
				$"'owner' must be one of: {string.Join(", ", CredentialOwners.All)} (ADR-0011: no personal credentials in v1).");
		}

		bool sudoEnabled = request.SudoEnabled ?? false;
		if (sudoEnabled && request.CredentialType != CredentialTypes.Ssh)
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "sudo_requires_ssh",
				"'sudo_enabled' is only meaningful for credential_type 'ssh'.");
		}

		Guid? id = await _credentials.CreateAsync(request.Name, request.CredentialType, owner, sudoEnabled, cancellationToken, request.Username);
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
		CredentialResponse? existing = await _credentials.GetAsync(id, cancellationToken);
		if (existing is null)
		{
			throw NotFoundError(id);
		}

		if (!string.IsNullOrWhiteSpace(request.Name))
		{
			switch (await _credentials.RenameAsync(id, request.Name, cancellationToken))
			{
				case CredentialWriteOutcome.NotFound:
					throw NotFoundError(id);
				case CredentialWriteOutcome.NameTaken:
					throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A credential named '{request.Name}' already exists.");
			}
		}

		if (request.SudoEnabled is bool sudoEnabled)
		{
			if (sudoEnabled && existing.CredentialType != CredentialTypes.Ssh)
			{
				throw new ApiException(
					HttpStatusCode.BadRequest, "sudo_requires_ssh",
					"'sudo_enabled' is only meaningful for credential_type 'ssh'.");
			}

			if (await _credentials.UpdateSudoAsync(id, sudoEnabled, cancellationToken) == CredentialWriteOutcome.NotFound)
			{
				throw NotFoundError(id);
			}
		}

		if (request.Username is not null)
		{
			// Empty string clears the username (same convention PUT semantics use
			// elsewhere in this controller for optional fields); only a JSON-absent
			// property leaves it untouched.
			string? newUsername = string.IsNullOrEmpty(request.Username) ? null : request.Username;
			if (await _credentials.UpdateUsernameAsync(id, newUsername, cancellationToken) == CredentialWriteOutcome.NotFound)
			{
				throw NotFoundError(id);
			}
		}

		if (!string.IsNullOrEmpty(request.Secret))
		{
			await StoreSecretAsync(id, request.Secret, cancellationToken);
		}

		return Ok((await _credentials.GetAsync(id, cancellationToken))!);
	}

	/// <summary>
	/// Issue #20 minimal test: decrypts the stored secret under the caller's identity
	/// (audited by <see cref="ICredentialSecretStore.DecryptAsync"/> exactly like any
	/// other decrypt) and, if that succeeds, marks the credential
	/// <see cref="CredentialHealthStates.Valid"/>; a missing secret or decrypt failure
	/// marks it <see cref="CredentialHealthStates.AuthFailing"/>. This does NOT dial
	/// the target -- see <see cref="CredentialTestResponse"/>'s doc comment for why the
	/// real connectivity test is a follow-up PowerShell job.
	/// </summary>
	[HttpPost("{id:guid}/test")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CredentialTestResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<CredentialTestResponse>> Test(Guid id, CancellationToken cancellationToken)
	{
		if (await _credentials.GetAsync(id, cancellationToken) is null)
		{
			throw NotFoundError(id);
		}

		string actor = User.GetRequiredUsername();
		bool succeeded;
		string message;
		try
		{
			using DecryptedSecret decrypted = await _secrets.DecryptAsync(id, actor, jobId: null, runId: null, cancellationToken);
			succeeded = true;
			message = "Stored secret decrypted successfully. This checks the secret is present and readable; it does not verify connectivity to the target.";
		}
		catch (CredentialSecretNotFoundException)
		{
			succeeded = false;
			message = "No secret is stored for this credential.";
		}
		catch (MasterKeyUnavailableException)
		{
			succeeded = false;
			message = "The appliance master key is unavailable; the secret could not be decrypted.";
		}

		await _credentials.MarkTestOutcomeAsync(id, succeeded, cancellationToken);
		string health = succeeded ? CredentialHealthStates.Valid : CredentialHealthStates.AuthFailing;
		return Ok(new CredentialTestResponse(id, succeeded, health, message));
	}

	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		string actor = User.GetRequiredUsername();
		return await _credentials.DeleteAsync(id, actor, cancellationToken) switch
		{
			CredentialDeleteOutcome.Deleted => NoContent(),
			CredentialDeleteOutcome.InUse => throw new ApiException(
				HttpStatusCode.Conflict, "credential_in_use",
				"Jobs or runs still reference this credential; their history must be removed before it can be deleted."),
			_ => throw NotFoundError(id),
		};
	}

	/// <summary>Secret write + rotation stamp. The store audits with the caller's identity; the value never lands in any response.</summary>
	private async Task StoreSecretAsync(Guid id, string secret, CancellationToken cancellationToken)
	{
		string actor = User.GetRequiredUsername();
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
