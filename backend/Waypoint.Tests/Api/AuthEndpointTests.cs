using System.Net;
using System.Net.Http.Json;
using Waypoint.Api.Contracts;
using Waypoint.Core.Serialization;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

public sealed class AuthEndpointTests : IClassFixture<WaypointApiFactory>
{
	private readonly WaypointApiFactory _factory;

	public AuthEndpointTests(WaypointApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task Login_WithCorrectCredentials_ReturnsTokenAndAdminRole()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		LoginResponse? body = await response.Content.ReadFromJsonAsync<LoginResponse>(WaypointJsonOptions.Default);
		Assert.NotNull(body);
		Assert.False(string.IsNullOrWhiteSpace(body!.Token));
		Assert.Equal("Admin", body.Role);
		Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);
	}

	[Fact]
	public async Task Login_WithWrongPassword_ReturnsUnauthorizedErrorEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", "not-the-right-password"),
			WaypointJsonOptions.Default);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "invalid_credentials");
	}

	[Fact]
	public async Task Me_WithValidToken_ReturnsAuthenticatedUser()
	{
		HttpClient client = _factory.CreateClient();
		string token = await LoginAndGetTokenAsync(client);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		CurrentUserResponse? body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>(WaypointJsonOptions.Default);
		Assert.NotNull(body);
		Assert.Equal("admin", body!.Username);
		Assert.Equal("Admin", body.Role);
	}

	private static async Task<string> LoginAndGetTokenAsync(HttpClient client)
	{
		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);

		LoginResponse? body = await response.Content.ReadFromJsonAsync<LoginResponse>(WaypointJsonOptions.Default);
		return body!.Token;
	}
}
