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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Auth;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// <c>/me</c> is issuer-agnostic (works for an OIDC-authenticated caller exactly as it
/// did for local auth). <c>/login</c> is the dev-grade local-auth stand-in (ADR-0004
/// rollout note) — issue #29 made it an explicit dev-flag-only path
/// (<see cref="LocalAuthOptions.Enabled"/>, off by default): production sign-in goes
/// through Keycloak's own OIDC authorization-code/token endpoints, which this backend
/// never proxies (ADR-0004 — the app stays a plain OIDC relying party). When the flag is
/// off, <c>/login</c> answers 404 rather than silently succeeding or 401ing, so a
/// misconfigured production deployment fails obviously rather than looking like a
/// working (but wrong) auth path.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
	private readonly ILocalAuthenticationService _authenticationService;
	private readonly bool _localAuthEnabled;
	private readonly OidcAuthOptions _oidcOptions;

	public AuthController(
		ILocalAuthenticationService authenticationService,
		IOptions<LocalAuthOptions> localAuthOptions,
		IOptions<OidcAuthOptions> oidcOptions)
	{
		_authenticationService = authenticationService;
		_localAuthEnabled = localAuthOptions.Value.Enabled;
		_oidcOptions = oidcOptions.Value;
	}

	/// <summary>
	/// Issue #534: anonymous so the SPA can feature-detect the dev-flag local-auth form
	/// and learn the browser-facing OIDC authority/client id before any sign-in
	/// attempt, without hardcoding either. Deliberately not gated behind a role —
	/// there is no session yet when this is called.
	/// </summary>
	[HttpGet("config")]
	[AllowAnonymous]
	[ProducesResponseType(typeof(AuthConfigResponse), StatusCodes.Status200OK)]
	public ActionResult<AuthConfigResponse> Config()
	{
		return Ok(new AuthConfigResponse(_localAuthEnabled, _oidcOptions.PublicAuthority, _oidcOptions.PublicClientId));
	}

	[HttpPost("login")]
	[AllowAnonymous]
	[ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
	public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
	{
		if (!_localAuthEnabled)
		{
			throw ApiException.NotFound(
				"Local auth is disabled. Sign in through Keycloak (ADR-0004).",
				"LocalAuth:Enabled is false or unset -- this is the production default. See deploy/README.md 'Local auth (dev-flag)'.");
		}

		LocalSession? session = _authenticationService.Authenticate(request.Username, request.Password);
		if (session is null)
		{
			throw new ApiException(HttpStatusCode.Unauthorized, "invalid_credentials", "Invalid username or password.");
		}

		return Ok(new LoginResponse(session.Token, session.Role.ToString(), session.ExpiresAt));
	}

	[HttpGet("me")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
	public ActionResult<CurrentUserResponse> Me()
	{
		string username = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
		string role = User.FindFirstValue(WaypointClaimTypes.Role) ?? string.Empty;

		return Ok(new CurrentUserResponse(username, role));
	}
}
