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
using System.Net.Http.Json;
using Waypoint.Api.Contracts;
using Waypoint.Core.Serialization;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// End-to-end coverage for issue #29's OIDC bearer swap, driven through the real
/// <c>AddJwtBearer</c> pipeline and <c>OidcClaimsMappingOptionsSetup</c> (see
/// <see cref="OidcApiFactory"/> for what is and is not substituted for the test host).
/// The <c>[Require*Role]</c> guard layer itself is untouched by issue #29 and already
/// covered by <c>MinimumRoleAuthorizationHandlerTests</c> -- these tests are about the
/// new claims-mapping seam specifically: a valid Keycloak-shaped JWT reaches <c>/me</c>
/// with the right identity/role, and the fail-closed paths (missing role, missing
/// username, wrong issuer/audience/signature) are all rejected rather than silently
/// downgraded.
/// </summary>
public sealed class OidcAuthenticationTests : IClassFixture<OidcApiFactory>
{
	private readonly OidcApiFactory _factory;

	public OidcAuthenticationTests(OidcApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task Me_WithValidOidcToken_ReturnsMappedIdentityAndRole()
	{
		HttpClient client = _factory.CreateClient();
		string token = OidcApiFactory.IssueToken(username: "jane.doe", role: "Cyber");

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		CurrentUserResponse? body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>(WaypointJsonOptions.Default);
		Assert.NotNull(body);
		Assert.Equal("jane.doe", body!.Username);
		Assert.Equal("Cyber", body.Role);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task Me_WithEachRealmRole_MapsToTheSameWaypointRole(string role)
	{
		HttpClient client = _factory.CreateClient();
		string token = OidcApiFactory.IssueToken(role: role);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		CurrentUserResponse? body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>(WaypointJsonOptions.Default);
		Assert.Equal(role, body!.Role);
	}

	[Fact]
	public async Task Me_WithTokenMissingRoleClaim_IsRejected()
	{
		// Fail-closed (issue #29's validation notes): a token that authenticates but
		// carries no recognized role must not reach a principal the role-guard layer
		// would silently deny everywhere -- OidcClaimsMappingOptionsSetup fails the
		// token validation outright instead.
		HttpClient client = _factory.CreateClient();
		string token = OidcApiFactory.IssueToken(omitRole: true);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Me_WithTokenMissingUsernameClaim_IsRejected()
	{
		HttpClient client = _factory.CreateClient();
		string token = OidcApiFactory.IssueToken(omitUsername: true);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Me_WithUnrecognizedRoleClaim_IsRejected()
	{
		HttpClient client = _factory.CreateClient();
		string token = OidcApiFactory.IssueToken(role: "SuperAdmin");

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Me_WithExpiredToken_IsRejected()
	{
		HttpClient client = _factory.CreateClient();
		string token = OidcApiFactory.IssueToken(expires: DateTime.UtcNow.AddMinutes(-5));

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Me_WithoutAnyToken_ReturnsUnauthenticatedEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "unauthenticated");
	}

	[Fact]
	public async Task LocalSessionToken_StillWorksAlongsideOidc_WhenLocalAuthEnabled()
	{
		// The policy scheme (OidcOrLocalPolicySchemeDefaults) must keep routing
		// dev-grade local tokens to LocalSession even with a real Oidc scheme also
		// registered -- this is what deploy/scripts/fresh-stack-smoke-test.sh and the
		// Playwright e2e suite depend on today (see issue #29's validation notes).
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);
		Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
		LoginResponse? login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(WaypointJsonOptions.Default);

		HttpRequestMessage meRequest = new(HttpMethod.Get, "/api/v1/auth/me");
		meRequest.Headers.Add("Authorization", $"Bearer {login!.Token}");
		HttpResponseMessage meResponse = await client.SendAsync(meRequest);

		Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
	}

	/// <summary>
	/// Issue #534: <c>GET /auth/config</c> is the SPA's anonymous feature-detect for
	/// (a) whether the dev-flag local-auth form should render at all and (b) the
	/// browser-facing OIDC authority/client id it needs for the authorization-code
	/// redirect -- reachable with no <c>Authorization</c> header at all, since there is
	/// no session yet when a client calls this.
	/// </summary>
	[Fact]
	public async Task Config_IsAnonymousAndReportsLocalAuthAndOidcSettings()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/auth/config");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		AuthConfigResponse? body = await response.Content.ReadFromJsonAsync<AuthConfigResponse>(WaypointJsonOptions.Default);
		Assert.NotNull(body);
		// OidcApiFactory/WaypointApiFactory leave LocalAuth:Enabled at its
		// inherited default (true) so both auth schemes stay exercised by this
		// suite -- see this class's LocalSessionToken_StillWorksAlongsideOidc test.
		Assert.True(body!.LocalAuthEnabled);
		// Neither PublicAuthority nor PublicClientId is overridden by the test
		// host, so both should be OidcAuthOptions' compiled-in defaults --
		// pinning them here catches an accidental default change same as any
		// other contract field.
		Assert.Equal("/auth/realms/waypoint", body.OidcAuthority);
		Assert.Equal("waypoint-frontend", body.OidcClientId);
	}
}
