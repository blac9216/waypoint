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
using Waypoint.Core.Trust;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The first slice of docs/security.md's planned managed-trust surface (issue #753,
/// epic #726, ADR-0025): Admin-only CA trust bundle upload/inventory/replace/delete and
/// scoped trust-policy CRUD. Reads are Viewer+ (matching every other Configuration
/// surface's RBAC shape -- Sites, StigManager, Credentials); every write is Admin-only,
/// since ADR-0025 and docs/security.md's RBAC reconciliation section both name "trust
/// bundle management and scoped TLS bypass authorization" as one of the explicit
/// trust-affecting Admin-only actions.
///
/// Deliberately NOT this slice (docs/testing.md/CLAUDE.md scope discipline, stated
/// here so a reviewer does not read its absence as a gap): no runtime client
/// (PowerCLI/NSX/SSH-adjacent/STIG Manager/content sync) consumes a
/// <see cref="TrustPolicy"/> yet; no runner materializes trust for a session; no
/// <c>PlannedComponentItem</c> snapshots a policy identity/version (that lands with
/// #735-#737); there is no frontend configuration screen, no air-gap transfer
/// behavior, and no readiness-check integration. This controller's CRUD surface is
/// real and independently testable against the migration 0059 tables.
/// </summary>
[ApiController]
[Route("api/v1/trust")]
public sealed class TrustController : ControllerBase
{
	/// <summary>
	/// Matches <see cref="Waypoint.Core.Trust.TrustBundleValidator.MaxPemBytes"/> plus
	/// generous headroom for multipart/JSON envelope overhead -- a real chain is a few
	/// KB, so this only needs to be "clearly larger than the validator's own limit,"
	/// not tuned to it exactly; the validator's own check is what actually enforces the
	/// byte ceiling on the certificate content itself.
	/// </summary>
	private const long MaxRequestBytes = 256 * 1024;

	private readonly ITrustRepository _trust;

	public TrustController(ITrustRepository trust)
	{
		ArgumentNullException.ThrowIfNull(trust);
		_trust = trust;
	}

