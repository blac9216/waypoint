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
using Waypoint.Api.Contracts;
using Waypoint.Core.Auth;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Dev-grade local auth surface (ADR-0004 rollout note). Both endpoints exist purely to
/// exercise <see cref="ILocalAuthenticationService"/> over HTTP; issue #29 replaces this
/// controller's login flow with Keycloak's OIDC authorization-code/token endpoints
/// without touching <c>/me</c> or the role guards downstream.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
	private readonly ILocalAuthenticationService _authenticationService;

	public AuthController(ILocalAuthenticationService authenticationService)
	{
		_authenticationService = authenticationService;
	}

	[HttpPost("login")]
	[AllowAnonymous]
	[ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
	public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
	{
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
