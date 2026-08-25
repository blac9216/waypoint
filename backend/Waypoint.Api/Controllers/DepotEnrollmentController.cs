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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Secrets;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The issue #691 assisted VCF 9.1 Software Depot enrollment surface: generate/read
/// the disposable non-secret Software Depot ID (portal-registration assist), accept and
/// store an Activation Code (existing or portal-issued), validate it against the tool,
/// and explicit confirmed identity reset. Identity follows the code (owner decision
/// 2026-08-25): a stored code is not required to match the generated Depot ID. The
/// Activation Code value itself NEVER appears in any response this controller returns --
/// only its non-secret decoded <c>asset_id</c> and the enrollment state machine. Authorization mirrors <see cref="CredentialsController"/>: read is
/// Viewer+, every state-changing action (generate, accept, reset) is Admin-only,
/// matching the Admin-only floor every credential write already has (the Activation
/// Code this flow ultimately stores is exactly that credential).
/// </summary>
[ApiController]
[Route("api/v1/downloads/enrollment")]
public sealed class DepotEnrollmentController : ControllerBase
{
	/// <summary>Same operational-chrome tier <see cref="DownloadsController"/>'s readiness/queueing uses -- an enrollment action is on-demand, user-visible-latency work, not a background sweep.</summary>
	private const short EnrollmentPriority = 4;

	private readonly IDepotEnrollmentRepository _enrollment;
	private readonly CredentialRepository _credentials;
	private readonly ICredentialCreationCoordinator _creation;
	private readonly ICredentialSecretStore _secrets;
	private readonly IJobControlRepository _jobs;

	public DepotEnrollmentController(
		IDepotEnrollmentRepository enrollment,
		CredentialRepository credentials,
		ICredentialCreationCoordinator creation,
		ICredentialSecretStore secrets,
		IJobControlRepository jobs)
	{
		ArgumentNullException.ThrowIfNull(enrollment);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(creation);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(jobs);
		_enrollment = enrollment;
		_credentials = credentials;
		_creation = creation;
		_secrets = secrets;
		_jobs = jobs;
	}

