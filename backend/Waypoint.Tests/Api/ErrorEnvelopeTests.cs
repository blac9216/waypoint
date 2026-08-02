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
