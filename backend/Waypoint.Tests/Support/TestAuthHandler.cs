using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Authorization;

namespace Waypoint.Tests.Support;

/// <summary>
/// Test-only authentication handler: builds a principal from the
/// <see cref="RoleHeaderName"/> request header instead of validating a real session
/// token. This exists so role-guard tests can assert 403 for every role below Admin
/// without the production local-auth flow (deliberately a single Admin user) having any
/// way to mint a lower-privileged token. Never registered outside
/// <see cref="RoleGuardedApiFactory"/>.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	public const string SchemeName = "Test";
	public const string RoleHeaderName = "X-Test-Role";

	public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
		: base(options, logger, encoder)
	{
	}

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Request.Headers.TryGetValue(RoleHeaderName, out Microsoft.Extensions.Primitives.StringValues roleValues))
		{
			return Task.FromResult(AuthenticateResult.NoResult());
		}

		Claim[] claims =
		[
			new Claim(ClaimTypes.NameIdentifier, "test-user"),
			new Claim(WaypointClaimTypes.Role, roleValues.ToString())
		];

		ClaimsIdentity identity = new(claims, SchemeName);
		ClaimsPrincipal principal = new(identity);
		AuthenticationTicket ticket = new(principal, SchemeName);

		return Task.FromResult(AuthenticateResult.Success(ticket));
	}
}
