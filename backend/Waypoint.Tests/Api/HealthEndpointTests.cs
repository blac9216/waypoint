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

public sealed class HealthEndpointTests : IClassFixture<WaypointApiFactory>
{
	private readonly WaypointApiFactory _factory;

	public HealthEndpointTests(WaypointApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task Get_ReturnsOkWithVersionAndBuildInfo_WithoutAuthentication()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/health");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		HealthResponse? body = await response.Content.ReadFromJsonAsync<HealthResponse>(WaypointJsonOptions.Default);
		Assert.NotNull(body);
		Assert.Equal("ok", body!.Status);
		Assert.False(string.IsNullOrWhiteSpace(body.Version));
		Assert.False(string.IsNullOrWhiteSpace(body.Sha));
		Assert.False(string.IsNullOrWhiteSpace(body.BuiltAt));
	}

	[Fact]
	public async Task Get_ResponseBody_UsesSnakeCaseFieldNames()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/health");
		string json = await response.Content.ReadAsStringAsync();

		Assert.Contains("\"built_at\"", json);
		Assert.DoesNotContain("\"BuiltAt\"", json);
		Assert.DoesNotContain("\"builtAt\"", json);
	}
}
