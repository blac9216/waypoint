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

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waypoint.Core.Configuration;

namespace Waypoint.Api.Authentication;

/// <summary>
/// Both schemes (Oidc, LocalSession) are registered unconditionally (see
/// <c>Program.cs</c>'s doc comment on why: a <c>WebApplicationFactory</c> test host's
/// config overlay is not visible to an eager read at that point in top-level
/// <c>Program.cs</c> code, only to options resolved later through DI). This selector is
/// what actually enforces the <c>LocalAuth:Enabled</c> dev-flag (issue #29) at request
/// time, read live via <see cref="IOptionsMonitor{TOptions}"/>: when the flag is off
/// (the default, and every non-dev deployment), every request routes to Oidc regardless
/// of what the caller presents, so the LocalSession scheme -- registered but never
/// selected -- is unreachable. When the flag is on, routing is by token shape: a JWT is
/// three base64url segments joined by <c>.</c>;
/// <c>InMemoryLocalAuthenticationService.GenerateToken()</c> never produces a `.`, so
/// the two token shapes cannot collide.
/// </summary>
public static class OidcOrLocalPolicySchemeDefaults
{
	public const string Scheme = "OidcOrLocal";

	public static string SelectScheme(HttpContext context)
	{
		bool localAuthEnabled = context.RequestServices
			.GetRequiredService<IOptionsMonitor<LocalAuthOptions>>().CurrentValue.Enabled;
		if (!localAuthEnabled)
		{
			return JwtBearerDefaults.AuthenticationScheme;
		}

		string? header = context.Request.Headers.Authorization.ToString();
		const string bearerPrefix = "Bearer ";
		if (!string.IsNullOrEmpty(header) && header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
		{
			string token = header[bearerPrefix.Length..].Trim();
			if (token.Count(c => c == '.') == 2)
			{
				return JwtBearerDefaults.AuthenticationScheme;
			}

			return LocalSessionAuthenticationDefaults.Scheme;
		}

		// No/malformed bearer header: default to Oidc so an anonymous request's
		// [AllowAnonymous] endpoints behave identically regardless of which scheme is
		// nominally "current" -- neither handler produces a result for a missing token.
		return JwtBearerDefaults.AuthenticationScheme;
	}
}
