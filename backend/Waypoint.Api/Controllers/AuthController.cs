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
