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
using Waypoint.Core.Configuration;

namespace Waypoint.Api.Authentication;

/// <summary>
/// Issue #536: computes the two <see cref="JwtBearerOptions"/> values Program.cs's
/// <c>AddJwtBearer</c> registration must set independently rather than let
/// <c>AddJwtBearer</c> derive both from the same <see cref="OidcAuthOptions.Authority"/> —
/// discovery (<see cref="ComputeMetadataAddress"/>) stays on the backend's internal,
/// container-network view of Keycloak, while issuer validation
/// (<see cref="ApplyIssuerValidation"/>) checks the browser-facing canonical issuer
/// string(s) a real token's <c>iss</c> claim actually carries. Extracted to a static,
/// side-effect-free class (rather than left inline in Program.cs) specifically so this
/// mapping is unit-testable without standing up a real host or a real Keycloak — see
/// <c>OidcJwtBearerOptionsFactoryTests</c>.
/// </summary>
public static class OidcJwtBearerOptionsFactory
{
	/// <summary>
	/// Builds the discovery-document URL from <see cref="OidcAuthOptions.Authority"/>,
	/// tolerating a trailing slash on the configured value the same way ASP.NET Core's
	/// own default <c>Authority</c>-derived metadata address does.
	/// </summary>
	public static string? ComputeMetadataAddress(OidcAuthOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Authority))
		{
			return null;
		}

		return $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration";
	}

	/// <summary>
	/// Sets <see cref="Microsoft.IdentityModel.Tokens.TokenValidationParameters.ValidIssuer"/>
	/// (single value — the common case) or
	/// <see cref="Microsoft.IdentityModel.Tokens.TokenValidationParameters.ValidIssuers"/>
	/// (zero or multiple values) from <see cref="OidcAuthOptions.ValidIssuer"/> and
	/// <see cref="OidcAuthOptions.ValidIssuers"/> combined — deliberately never falls
	/// back to <see cref="OidcAuthOptions.Authority"/> (ASP.NET Core's own
	/// <c>AddJwtBearer</c> default): a real browser-obtained token's <c>iss</c> can
	/// never match that internal address, so falling back to it would silently trade
	/// one broken default for another rather than failing closed. When neither
	/// property is set, <c>ValidIssuers</c> is left as an empty list — every token is
	/// then rejected on issuer, the same fail-closed posture
	/// <see cref="OidcAuthOptions.Authority"/>'s own doc comment already documents for
	/// an unset <c>Authority</c>.
	/// </summary>
	public static void ApplyIssuerValidation(OidcAuthOptions options, JwtBearerOptions jwtBearerOptions)
	{
		List<string> validIssuers = new();
		if (!string.IsNullOrWhiteSpace(options.ValidIssuer))
		{
			validIssuers.Add(options.ValidIssuer);
		}

		validIssuers.AddRange(options.ValidIssuers.Where(issuer => !string.IsNullOrWhiteSpace(issuer)));

		jwtBearerOptions.TokenValidationParameters.ValidateIssuer = true;
		if (validIssuers.Count == 1)
		{
			jwtBearerOptions.TokenValidationParameters.ValidIssuer = validIssuers[0];
			jwtBearerOptions.TokenValidationParameters.ValidIssuers = null;
		}
		else
		{
			jwtBearerOptions.TokenValidationParameters.ValidIssuer = null;
			jwtBearerOptions.TokenValidationParameters.ValidIssuers = validIssuers;
		}
	}
}