	/// <summary>Current enrollment state plus the non-secret Depot ID/pairing facts. Viewer+, matching every other read on the downloads surface.</summary>
	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(DepotEnrollmentResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DepotEnrollmentResponse>> Get(CancellationToken cancellationToken)
	{
		DepotEnrollment? enrollment = await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false);
		if (enrollment is null)
		{
			throw new ApiException(HttpStatusCode.InternalServerError, "enrollment_row_missing", "The depot_enrollment singleton row is missing.");
		}

		CredentialResponse? activationCode = await _credentials.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken).ConfigureAwait(false);
		return Ok(DepotEnrollmentResponse.FromDomain(enrollment, activationCode is { HasSecret: true }));
	}

	/// <summary>
	/// Queues a <c>depot-enrollment</c> job to generate/read the Software Depot ID
	/// (issue #691: "an operator with no code can generate/view/copy a stable Software
	/// Depot ID"). Admin-only: this is the entry point for the whole enrollment flow,
	/// matching the Admin floor <see cref="CredentialsController.Create"/> already has
	/// for the credential this flow ultimately produces.
	/// </summary>
	[HttpPost("depot-id")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DepotEnrollmentJobQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<DepotEnrollmentJobQueuedResponse>> GenerateDepotId(CancellationToken cancellationToken)
	{
		return Accepted(await QueueEnrollmentJobAsync("generate-depot-id", cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Accepts an Activation Code -- either an existing one the operator already holds,
	/// or a freshly portal-issued one -- decodes its structure to confirm it carries an
	/// <c>asset_id</c>, and stores it encrypted as the
	/// <see cref="CredentialTypes.DepotActivationCode"/> credential. Identity follows the
	/// code (owner decision 2026-08-25): any structurally valid code is accepted, no match
	/// against the disposable Software Depot ID is required, and no prior Depot ID is
	/// needed -- swapping in a different working code just works. The raw code is never
	/// echoed back, logged, or included in any exception detail; a structural rejection
	/// reports only the reason class, never the value.
	/// </summary>
	[HttpPost("activation-code")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DepotEnrollmentResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DepotEnrollmentResponse>> AcceptActivationCode(
		[FromBody] AcceptActivationCodeRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.ActivationCode))
		{
			throw ApiException.Validation("'activation_code' is required.");
		}

		string? assetId = DepotActivationCodeCodec.TryExtractAssetId(request.ActivationCode);
		if (assetId is null)
		{
			// Never echoes the input -- a structurally invalid code is reported by
			// class only, not by contents.
			throw new ApiException(
				HttpStatusCode.BadRequest, "invalid_activation_code",
				"The Activation Code could not be decoded, or its decoded structure has no 'asset_id' field.");
		}

		byte[] secretBytes = Encoding.UTF8.GetBytes(request.ActivationCode);
		string actor = User.GetRequiredUsername();
		try
		{
			CredentialResponse? existing = await _credentials.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken).ConfigureAwait(false);
			if (existing is null)
			{
				Guid? created = await _creation.CreateAsync(
					name: "VCF Software Depot Activation Code", CredentialTypes.DepotActivationCode, CredentialOwners.Shared,
					sudoEnabled: false, username: null, secretBytes, actor, cancellationToken).ConfigureAwait(false);
				if (created is null)
				{
					throw new ApiException(HttpStatusCode.Conflict, "name_taken", "A credential named 'VCF Software Depot Activation Code' already exists.");
				}
			}
			else
			{
				// Same store-then-stamp sequence CredentialsController.StoreSecretAsync
				// uses for a PUT-driven secret rotation -- an already-configured
				// Activation Code (e.g. re-pairing after a portal reissue) is replaced
				// in place rather than creating a second credential row.
				await _secrets.StoreAsync(existing.Id, secretBytes, actor, cancellationToken).ConfigureAwait(false);
				await _credentials.StampRotatedAsync(existing.Id, cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			System.Security.Cryptography.CryptographicOperations.ZeroMemory(secretBytes);
		}

		await _enrollment.SetPairedAsync(assetId, cancellationToken).ConfigureAwait(false);

		DepotEnrollment updated = (await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false))!;
		return Ok(DepotEnrollmentResponse.FromDomain(updated, activationCodeConfigured: true));
	}

	/// <summary>
	/// Queues a <c>depot-enrollment</c> validate-code job: a bounded noninteractive
	/// tool call proves the stored code is actually accepted (issue #691 AC 3).
	/// Requires <see cref="AcceptActivationCode"/> to have already run (a code must be
	/// stored to validate).
	/// </summary>
	[HttpPost("validate")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DepotEnrollmentJobQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<DepotEnrollmentJobQueuedResponse>> Validate(CancellationToken cancellationToken)
	{
		CredentialResponse? activationCode = await _credentials.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken).ConfigureAwait(false);
		if (activationCode is null || !activationCode.HasSecret)
		{
			throw new ApiException(
				HttpStatusCode.Conflict, "activation_code_unavailable",
				"No Activation Code is stored yet (POST /downloads/enrollment/activation-code first).");
		}

		return Accepted(await QueueEnrollmentJobAsync("validate-code", cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Explicit confirmed identity reset (issue #691 AC): clears the recorded Depot
	/// ID/pairing, returning enrollment to <see cref="DepotEnrollmentStates.DepotIdUnavailable"/>.
	/// Requires <paramref name="request"/>'s <c>confirm</c> to be literally true --
	/// this is destructive to the current pairing (a fresh Depot ID will need a fresh
	/// portal-issued code) and MUST NOT be triggerable by an accidental click. Never
	/// deletes the stored Activation Code credential or any legacy Download Token row
	/// itself -- only this enrollment record's own state; an operator who also wants
	/// the credential gone deletes it separately via <c>DELETE /credentials/{id}</c>.
	/// </summary>
	[HttpPost("reset")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DepotEnrollmentResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DepotEnrollmentResponse>> Reset([FromBody] ResetEnrollmentRequest request, CancellationToken cancellationToken)
	{
		if (request is not { Confirm: true })
		{
			throw ApiException.Validation("Resetting depot enrollment requires the request body '{\"confirm\": true}'.");
		}

		await _enrollment.ResetAsync(cancellationToken).ConfigureAwait(false);
		DepotEnrollment updated = (await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false))!;
		CredentialResponse? activationCode = await _credentials.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken).ConfigureAwait(false);
		return Ok(DepotEnrollmentResponse.FromDomain(updated, activationCode is { HasSecret: true }));
	}

	private async Task<DepotEnrollmentJobQueuedResponse> QueueEnrollmentJobAsync(string operation, CancellationToken cancellationToken)
	{
		string initiatedBy = User.GetRequiredUsername();
		Guid runId = await _jobs.CreateRunAsync("depot-enrollment", "{}", credentialId: null, initiatedBy, cancellationToken).ConfigureAwait(false);

		string payload = JsonSerializer.Serialize(new { operation });
		JobSpec spec = new("depot-enrollment", EnrollmentPriority, TargetId: null, TargetName: "depot-enrollment", Payload: payload);
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, [spec], initiatedBy, cancellationToken).ConfigureAwait(false);

		return new DepotEnrollmentJobQueuedResponse(runId.ToString(), jobIds[0].ToString());
	}
}
