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

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Waypoint.Tests.Support;

/// <summary>
/// A host identical to <see cref="WaypointApiFactory"/> except the real
/// Keycloak-pointing JwtBearer options (issuer discovery over HTTPS -- unreachable in a
/// test host) are replaced with a fixed symmetric signing key and in-process
/// <see cref="TokenValidationParameters"/> for <see cref="Issuer"/>/<see cref="Audience"/>
/// (issue #29). This proves the real production pipeline -- <c>AddJwtBearer</c> plus
/// <c>OidcClaimsMappingOptionsSetup</c>'s claims mapping -- end to end via
/// <see cref="IssueToken"/>-minted tokens, without standing up a real Keycloak.
/// LocalAuth stays enabled (inherited default) so both schemes remain exercised by the
/// same suite.
///
/// Not sealed (issue #521): <c>FreshAuthCredentialOverwriteTests</c> subclasses this to
/// additionally layer the real Postgres-backed credential store on top of the real JWT
/// pipeline, the same way <c>CredentialsApiTests.SecretsApiFactory</c> layers it on
/// <see cref="WaypointApiFactory"/> for the local-auth suite.
/// </summary>
public class OidcApiFactory : WaypointApiFactory
{
	public const string Issuer = "https://keycloak.example.internal/realms/waypoint";
	public const string Audience = "waypoint-backend";

	private static readonly SymmetricSecurityKey SigningKey =
		new(Encoding.UTF8.GetBytes("test-only-oidc-signing-key-not-for-any-real-use-32bytes+"));

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
			{
				// Real production config (Authority + discovery) is replaced outright --
				// this is the one seam a test host cannot exercise over a network, so it
				// substitutes an equivalent fixed key/issuer/audience instead of trying to
				// reach a real IdP. options.Events (the claims mapping from
				// OidcClaimsMappingOptionsSetup) is untouched -- that is exactly what this
				// factory exists to exercise unmodified.
				options.Authority = null;
				options.RequireHttpsMetadata = false;
				options.TokenValidationParameters = new TokenValidationParameters
				{
					ValidateIssuer = true,
					ValidIssuer = Issuer,
					ValidateAudience = true,
					ValidAudience = Audience,
					ValidateIssuerSigningKey = true,
					IssuerSigningKey = SigningKey,
					ValidateLifetime = true,
				};
			});
		});
	}

	/// <summary>
	/// Mints a token this factory's JwtBearer handler validates. <paramref name="role"/>
	/// is placed on the <c>role</c> claim (the realm's protocol-mapper claim name --
	/// <c>OidcAuthOptions.RoleClaimType</c>'s default) unless <paramref name="omitRole"/>
	/// is set, and <paramref name="username"/> on <c>preferred_username</c> unless
	/// <paramref name="omitUsername"/> is set -- the two failure-mode knobs
	/// <c>OidcClaimsMappingOptionsSetupTests</c> needs to prove the fail-closed paths.
	/// <paramref name="authTime"/> (issue #521) places the standard OIDC <c>auth_time</c>
	/// claim (Unix-epoch seconds) unless left null, in which case the token carries none
	/// -- the fail-closed case <c>FreshAuthTests</c> needs to prove for a Keycloak realm
	/// missing the "Authentication Time" protocol mapper.
	/// </summary>
	public static string IssueToken(
		string username = "jane.doe",
		string? role = "Admin",
		bool omitRole = false,
		bool omitUsername = false,
		DateTime? expires = null,
		DateTimeOffset? authTime = null,
		string? issuer = null)
	{
		List<Claim> claims = new();
		if (!omitUsername)
		{
			claims.Add(new Claim("preferred_username", username));
		}

		if (!omitRole && role is not null)
		{
			claims.Add(new Claim("role", role));
		}

		if (authTime is DateTimeOffset authTimeValue)
		{
			claims.Add(new Claim("auth_time", authTimeValue.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
		}

		JwtSecurityToken token = new(
			issuer: issuer ?? Issuer,
			audience: Audience,
			claims: claims,
			expires: expires ?? DateTime.UtcNow.AddMinutes(5),
			signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

		return new JwtSecurityTokenHandler().WriteToken(token);
	}
}
