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

public sealed class PaginationTests : IClassFixture<WaypointApiFactory>
{
	private readonly WaypointApiFactory _factory;

	public PaginationTests(WaypointApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task GetItems_ReturnsXTotalCountHeader_MatchingFullCollectionSize()
	{
		HttpClient client = _factory.CreateClient();
		string token = await LoginAsAdminAsync(client);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/_stub/items?limit=1&offset=0");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? values));
		Assert.Equal("3", values!.Single());

		ScaffoldStubItem[]? body = await response.Content.ReadFromJsonAsync<ScaffoldStubItem[]>(WaypointJsonOptions.Default);
		Assert.NotNull(body);
		Assert.Single(body!);
	}

	private static async Task<string> LoginAsAdminAsync(HttpClient client)
	{
		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);

		LoginResponse? body = await response.Content.ReadFromJsonAsync<LoginResponse>(WaypointJsonOptions.Default);
		return body!.Token;
	}
}
