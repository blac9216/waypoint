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
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1464 end to end against real Postgres: the <c>/consumer-views</c> surface --
/// EVERY verb (including read) is Admin-only per decision R2-10, an unknown platform
/// key is a 400, and the exactly-one-default invariant holds in both directions
/// through the real HTTP surface: marking another view <c>is_default: true</c> MOVES
/// the default (200/201, exactly one default afterwards), while clearing or deleting
/// the view that holds it is a 409. Every test starts from migration 0131's seeded
/// default row -- the production-reachable state (PR #1816 round 2 Spec finding 1).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ConsumerViewsApiTests : IAsyncLifetime
{
	private sealed class ConsumerViewsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ConsumerViewsApiFactory(string connectionString)
		{
			_connectionString = connectionString;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureTestServices(services =>
			{
				services
					.AddAuthentication(TestAuthHandler.SchemeName)
					.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

				services.PostConfigure<AuthenticationOptions>(options =>
				{
					options.DefaultScheme = TestAuthHandler.SchemeName;
					options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
					options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
					options.DefaultForbidScheme = TestAuthHandler.SchemeName;
				});

				services.AddSingleton<IConsumerViewRepository>(new ConsumerViewRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ConsumerViewsApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ConsumerViewsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetToSeededStateAsync();

		_factory = new ConsumerViewsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Resets to the state a real deployment is actually in: migration 0131's seeded
	/// default row present and holding the default, and nothing else (PR #1816 round 2
	/// Spec finding 1 -- both suites previously did a bare `DELETE FROM consumer_views`,
	/// so every CRUD test ran from a zero-default state production can never reach,
	/// which is exactly why a total default-transfer deadlock survived two review
	/// rounds). The seeded row is re-inserted and re-promoted rather than assumed
	/// intact, because tests legitimately move the default off it and then delete it.
	/// </summary>
	private async Task ResetToSeededStateAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			DELETE FROM consumer_views WHERE id <> '00000000-0000-0000-0000-000000000001';
			INSERT INTO consumer_views (id, name, platforms, is_default)
			VALUES ('00000000-0000-0000-0000-000000000001', 'Default (unfiltered)', '{}', true)
			ON CONFLICT (id) DO NOTHING;
			UPDATE consumer_views SET name = 'Default (unfiltered)', platforms = '{}', is_default = true
			WHERE id = '00000000-0000-0000-0000-000000000001';
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task ListViews_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/consumer-views");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	[InlineData("Cyber")]
	public async Task ListViews_BelowAdmin_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/consumer-views");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task PostView_BelowAdmin_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Baseline", platforms = SingleValidPlatform }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostView_UnknownPlatformKey_Returns400()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Baseline", platforms = SingleUnknownPlatform }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PostView_ThenGet_RoundTripsPlatformsAsAdmin()
	{
		(HttpResponseMessage createResponse, string id) = await CreateViewAsync(
			"7.0 only", ["embeddedEsx-7.0-INTL", "esxio-8.0-INTL"], isDefault: false);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage getResponse = await _client.SendAsync(get);

		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal("7.0 only", document.RootElement.GetProperty("name").GetString());
		Assert.False(document.RootElement.GetProperty("is_default").GetBoolean());
		string[] platforms = document.RootElement.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["embeddedEsx-7.0-INTL", "esxio-8.0-INTL"], platforms);
	}

	[Fact]
	public async Task GetView_AsViewer_Returns403()
	{
		(_, string id) = await CreateViewAsync("Baseline", [], isDefault: false);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(get);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostView_DuplicateName_Returns409()
	{
		await CreateViewAsync("Duplicate", [], isDefault: false);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Duplicate", platforms = Array.Empty<string>() }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	/// <summary>
	/// PR #1816 round 2 Spec finding 1: the fixture starts from migration 0131's seeded
	/// default row -- the state a real deployment is in -- not a wiped table. Pinned so
	/// a regression of the fixture back to `DELETE FROM consumer_views` fails loudly.
	/// </summary>
	[Fact]
	public async Task ListViews_AtFixtureStart_ContainsOnlyTheSeededDefault()
	{
		HttpRequestMessage list = new(HttpMethod.Get, "/api/v1/consumer-views");
		list.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(list);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement only = Assert.Single(document.RootElement.EnumerateArray());
		Assert.Equal(SeededDefaultId, only.GetProperty("id").GetString());
		Assert.True(only.GetProperty("is_default").GetBoolean());
	}

	/// <summary>
	/// Issue #1464 AC 2 over real HTTP, PR #1816 round 2 Spec finding 1: POSTing a view
	/// with <c>is_default: true</c> while the seeded row holds the default MOVES the
	/// default (201), rather than the 409 round 1 returned -- which made every
	/// <c>is_default: true</c> write dead in production. Exactly one default afterwards.
	/// </summary>
	[Fact]
	public async Task PostView_WithIsDefaultTrue_MovesTheDefaultAndReturns201()
	{
		(HttpResponseMessage response, string id) = await CreateViewAsync("Operator default", [], isDefault: true);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		IReadOnlyList<JsonElement> defaults = await ListDefaultsAsync();
		JsonElement only = Assert.Single(defaults);
		Assert.Equal(id, only.GetProperty("id").GetString());
	}

	/// <summary>
	/// Issue #1464 AC 2 over real HTTP, PR #1816 round 2 Spec finding 1: the PUT that
	/// round 1 refused with 409 <c>default_already_set</c>. Promoting another view is
	/// the supported move -- 200, the promoted view is default, the previous default is
	/// not, and exactly one row is default.
	/// </summary>
	[Fact]
	public async Task PutView_PromotingAnotherView_MovesTheDefaultAndReturns200()
	{
		(_, string secondId) = await CreateViewAsync("Operator view", [], isDefault: false);

		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{secondId}")
		{
			Content = JsonBody(new { is_default = true }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument promoted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(promoted.RootElement.GetProperty("is_default").GetBoolean());

		IReadOnlyList<JsonElement> defaults = await ListDefaultsAsync();
		JsonElement only = Assert.Single(defaults);
		Assert.Equal(secondId, only.GetProperty("id").GetString());
		Assert.False(await IsDefaultAsync(SeededDefaultId));
	}

	/// <summary>
	/// The whole operator story end to end, which round 1 made impossible in both
	/// orders (PR #1816 round 2 Spec finding 1): move the default onto a new view, then
	/// delete the seeded row that used to hold it. Both steps succeed and exactly one
	/// default survives.
	/// </summary>
	[Fact]
	public async Task PutThenDelete_MovingTheDefaultThenDeletingThePreviousDefault_Succeeds()
	{
		(_, string id) = await CreateViewAsync("Operator default", [], isDefault: true);

		HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/consumer-views/{SeededDefaultId}");
		delete.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage deleteResponse = await _client.SendAsync(delete);

		Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
		JsonElement only = Assert.Single(await ListDefaultsAsync());
		Assert.Equal(id, only.GetProperty("id").GetString());
	}

	[Fact]
	public async Task PutView_NameOnly_LeavesPlatformsUntouched()
	{
		(_, string id) = await CreateViewAsync("Original", ["embeddedEsx-7.0-INTL"], isDefault: false);

		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{id}")
		{
			Content = JsonBody(new { name = "Renamed" }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("Renamed", document.RootElement.GetProperty("name").GetString());
		string[] platforms = document.RootElement.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["embeddedEsx-7.0-INTL"], platforms);
	}

	[Fact]
	public async Task PutView_UnknownId_Returns404()
	{
		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{Guid.NewGuid()}")
		{
			Content = JsonBody(new { name = "New name" }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DeleteView_Existing_Returns204AndRemovesFromList()
	{
		(_, string id) = await CreateViewAsync("To delete", [], isDefault: false);

		HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/consumer-views/{id}");
		delete.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage deleteResponse = await _client.SendAsync(delete);

		Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

		HttpRequestMessage list = new(HttpMethod.Get, "/api/v1/consumer-views");
		list.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage listResponse = await _client.SendAsync(list);
		using JsonDocument listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.DoesNotContain(listDocument.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == id);
	}

	[Fact]
	public async Task DeleteView_BelowAdmin_Returns403()
	{
		(_, string id) = await CreateViewAsync("To delete", [], isDefault: false);

		HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/consumer-views/{id}");
		delete.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");
		HttpResponseMessage response = await _client.SendAsync(delete);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>PR #1816 round 1 Note 3: the HTTP-level 403 for PUT was missing while every other verb had one -- the guard itself was already proven reflectively by <c>EndpointRoleMatrixTests</c>, this closes the suite-asymmetry gap.</summary>
	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task PutView_BelowAdmin_Returns403(string role)
	{
		(_, string id) = await CreateViewAsync("To rename", [], isDefault: false);

		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{id}")
		{
			Content = JsonBody(new { name = "Renamed" }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>PR #1816 round 1 Spec finding 1, DELETE half, over real HTTP: deleting the sole default view is a 409, never a silent 204 that leaves zero defaults.</summary>
	[Fact]
	public async Task DeleteView_SoleDefault_Returns409()
	{
		(_, string id) = await CreateViewAsync("Only default", [], isDefault: true);

		HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/consumer-views/{id}");
		delete.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(delete);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage getResponse = await _client.SendAsync(get);
		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
	}

	/// <summary>PR #1816 round 1 Spec finding 1, UPDATE/clear half, over real HTTP: a single <c>PUT {"is_default": false}</c> on the sole default view is a 409, never a silent 200 that leaves zero defaults.</summary>
	[Fact]
	public async Task PutView_UnsettingIsDefaultOnSoleDefault_Returns409()
	{
		(_, string id) = await CreateViewAsync("Only default", [], isDefault: true);

		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{id}")
		{
			Content = JsonBody(new { is_default = false }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage getResponse = await _client.SendAsync(get);
		using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.True(document.RootElement.GetProperty("is_default").GetBoolean());
	}

	/// <summary>PR #1816 round 1 Note 4: an empty platform set is explicitly legal over the real HTTP surface too, not just at the repository -- it means "all platforms, no filtering."</summary>
	[Fact]
	public async Task PostView_EmptyPlatforms_Returns201WithEmptyPlatforms()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Unfiltered", platforms = Array.Empty<string>() }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Empty(document.RootElement.GetProperty("platforms").EnumerateArray());
	}

	private const string SeededDefaultId = "00000000-0000-0000-0000-000000000001";

	private static readonly string[] SingleValidPlatform = ["embeddedEsx-7.0-INTL"];
	private static readonly string[] SingleUnknownPlatform = ["not-a-real-platform"];

	private async Task<(HttpResponseMessage Response, string Id)> CreateViewAsync(string name, string[] platforms, bool isDefault)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name, platforms, is_default = isDefault }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string id = document.RootElement.GetProperty("id").GetString()!;
		return (response, id);
	}

	/// <summary>Every view currently flagged <c>is_default</c>, read back over the API -- the "exactly one" assertion the move tests make.</summary>
	private async Task<IReadOnlyList<JsonElement>> ListDefaultsAsync()
	{
		HttpRequestMessage list = new(HttpMethod.Get, "/api/v1/consumer-views");
		list.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(list);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return [.. document.RootElement.EnumerateArray()
			.Where(item => item.GetProperty("is_default").GetBoolean())
			.Select(item => item.Clone())];
	}

	private async Task<bool> IsDefaultAsync(string id)
	{
		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(get);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("is_default").GetBoolean();
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
