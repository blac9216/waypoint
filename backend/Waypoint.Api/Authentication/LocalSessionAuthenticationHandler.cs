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

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Auth;
using Waypoint.Core.Authorization;

namespace Waypoint.Api.Authentication;

/// <summary>
/// Validates a <c>Authorization: Bearer &lt;token&gt;</c> header against
/// <see cref="ILocalAuthenticationService"/> and, on success, builds a
/// <see cref="ClaimsPrincipal"/> carrying <see cref="WaypointClaimTypes.Role"/>. This
/// class is the only place that knows the local-auth token format; everything else
/// (role guards, controllers) reads claims. That boundary is what lets issue #29 swap
/// this handler for OIDC bearer validation without touching authorization code.
/// </summary>
public sealed class LocalSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	private const string BearerPrefix = "Bearer ";

	private readonly ILocalAuthenticationService _authenticationService;

	public LocalSessionAuthenticationHandler(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger,
		UrlEncoder encoder,
		ILocalAuthenticationService authenticationService)
		: base(options, logger, encoder)
	{
		_authenticationService = authenticationService;
	}

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		string? header = Request.Headers.Authorization.ToString();
		if (string.IsNullOrEmpty(header) || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return Task.FromResult(AuthenticateResult.NoResult());
		}

		string token = header[BearerPrefix.Length..].Trim();
		if (string.IsNullOrEmpty(token))
		{
			return Task.FromResult(AuthenticateResult.NoResult());
		}

		LocalSession? session = _authenticationService.ValidateToken(token);
		if (session is null)
		{
			return Task.FromResult(AuthenticateResult.Fail("The session token is invalid or has expired."));
		}

		// ClaimTypes.Name (not just NameIdentifier) is required: it's what
		// ClaimsPrincipalExtensions.GetRequiredUsername() and Identity.Name read, and
		// what controllers use to attribute a run/credential action to its actual
		// caller (issue #209 postmortem -- its absence silently collapsed every
		// caller and every recorded initiator to the fallback literal "admin").
		Claim[] claims =
		[
			new Claim(ClaimTypes.NameIdentifier, session.Username),
			new Claim(ClaimTypes.Name, session.Username),
			new Claim(WaypointClaimTypes.Role, session.Role.ToString())
		];

		ClaimsIdentity identity = new(claims, Scheme.Name);
		ClaimsPrincipal principal = new(identity);
		AuthenticationTicket ticket = new(principal, Scheme.Name);

		return Task.FromResult(AuthenticateResult.Success(ticket));
	}
}
