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

/// <summary>
/// Exercises the <c>Require*Role</c> guards across the full Viewer/Cyber/Operator/Admin
/// hierarchy via <see cref="RoleGuardedApiFactory"/> — see that class for why role
/// injection is needed (local auth in this milestone mints only Admin sessions).
/// </summary>
public sealed class RoleGuardTests : IClassFixture<RoleGuardedApiFactory>
{
	private readonly RoleGuardedApiFactory _factory;

	public RoleGuardTests(RoleGuardedApiFactory factory)
	{
		_factory = factory;
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task AdminOnlyStub_WithLowerRole_Returns403WithErrorEnvelope(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/_stub/admin-only");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
	}

	[Fact]
	public async Task AdminOnlyStub_WithAdminRole_ReturnsOk()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/_stub/admin-only");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task ViewerGuardedStub_WithAnyAuthenticatedRole_ReturnsOk(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/_stub/items");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}
}
