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
using Microsoft.Extensions.Options;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
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
	/// <summary>
	/// No scan priority tier applies to a connectivity test (domain-model.md's
	/// six-valued priority column is for scan/remediate targets); same tier
	/// <see cref="DiscoveryController"/> uses for its own on-demand, user-visible-latency
	/// job.
	/// </summary>
	private const short CredentialTestPriority = 4;

	private readonly CredentialRepository _credentials;
	private readonly ICredentialSecretStore _secrets;
	private readonly ICredentialCreationCoordinator _creation;
	private readonly IJobControlRepository _jobs;
	private readonly IOptionsMonitor<StepUpAuthOptions> _stepUpAuthOptions;

	public CredentialsController(
		CredentialRepository credentials,
		ICredentialSecretStore secrets,
		ICredentialCreationCoordinator creation,
		IJobControlRepository jobs,
		IOptionsMonitor<StepUpAuthOptions> stepUpAuthOptions)
	{
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(creation);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(stepUpAuthOptions);
		_credentials = credentials;
		_secrets = secrets;
		_creation = creation;
		_jobs = jobs;
		_stepUpAuthOptions = stepUpAuthOptions;
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

		// Issue #188: metadata insert + secret store (+ rotated_at stamp) commit as one
		// transaction via the coordinator, so a secret-store failure (bad master key, DB
		// blip) never leaves an orphan metadata row with has_secret=false for a retry to
		// collide with as a spurious 409 name_taken.
		byte[]? secretBytes = string.IsNullOrEmpty(request.Secret) ? null : Encoding.UTF8.GetBytes(request.Secret);
		Guid? id;
		try
		{
			id = await _creation.CreateAsync(
				request.Name, request.CredentialType, owner, sudoEnabled, request.Username,
				secretBytes, User.GetRequiredUsername(), cancellationToken);
		}
		finally
		{
			if (secretBytes is not null)
			{
				System.Security.Cryptography.CryptographicOperations.ZeroMemory(secretBytes);
			}
		}

		if (id is not Guid createdId)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A credential named '{request.Name}' already exists.");
		}

		CredentialResponse created = (await _credentials.GetAsync(createdId, cancellationToken))!;
		return CreatedAtAction(nameof(Get), new { id = createdId }, created);
	}

	/// <summary>
	/// Issue #521: a request that overwrites secret material (<see cref="CredentialUpdateRequest.Secret"/>
	/// set) additionally requires step-up re-authentication -- checked first, before any
	/// other field is written, so a stale-auth caller cannot slip a rename/sudo-flip
	/// through alongside a rejected secret rotation. This is conditional on the request
	/// body (renaming/sudo-only updates are never gated), so it is the imperative
	/// <see cref="RequireFreshAuthAttribute.Check"/> call below, not a pipeline-level
	/// <c>[RequireFreshAuth]</c> attribute (a bare `[Authorize]` policy runs before model
	/// binding decides what this specific request is doing, so it cannot see the body) --
	/// see that attribute's doc comment for why it still exists as a declarative
	/// marker/backstop for actions that gate unconditionally.
	/// </summary>
	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CredentialResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<CredentialResponse>> Update(Guid id, [FromBody] CredentialUpdateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!string.IsNullOrEmpty(request.Secret))
		{
			RequireFreshAuthAttribute.Check(User, _stepUpAuthOptions.CurrentValue.FreshnessWindow);
		}

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
	/// Issue #245: queues a real <c>credential-test</c> connectivity job (replacing
	/// issue #20's synchronous decrypt-only 200) -- per docs/api-contract.md
	/// ("Connectivity check; 202 → job"). One run containing one job, the same
	/// one-run-per-initiation shape <see cref="DiscoveryController.Discover"/> uses.
	/// The job's terminal outcome, not this method, flips <c>credentials.health</c>
	/// (<see cref="Waypoint.Infrastructure.Credentials.CredentialTestJobHandler"/>).
	/// </summary>
	[HttpPost("{id:guid}/test")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(CredentialTestQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<CredentialTestQueuedResponse>> Test(Guid id, CancellationToken cancellationToken)
	{
		if (await _credentials.GetAsync(id, cancellationToken) is null)
		{
			throw NotFoundError(id);
		}

		string actor = User.GetRequiredUsername();
		Guid runId = await _jobs.CreateRunAsync("credential-test", "{}", id, actor, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(
			runId,
			[new JobSpec("credential-test", CredentialTestPriority, CredentialId: id)],
			actor,
			cancellationToken).ConfigureAwait(false);

		return Accepted(new CredentialTestQueuedResponse(runId, jobIds[0]));
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
