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
using Waypoint.Core.StigManager;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/stigman` surface (issue #310, first slice of the #25 split):
/// global connection CRUD (GET/PUT, Admin writes) plus the per-site resolved read and
/// the reachability/auth test. Reads are Viewer+, mutations Admin-only, matching the
/// role-gate shape every other Configuration-surface controller (Sites, Credentials)
/// already uses.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class StigManagerController : ControllerBase
{
	private readonly Infrastructure.StigManager.StigManagerRepository _stigman;
	private readonly ICredentialSecretStore _secrets;
	private readonly IStigManagerProbe _probe;

	public StigManagerController(Infrastructure.StigManager.StigManagerRepository stigman, ICredentialSecretStore secrets, IStigManagerProbe probe)
	{
		ArgumentNullException.ThrowIfNull(stigman);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(probe);
		_stigman = stigman;
		_secrets = secrets;
		_probe = probe;
	}

	[HttpGet("stigman")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(StigManagerConnectionResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<StigManagerConnectionResponse>> GetGlobal(CancellationToken cancellationToken)
	{
		StigManagerConnection? connection = await _stigman.GetGlobalAsync(cancellationToken).ConfigureAwait(false);
		return connection is null
			? throw new ApiException(HttpStatusCode.NotFound, "not_found", "No global STIG Manager connection is configured.")
			: Ok(StigManagerConnectionResponse.FromDomain(connection));
	}

	[HttpPut("stigman")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(StigManagerConnectionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<StigManagerConnectionResponse>> PutGlobal([FromBody] StigManagerConnectionBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ValidateOrThrow(request.Endpoint, request.Authority, request.Collection, request.ClientId);

		StigManagerConnection saved = await _stigman.PutGlobalAsync(
			request.Endpoint!, request.Authority!, request.Collection!, request.ClientId!,
			string.IsNullOrWhiteSpace(request.Scope) ? "openid stig-manager:stig:read" : request.Scope,
			request.CredentialId, cancellationToken).ConfigureAwait(false);
		return Ok(StigManagerConnectionResponse.FromDomain(saved));
	}

	/// <summary>The resolved connection for a site: its <c>stigman_override</c> when present, else the global default (docs/domain-model.md "STIG configuration documents" three-layer "most specific wins" convention, applied to this two-layer case).</summary>
	[HttpGet("sites/{siteId:guid}/stigman")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ResolvedStigManagerConnectionResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ResolvedStigManagerConnectionResponse>> GetForSite(Guid siteId, CancellationToken cancellationToken)
	{
		ResolvedStigManagerConnection? resolved = await _stigman.ResolveForSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
		return resolved is null
			? throw new ApiException(HttpStatusCode.NotFound, "not_found", "No STIG Manager connection is configured for this site or globally.")
			: Ok(ResolvedStigManagerConnectionResponse.FromDomain(resolved));
	}

	/// <summary>
	/// PUT here writes the site's <c>stigman_override</c> the same way <c>SitesController</c>
	/// does today (PR #238) -- there is no separate override row, only the JSONB column
	/// already on <c>sites</c> (migration 0009). This endpoint exists to keep the
	/// site-scoped writes in the same resource family as the reads above; the
	/// underlying column write is unchanged.
	/// </summary>
	[HttpPut("sites/{siteId:guid}/stigman")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ResolvedStigManagerConnectionResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ResolvedStigManagerConnectionResponse>> PutForSite(
		Guid siteId, [FromBody] StigManagerConnectionBody request, [FromServices] Infrastructure.Sites.SiteRepository sites, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(sites);
		ValidateOrThrow(request.Endpoint, request.Authority, request.Collection, request.ClientId);

		string overrideJson = System.Text.Json.JsonSerializer.Serialize(
			new
			{
				Endpoint = request.Endpoint,
				Authority = request.Authority,
				Collection = request.Collection,
				ClientId = request.ClientId,
				Scope = string.IsNullOrWhiteSpace(request.Scope) ? "openid stig-manager:stig:read" : request.Scope,
				CredentialId = request.CredentialId,
			},
			Core.Serialization.WaypointJsonOptions.Default);

		Core.Sites.SiteWriteOutcome outcome = await sites.UpdateAsync(
			siteId, name: null, description: null, stigmanOverrideJson: overrideJson,
			clearDescription: false, clearStigmanOverride: false, cancellationToken).ConfigureAwait(false);
		if (outcome == Core.Sites.SiteWriteOutcome.NotFound)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No site exists with id '{siteId}'.");
		}

		ResolvedStigManagerConnection resolved = (await _stigman.ResolveForSiteAsync(siteId, cancellationToken).ConfigureAwait(false))!;
		return Ok(ResolvedStigManagerConnectionResponse.FromDomain(resolved));
	}

	/// <summary>
	/// Reachability + auth check, no side effects (issue #310 AC). Tests the resolved
	/// connection for <paramref name="siteId"/> when supplied, else the global
	/// connection. Never dials out with a fabricated identity: a configured
	/// <c>credential_id</c> is decrypted (audited, exactly like
	/// <c>CredentialsController.Test</c>) to obtain the OIDC client secret; a connection
	/// with no credential attempts the probe with no client secret (some STIG Manager
	/// deployments allow a public client).
	/// </summary>
	[HttpPost("stigman/test")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(StigManagerTestResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<StigManagerTestResponse>> Test([FromQuery] Guid? siteId, CancellationToken cancellationToken)
	{
		ResolvedStigManagerConnection? connection = siteId is Guid site
			? await _stigman.ResolveForSiteAsync(site, cancellationToken).ConfigureAwait(false)
			: await GetGlobalAsResolvedAsync(cancellationToken).ConfigureAwait(false);

		if (connection is null)
		{
			return Ok(new StigManagerTestResponse(
				Reachable: false, AuthOk: false, StigManagerTestOutcomes.NotConfigured,
				"No STIG Manager connection is configured.", ApiVersion: null));
		}

		string? clientSecret = null;
		if (connection.CredentialId is Guid credentialId)
		{
			string actor = User.GetRequiredUsername();
			try
			{
				using DecryptedSecret decrypted = await _secrets.DecryptAsync(credentialId, actor, jobId: null, runId: null, cancellationToken).ConfigureAwait(false);
				clientSecret = decrypted.Value;
			}
			catch (CredentialSecretNotFoundException)
			{
				return Ok(new StigManagerTestResponse(
					Reachable: false, AuthOk: false, StigManagerTestOutcomes.AuthFailed,
					"The configured credential has no stored secret.", ApiVersion: null));
			}
			catch (MasterKeyUnavailableException)
			{
				// Issue #430: distinct from AuthFailed -- the STIG Manager credential
				// itself may be fine, but the appliance's own master key is not
				// available to decrypt it. Never send the exception's detailed message
				// (env var, key path) to the wire; only this generic, operator-actionable
				// text does (matches #409's wire hygiene rule for the same exception).
				return Ok(new StigManagerTestResponse(
					Reachable: false, AuthOk: false, StigManagerTestOutcomes.MasterKeyUnavailable,
					"The appliance master key is unavailable; the credential could not be decrypted.", ApiVersion: null));
			}
		}

		StigManagerProbeResult result = await _probe.ProbeAsync(connection, clientSecret, cancellationToken).ConfigureAwait(false);
		return Ok(ToResponse(result));
	}

	private async Task<ResolvedStigManagerConnection?> GetGlobalAsResolvedAsync(CancellationToken cancellationToken)
	{
		StigManagerConnection? global = await _stigman.GetGlobalAsync(cancellationToken).ConfigureAwait(false);
		return global is null
			? null
			: new ResolvedStigManagerConnection(
				global.Endpoint, global.Authority, global.Collection, global.ClientId, global.Scope, global.CredentialId,
				StigManagerConnectionSource.Global);
	}

	private static StigManagerTestResponse ToResponse(StigManagerProbeResult result)
	{
		if (!result.Reachable)
		{
			return new StigManagerTestResponse(false, false, StigManagerTestOutcomes.Unreachable, result.Detail ?? "STIG Manager was unreachable.", null);
		}

		if (!result.AuthOk)
		{
			return new StigManagerTestResponse(true, false, StigManagerTestOutcomes.AuthFailed, result.Detail ?? "Authentication to STIG Manager failed.", null);
		}

		return new StigManagerTestResponse(true, true, StigManagerTestOutcomes.Ok, "STIG Manager is reachable and the connection authenticated successfully.", result.ApiVersion);
	}

	private static void ValidateOrThrow(string? endpoint, string? authority, string? collection, string? clientId)
	{
		if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(authority)
			|| string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(clientId))
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "validation_failed",
				"'endpoint', 'authority', 'collection', and 'client_id' are required.");
		}

		if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _) || !Uri.TryCreate(authority, UriKind.Absolute, out _))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'endpoint' and 'authority' must be absolute URIs.");
		}
	}
}
