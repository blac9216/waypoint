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
using Waypoint.Api.Authentication;
using Waypoint.Core.Configuration;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #536: pins <see cref="OidcJwtBearerOptionsFactory"/>'s discovery/issuer split
/// in isolation — the mapping Program.cs's real <c>AddJwtBearer</c> registration relies
/// on, extracted to a static class specifically so it is testable without a real host or
/// a real Keycloak. <see cref="Api.OidcAuthenticationTests"/> proves the end-to-end
/// accept/reject behavior through the real pipeline; this class proves the two
/// <see cref="JwtBearerOptions"/> values are computed correctly for every input shape.
/// </summary>
public sealed class OidcJwtBearerOptionsFactoryTests
{
	private static readonly string[] ExpectedCombinedIssuers =
	{
		"https://localhost:8443/auth/realms/waypoint",
		"https://waypoint.example.internal/auth/realms/waypoint",
	};


	[Fact]
	public void ComputeMetadataAddress_AppendsWellKnownDiscoveryPath()
	{
		OidcAuthOptions options = new() { Authority = "http://keycloak:8080/auth/realms/waypoint" };

		string? metadataAddress = OidcJwtBearerOptionsFactory.ComputeMetadataAddress(options);

		Assert.Equal(
			"http://keycloak:8080/auth/realms/waypoint/.well-known/openid-configuration",
			metadataAddress);
	}

	[Fact]
	public void ComputeMetadataAddress_TrimsATrailingSlashBeforeAppending()
	{
		OidcAuthOptions options = new() { Authority = "http://keycloak:8080/auth/realms/waypoint/" };

		string? metadataAddress = OidcJwtBearerOptionsFactory.ComputeMetadataAddress(options);

		Assert.Equal(
			"http://keycloak:8080/auth/realms/waypoint/.well-known/openid-configuration",
			metadataAddress);
	}

	[Fact]
	public void ComputeMetadataAddress_WithNoAuthority_ReturnsNull()
	{
		OidcAuthOptions options = new() { Authority = null };

		string? metadataAddress = OidcJwtBearerOptionsFactory.ComputeMetadataAddress(options);

		Assert.Null(metadataAddress);
	}

	[Fact]
	public void ApplyIssuerValidation_WithSingleValidIssuer_SetsValidIssuerNotTheAuthority()
	{
		// The core issue #536 assertion: ValidIssuer must come from the browser-facing
		// canonical issuer, NOT from Authority (the backend's internal discovery
		// address) -- AddJwtBearer's own default would derive it from Authority, which
		// is exactly the bug this factory exists to avoid reintroducing.
		OidcAuthOptions options = new()
		{
			Authority = "http://keycloak:8080/auth/realms/waypoint",
			ValidIssuer = "https://localhost:8443/auth/realms/waypoint",
		};
		JwtBearerOptions jwtBearerOptions = new();

		OidcJwtBearerOptionsFactory.ApplyIssuerValidation(options, jwtBearerOptions);

		Assert.True(jwtBearerOptions.TokenValidationParameters.ValidateIssuer);
		Assert.Equal("https://localhost:8443/auth/realms/waypoint", jwtBearerOptions.TokenValidationParameters.ValidIssuer);
		Assert.Null(jwtBearerOptions.TokenValidationParameters.ValidIssuers);
	}

	[Fact]
	public void ApplyIssuerValidation_WithMultipleValidIssuers_PopulatesValidIssuersNotValidIssuer()
	{
		OidcAuthOptions options = new()
		{
			ValidIssuer = "https://localhost:8443/auth/realms/waypoint",
			ValidIssuers = new[] { "https://waypoint.example.internal/auth/realms/waypoint" },
		};
		JwtBearerOptions jwtBearerOptions = new();

		OidcJwtBearerOptionsFactory.ApplyIssuerValidation(options, jwtBearerOptions);

		Assert.Null(jwtBearerOptions.TokenValidationParameters.ValidIssuer);
		Assert.Equal(ExpectedCombinedIssuers, jwtBearerOptions.TokenValidationParameters.ValidIssuers);
	}

	[Fact]
	public void ApplyIssuerValidation_WithNeitherConfigured_FailsClosedWithNoValidIssuers()
	{
		// Deliberately does NOT fall back to Authority (issue #536) -- an unconfigured
		// deployment must reject every real token on issuer rather than silently
		// validating against the internal address a browser-minted token's iss can
		// never match.
		OidcAuthOptions options = new();
		JwtBearerOptions jwtBearerOptions = new();

		OidcJwtBearerOptionsFactory.ApplyIssuerValidation(options, jwtBearerOptions);

		Assert.True(jwtBearerOptions.TokenValidationParameters.ValidateIssuer);
		Assert.Null(jwtBearerOptions.TokenValidationParameters.ValidIssuer);
		Assert.Empty(jwtBearerOptions.TokenValidationParameters.ValidIssuers ?? Array.Empty<string>());
	}

	[Fact]
	public void ApplyIssuerValidation_IgnoresBlankEntriesInValidIssuers()
	{
		OidcAuthOptions options = new()
		{
			ValidIssuer = "https://localhost:8443/auth/realms/waypoint",
			ValidIssuers = new[] { "", "   " },
		};
		JwtBearerOptions jwtBearerOptions = new();

		OidcJwtBearerOptionsFactory.ApplyIssuerValidation(options, jwtBearerOptions);

		Assert.Equal("https://localhost:8443/auth/realms/waypoint", jwtBearerOptions.TokenValidationParameters.ValidIssuer);
	}
}
