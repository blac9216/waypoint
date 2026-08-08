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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Contracts;
using Waypoint.Core.Jobs;
using Waypoint.Core.Serialization;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Regression coverage for PR #215 round-1 review: <see cref="RunsEndpointTests"/>
/// exercises the ownership rule only through <see cref="TestAuthHandler"/>, whose
/// claim set is more generous than production's. These tests instead drive the real
/// login endpoint and the real <c>LocalSessionAuthenticationHandler</c> (via
/// <see cref="WaypointApiFactory"/>, with only <see cref="IJobQueueRepository"/>
/// swapped for a fake so no Postgres is required), so a future regression that drops
/// the handler's <c>ClaimTypes.Name</c> claim -- or reintroduces a
/// <c>?? "admin"</c>-style fallback -- fails a test instead of passing one.
///
/// The bootstrap admin's configured username is deliberately overridden away from
/// the literal "admin": that string is also the broken fallback's hardcoded value,
/// so asserting against the default username would not distinguish "the real claim
/// resolved correctly" from "the fallback silently fired and happened to match."
/// </summary>
public sealed class RunInitiatorProductionClaimsTests : IClassFixture<RunInitiatorProductionClaimsTests.ProductionAuthRunsApiFactory>
{
	private readonly ProductionAuthRunsApiFactory _factory;

	public RunInitiatorProductionClaimsTests(ProductionAuthRunsApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task CreateRun_ThroughRealLogin_RecordsActualUsernameAsInitiator()
	{
		HttpClient client = _factory.CreateClient();
		string token = await LoginAsync(client);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add("Authorization", $"Bearer {token}");
		request.Content = JsonContent.Create(new { run_type = "scan", scope = "{}" }, options: WaypointJsonOptions.Default);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(ProductionAuthRunsApiFactory.DistinctAdminUsername, _factory.Repository.LastCreateRun!.Value.InitiatedBy);
	}

	[Fact]
	public async Task PauseRun_ThroughRealLogin_OwnerSucceeds()
	{
		// Proves the positive path also flows through the real claim, not just that
		// a mismatch would be caught: the run's recorded initiator is the same
		// distinct username the real login issues, and the real Admin role means
		// this also exercises the "Admin any run" branch honestly (a genuine role
		// claim, not a role header the test double trusts blindly).
		HttpClient client = _factory.CreateClient();
		string token = await LoginAsync(client);
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: ProductionAuthRunsApiFactory.DistinctAdminUsername));
		_factory.Repository.SetPauseResult(true);

		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add("Authorization", $"Bearer {token}");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<string> LoginAsync(HttpClient client)
	{
		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login",
			new LoginRequest(ProductionAuthRunsApiFactory.DistinctAdminUsername, WaypointApiFactory.TestAdminPassword),
			WaypointJsonOptions.Default);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		LoginResponse? body = await response.Content.ReadFromJsonAsync<LoginResponse>(WaypointJsonOptions.Default);
		return body!.Token;
	}

	/// <summary>
	/// Real <see cref="WaypointApiFactory"/> (production auth handler, production
	/// claim shape) with the bootstrap username overridden to a non-"admin" value
	/// and <see cref="IJobQueueRepository"/> swapped for a fake so the run endpoints
	/// work without Postgres.
	/// </summary>
	public sealed class ProductionAuthRunsApiFactory : WaypointApiFactory
	{
		public const string DistinctAdminUsername = "e2e-claims-probe";

		public Guid RunId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
		public FakeJobQueueRepository Repository { get; } = new();

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureAppConfiguration((_, configBuilder) =>
			{
				configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["LocalAuth:AdminUsername"] = DistinctAdminUsername
				});
			});

			builder.ConfigureTestServices(services =>
			{
				var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IJobQueueRepository));
				if (descriptor != null)
				{
					services.Remove(descriptor);
				}
				services.AddSingleton<IJobQueueRepository>(Repository);
			});
		}
	}
}
