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
/// Issue #29's dev-flag gating (<c>LocalAuth:Enabled</c>, off by default) and issue
/// #505's warm-up-window error surface.
/// </summary>
public sealed class LocalAuthDevFlagTests
{
	[Fact]
	public async Task Login_WithLocalAuthDisabled_ReturnsNotFound()
	{
		await using LocalAuthDisabledApiFactory factory = new();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task Login_DuringWarmUpWindow_ReturnsAuthNotReadyNotInvalidCredentials()
	{
		// Issue #505: an unresolved admin hash must not be reported as "your password
		// is wrong" -- it is a distinct 503, mirroring the master_key_unavailable
		// precedent (#409/#429).
		await using LocalAuthNotReadyApiFactory factory = new();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest("admin", WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "auth_not_ready");
	}
}
