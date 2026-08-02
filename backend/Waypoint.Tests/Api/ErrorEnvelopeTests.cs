using System.Net;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

public sealed class ErrorEnvelopeTests : IClassFixture<WaypointApiFactory>
{
	private readonly WaypointApiFactory _factory;

	public ErrorEnvelopeTests(WaypointApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task ProtectedStub_WithoutAuthentication_Returns401WithErrorEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/_stub/items");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "unauthenticated");
	}

	[Fact]
	public async Task UnknownRoute_Returns404WithErrorEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/this-route-does-not-exist");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}
}