	[HttpGet("bundles")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(IReadOnlyList<TrustBundleSummaryResponse>), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<TrustBundleSummaryResponse>>> ListBundles(CancellationToken cancellationToken)
	{
		IReadOnlyList<TrustBundle> bundles = await _trust.ListAsync(cancellationToken).ConfigureAwait(false);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return Ok(bundles.Select(b => TrustBundleSummaryResponse.FromDomain(b, now)).ToList());
	}

	[HttpGet("bundles/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(TrustBundleDetailResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<TrustBundleDetailResponse>> GetBundle(Guid id, CancellationToken cancellationToken)
	{
		TrustBundle? bundle = await _trust.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return bundle is null
			? throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No trust bundle exists with id '{id}'.")
			: Ok(TrustBundleDetailResponse.FromDomain(bundle, DateTimeOffset.UtcNow));
	}

	/// <summary>
	/// Upload (issue #753 AC "upload valid CA certificates/chains ... see subject,
	/// issuer, fingerprint, validity"). Accepts a plain JSON body
	/// (<see cref="TrustBundleUploadRequest"/>) -- a CA chain is small text, so this
	/// slice does not need <c>ManagedToolController</c>'s multipart/streaming shape.
	/// Validation order is fixed and deliberate: <see cref="TrustBundleValidator"/>
	/// (size/format/private-key/expiry, no I/O) runs FIRST and rejects wholesale before
	/// any repository call, so a malformed or key-bearing upload never reaches the
	/// database layer at all -- it is never briefly inserted and then rolled back.
	/// </summary>
	[HttpPost("bundles")]
	[RequireAdminRole]
	[RequestSizeLimit(MaxRequestBytes)]
	[ProducesResponseType(typeof(TrustBundleDetailResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<TrustBundleDetailResponse>> Upload([FromBody] TrustBundleUploadRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		TrustBundleValidationResult validated = TrustBundleValidator.Validate(request.Label, request.PemChain, DateTimeOffset.UtcNow);
		if (!validated.IsValid)
		{
			throw ApiException.Validation(validated.SafeErrorMessage!, DescribeOutcome(validated.Outcome));
		}

		TrustBundle? duplicate = await _trust.FindActiveByFingerprintAsync(validated.FingerprintSha256!, cancellationToken).ConfigureAwait(false);
		if (duplicate is not null)
		{
			throw new ApiException(
				HttpStatusCode.Conflict, "duplicate_fingerprint",
				"A currently-active trust bundle already carries this certificate's fingerprint.",
				"To replace it, use the replace endpoint instead of a fresh upload.");
		}

		string actor = User.GetRequiredUsername();
		TrustBundle created = await _trust.CreateAsync(
			validated.Label!, validated.PemChain!, validated.Subject!, validated.Issuer!, validated.FingerprintSha256!,
			validated.NotBefore!.Value, validated.NotAfter!.Value, actor, supersedesId: null, cancellationToken).ConfigureAwait(false);

		return CreatedAtAction(nameof(GetBundle), new { id = created.Id }, TrustBundleDetailResponse.FromDomain(created, DateTimeOffset.UtcNow));
	}

	/// <summary>
	/// Replacement (issue #753 AC "immutable versioning on replacement -- supersede,
	/// don't mutate"): validates the new upload exactly like <see cref="Upload"/>, then
	/// atomically supersedes <paramref name="id"/> in the same transaction that inserts
	/// the new row (<c>TrustRepository.CreateAsync</c>'s <c>supersedesId</c> parameter).
	/// The old row's PEM/metadata are never rewritten; only its <c>status</c>/
	/// <c>superseded_at</c>/<c>superseded_by_id</c> change.
	/// </summary>
	[HttpPost("bundles/{id:guid}/replace")]
	[RequireAdminRole]
	[RequestSizeLimit(MaxRequestBytes)]
	[ProducesResponseType(typeof(TrustBundleDetailResponse), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<TrustBundleDetailResponse>> Replace(Guid id, [FromBody] TrustBundleUploadRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		TrustBundle? existing = await _trust.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No trust bundle exists with id '{id}'.");
		}

		if (existing.Status != TrustBundleStatuses.Active)
		{
			throw new ApiException(
				HttpStatusCode.Conflict, "already_superseded",
				"This trust bundle has already been superseded and cannot be replaced again directly.",
				"Upload a new bundle, or replace its current successor instead.");
		}

		TrustBundleValidationResult validated = TrustBundleValidator.Validate(request.Label, request.PemChain, DateTimeOffset.UtcNow);
		if (!validated.IsValid)
		{
			throw ApiException.Validation(validated.SafeErrorMessage!, DescribeOutcome(validated.Outcome));
		}

		TrustBundle? duplicate = await _trust.FindActiveByFingerprintAsync(validated.FingerprintSha256!, cancellationToken).ConfigureAwait(false);
		if (duplicate is not null)
		{
			throw new ApiException(
				HttpStatusCode.Conflict, "duplicate_fingerprint",
				"A currently-active trust bundle already carries this certificate's fingerprint.");
		}

		string actor = User.GetRequiredUsername();
		TrustBundle created = await _trust.CreateAsync(
			validated.Label!, validated.PemChain!, validated.Subject!, validated.Issuer!, validated.FingerprintSha256!,
			validated.NotBefore!.Value, validated.NotAfter!.Value, actor, supersedesId: id, cancellationToken).ConfigureAwait(false);

		return CreatedAtAction(nameof(GetBundle), new { id = created.Id }, TrustBundleDetailResponse.FromDomain(created, DateTimeOffset.UtcNow));
	}

	/// <summary>Delete-safety (issue #753 AC "delete blocked while referenced -- RESTRICT"): returns 409 rather than the raw FK violation when any policy still points at this bundle.</summary>
	[HttpDelete("bundles/{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> DeleteBundle(Guid id, CancellationToken cancellationToken)
	{
		TrustBundleDeleteOutcome outcome = await _trust.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
		return outcome switch
		{
			TrustBundleDeleteOutcome.Deleted => NoContent(),
			TrustBundleDeleteOutcome.NotFound => throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No trust bundle exists with id '{id}'."),
			TrustBundleDeleteOutcome.Referenced => throw new ApiException(
				HttpStatusCode.Conflict, "trust_bundle_in_use",
				"This trust bundle is referenced by at least one trust policy and cannot be deleted.",
				"Rebind or delete the referencing trust policy first."),
			_ => throw new InvalidOperationException($"Unhandled {nameof(TrustBundleDeleteOutcome)}: {outcome}"),
		};
	}

	[HttpGet("policies")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(IReadOnlyList<TrustPolicyResponse>), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<TrustPolicyResponse>>> ListPolicies(CancellationToken cancellationToken)
	{
		IReadOnlyList<TrustPolicy> policies = await _trust.ListPoliciesAsync(cancellationToken).ConfigureAwait(false);
		return Ok(policies.Select(TrustPolicyResponse.FromDomain).ToList());
	}

	[HttpGet("policies/{scopeType}/{scopeId}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(TrustPolicyResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<TrustPolicyResponse>> GetPolicy(string scopeType, string scopeId, CancellationToken cancellationToken)
	{
		ValidateScopeTypeOrThrow(scopeType);
		TrustPolicy? policy = await _trust.GetCurrentPolicyAsync(scopeType, scopeId, cancellationToken).ConfigureAwait(false);
		return policy is null
			? throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No current trust policy exists for scope '{scopeType}/{scopeId}'.")
			: Ok(TrustPolicyResponse.FromDomain(policy));
	}

	/// <summary>
	/// Sets (or replaces) the current trust policy for one scope (docs/security.md
	/// `PUT /connections/{id}/trust-policy`, generalized to (scope_type, scope_id)).
	/// <c>mode: "bypass"</c> with no <c>bypass_reason</c> is rejected here before ever
	/// reaching the database's own CHECK constraint (ADR-0025 "The API rejects a bypass
	/// request with no bypass_reason").
	/// </summary>
	[HttpPut("policies/{scopeType}/{scopeId}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(TrustPolicyResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<TrustPolicyResponse>> SetPolicy(
		string scopeType, string scopeId, [FromBody] TrustPolicyRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidateScopeTypeOrThrow(scopeType);

		if (string.IsNullOrWhiteSpace(scopeId))
		{
			throw ApiException.Validation("scope_id is required.");
		}

		string? mode = request.Mode?.Trim();
		if (mode == TrustPolicyModes.Bundle)
		{
			if (request.TrustBundleId is null)
			{
				throw ApiException.Validation("'trust_bundle_id' is required when mode is 'bundle'.");
			}

			if (!string.IsNullOrWhiteSpace(request.BypassReason))
			{
				throw ApiException.Validation("'bypass_reason' must not be set when mode is 'bundle'.");
			}
		}
		else if (mode == TrustPolicyModes.Bypass)
		{
			if (string.IsNullOrWhiteSpace(request.BypassReason))
			{
				// ADR-0025: "The API rejects a bypass request with no bypass_reason."
				throw ApiException.Validation("'bypass_reason' is required when mode is 'bypass'.");
			}

			if (request.TrustBundleId is not null)
			{
				throw ApiException.Validation("'trust_bundle_id' must not be set when mode is 'bypass'.");
			}
		}
		else
		{
			throw ApiException.Validation("'mode' must be either 'bundle' or 'bypass'.");
		}

		string actor = User.GetRequiredUsername();
		(TrustPolicyWriteOutcome outcome, TrustPolicy? policy) = await _trust.SetPolicyAsync(
			scopeType, scopeId, mode!, request.TrustBundleId, request.BypassReason?.Trim(), actor, cancellationToken).ConfigureAwait(false);

		return outcome switch
		{
			TrustPolicyWriteOutcome.Written => Ok(TrustPolicyResponse.FromDomain(policy!)),
			TrustPolicyWriteOutcome.TrustBundleNotFound => throw new ApiException(
				HttpStatusCode.NotFound, "not_found", $"No trust bundle exists with id '{request.TrustBundleId}'."),
			TrustPolicyWriteOutcome.TrustBundleSuperseded => throw new ApiException(
				HttpStatusCode.Conflict, "trust_bundle_superseded",
				"This trust bundle has been superseded and cannot be newly bound to a policy.",
				"Bind to its current successor instead."),
			_ => throw new InvalidOperationException($"Unhandled {nameof(TrustPolicyWriteOutcome)}: {outcome}"),
		};
	}

	private static void ValidateScopeTypeOrThrow(string scopeType)
	{
		if (!TrustScopeTypes.All.Contains(scopeType))
		{
			throw ApiException.Validation(
				$"'{scopeType}' is not a supported scope type.",
				$"Supported scope types: {string.Join(", ", TrustScopeTypes.All)}.");
		}
	}

	private static string DescribeOutcome(TrustBundleValidationOutcome outcome) => outcome switch
	{
		TrustBundleValidationOutcome.Empty => "empty",
		TrustBundleValidationOutcome.OversizedInput => "oversized",
		TrustBundleValidationOutcome.Malformed => "malformed",
		TrustBundleValidationOutcome.ContainsPrivateKey => "private_key_present",
		TrustBundleValidationOutcome.Expired => "expired",
		TrustBundleValidationOutcome.DuplicateFingerprint => "duplicate",
		_ => "invalid",
	};
}
