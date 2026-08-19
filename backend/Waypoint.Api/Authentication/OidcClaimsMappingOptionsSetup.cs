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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;

namespace Waypoint.Api.Authentication;

/// <summary>
/// Attaches the post-validation claims mapping (issue #29) to the JwtBearer handler's
/// <c>OnTokenValidated</c> event. Registered via <c>services.ConfigureOptions&lt;...&gt;()</c>
/// (see <c>Program.cs</c>) rather than inline in the <c>AddJwtBearer</c> lambda so this
/// gets its <see cref="ILoggerFactory"/> and <see cref="OidcAuthOptions"/> from the real
/// DI container instead of a throwaway one built mid-registration.
///
/// Runs after <see cref="JwtBearerHandler"/> has already verified issuer, audience, and
/// signature (that verification is entirely the framework's job — nothing here re-checks
/// it); this only translates the realm's claim shape into the two claims the rest of the
/// app already understands (<see cref="ClaimTypes.Name"/> and
/// <see cref="WaypointClaimTypes.Role"/>), the same pair
/// <c>LocalSessionAuthenticationHandler</c> issues for a local session. Everything
/// downstream (<c>[Require*Role]</c>, <c>ClaimsPrincipalExtensions</c>) is unaware which
/// scheme authenticated the request.
///
/// Fails closed: a token with no resolvable username or no recognized role claim is
/// rejected here rather than let through with a principal the role-guard layer would
/// silently deny everywhere (the same "half-authenticated" trap
/// <c>frontend/src/lib/auth.tsx</c>'s wire-narrowing comments describe for the client
/// side of this exact contract).
/// </summary>
public sealed partial class OidcClaimsMappingOptionsSetup : IPostConfigureOptions<JwtBearerOptions>
{
	private readonly IOptionsMonitor<OidcAuthOptions> _oidcOptions;
	private readonly ILogger<OidcClaimsMappingOptionsSetup> _logger;

	public OidcClaimsMappingOptionsSetup(
		IOptionsMonitor<OidcAuthOptions> oidcOptions,
		ILogger<OidcClaimsMappingOptionsSetup> logger)
	{
		_oidcOptions = oidcOptions;
		_logger = logger;
	}

	public void PostConfigure(string? name, JwtBearerOptions options)
	{
		if (name != JwtBearerDefaults.AuthenticationScheme)
		{
			return;
		}

		options.Events ??= new JwtBearerEvents();
		Func<TokenValidatedContext, Task> previous = options.Events.OnTokenValidated;

		options.Events.OnTokenValidated = async context =>
		{
			await previous(context);
			if (context.Result is not null)
			{
				// A prior handler already decided (e.g. failed/skipped) -- do not override it.
				return;
			}

			MapClaims(context);
		};
	}

	private void MapClaims(TokenValidatedContext context)
	{
		OidcAuthOptions options = _oidcOptions.CurrentValue;

		if (context.Principal?.Identity is not ClaimsIdentity identity)
		{
			LogNoIdentity();
			context.Fail("The validated token produced no claims identity.");
			return;
		}

		// preferred_username is the standard OIDC claim Keycloak populates from the
		// user's username. Both ClaimTypes.Name and ClaimTypes.NameIdentifier are
		// populated from it below -- LocalSessionAuthenticationHandler issues both for
		// exactly the same reason (see its doc comment, issue #209 postmortem):
		// ClaimsPrincipalExtensions.GetRequiredUsername() reads Name, while
		// AuthController.Me reads NameIdentifier, and both must resolve to the real
		// caller, not silently collapse to a fallback.
		string? subject = identity.FindFirst("sub")?.Value;
		string? username = identity.FindFirst("preferred_username")?.Value
			?? identity.FindFirst(ClaimTypes.Name)?.Value
			?? subject;
		if (string.IsNullOrWhiteSpace(username))
		{
			LogNoUsername();
			context.Fail("The validated token carries no preferred_username/sub claim to attribute this session to.");
			return;
		}

		if (identity.FindFirst(ClaimTypes.Name) is null)
		{
			identity.AddClaim(new Claim(ClaimTypes.Name, username));
		}

		if (identity.FindFirst(ClaimTypes.NameIdentifier) is null)
		{
			identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, username));
		}

		// Issue #512: users.oidc_sub keys on the token's real `sub`, distinct from the
		// human-readable username above -- falls back to username only in the
		// (should-not-happen, since JwtBearer already validated the token carries a
		// subject) case `sub` itself was absent.
		if (identity.FindFirst(WaypointClaimTypes.Subject) is null)
		{
			identity.AddClaim(new Claim(WaypointClaimTypes.Subject, string.IsNullOrWhiteSpace(subject) ? username : subject));
		}

		// The realm's waypoint-role protocol mapper (deploy/keycloak/realm/waypoint-realm.json)
		// puts the member group's name directly on the configured claim -- a single
		// value, not an array, matching the strictly-one-role-per-user model
		// (docs/domain-model.md "Roles"). JwtSecurityTokenHandler's default inbound
		// claim type map (DefaultInboundClaimTypeMap) silently remaps the short JWT
		// claim name "role" to the long-form ClaimTypes.Role URI while validating, so
		// both spellings are checked -- a differently-configured RoleClaimType (any
		// value other than "role") is looked up literally, unaffected by that map.
		string? roleClaim = identity.FindFirst(options.RoleClaimType)?.Value
			?? (string.Equals(options.RoleClaimType, "role", StringComparison.Ordinal)
				? identity.FindFirst(ClaimTypes.Role)?.Value
				: null);
		if (string.IsNullOrWhiteSpace(roleClaim) || !Enum.TryParse(roleClaim, ignoreCase: true, out WaypointRole role))
		{
			LogUnrecognizedRole(roleClaim ?? "(absent)");
			context.Fail("The validated token carries no recognized Waypoint role claim.");
			return;
		}

		if (identity.FindFirst(WaypointClaimTypes.Role) is null)
		{
			identity.AddClaim(new Claim(WaypointClaimTypes.Role, role.ToString()));
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "OIDC token validated but produced no ClaimsIdentity; rejecting.")]
	private partial void LogNoIdentity();

	[LoggerMessage(Level = LogLevel.Warning, Message = "OIDC token has no preferred_username/name/sub claim; rejecting.")]
	private partial void LogNoUsername();

	[LoggerMessage(Level = LogLevel.Warning, Message = "OIDC token role claim '{RoleClaim}' is missing or unrecognized; rejecting.")]
	private partial void LogUnrecognizedRole(string roleClaim);
}
