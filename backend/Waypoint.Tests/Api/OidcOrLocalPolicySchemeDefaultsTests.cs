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
using Waypoint.Api.Authentication;
using Waypoint.Core.Configuration;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Unit coverage for the request-time scheme selector issue #29 added
/// (<see cref="OidcOrLocalPolicySchemeDefaults"/>). The end-to-end paths (a real request
/// actually authenticating against each scheme) are covered by
/// <c>OidcAuthenticationTests</c> and <c>LocalAuthDevFlagTests</c> — this is the pure
/// per-request routing decision in isolation, including the "flag off" case that is
/// otherwise hard to observe directly (a disabled LocalSession scheme just looks like
/// "Oidc handled it, and rejected the token" from outside).
/// </summary>
public sealed class OidcOrLocalPolicySchemeDefaultsTests
{
	[Fact]
	public void SelectScheme_WithLocalAuthDisabled_AlwaysReturnsOidc_EvenForALocalShapedToken()
	{
		HttpContext context = BuildContext(localAuthEnabled: false, bearerToken: "a".PadLeft(64, 'a'));

		string scheme = OidcOrLocalPolicySchemeDefaults.SelectScheme(context);

		Assert.Equal(JwtBearerDefaults.AuthenticationScheme, scheme);
	}

	[Fact]
	public void SelectScheme_WithLocalAuthEnabledAndJwtShapedToken_ReturnsOidc()
	{
		HttpContext context = BuildContext(localAuthEnabled: true, bearerToken: "header.payload.signature");

		string scheme = OidcOrLocalPolicySchemeDefaults.SelectScheme(context);

		Assert.Equal(JwtBearerDefaults.AuthenticationScheme, scheme);
	}

	[Fact]
	public void SelectScheme_WithLocalAuthEnabledAndOpaqueHexToken_ReturnsLocalSession()
	{
		HttpContext context = BuildContext(localAuthEnabled: true, bearerToken: new string('a', 64));

		string scheme = OidcOrLocalPolicySchemeDefaults.SelectScheme(context);

		Assert.Equal(LocalSessionAuthenticationDefaults.Scheme, scheme);
	}

	[Fact]
	public void SelectScheme_WithNoBearerHeader_ReturnsOidc()
	{
		HttpContext context = BuildContext(localAuthEnabled: true, bearerToken: null);

		string scheme = OidcOrLocalPolicySchemeDefaults.SelectScheme(context);

		Assert.Equal(JwtBearerDefaults.AuthenticationScheme, scheme);
	}

	private static DefaultHttpContext BuildContext(bool localAuthEnabled, string? bearerToken)
	{
		ServiceCollection services = new();
		services.AddSingleton<IOptionsMonitor<LocalAuthOptions>>(
			new StaticOptionsMonitor<LocalAuthOptions>(new LocalAuthOptions { Enabled = localAuthEnabled }));

		DefaultHttpContext context = new()
		{
			RequestServices = services.BuildServiceProvider()
		};

		if (bearerToken is not null)
		{
			context.Request.Headers.Authorization = $"Bearer {bearerToken}";
		}

		return context;
	}
}
