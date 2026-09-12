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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waypoint.Core.Subscriptions;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Subscriptions;
using Waypoint.Tests.Infrastructure.Postgres;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api.Controllers;

/// <summary>
/// <c>SubscriptionsController</c> (issue #1450) end to end against real Postgres: RBAC
/// matrix (Viewer+ reads, Admin-only writes per R2-10), CRUD happy paths, adopt-a
/// -preset, and validation -&gt; 400 mapping. Never asserts anything about a downstream
/// download/evaluation job -- this controller deliberately never enqueues one.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class SubscriptionsControllerTests : IAsyncLifetime
{
	private sealed class SubscriptionsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public SubscriptionsApiFactory(string connectionString)
		{
			_connectionString = connectionString;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureAppConfiguration((_, configBuilder) =>
			{
				configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:Waypoint"] = _connectionString,
				});
			});

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

				// The AddWaypointInfrastructure-registered repositories are constructed
				// with whatever ConnectionStrings:Waypoint the host resolved at startup
				// (appsettings.json's own "Host=postgres" default, unreachable here) --
				// this test's ConfigureAppConfiguration override above is not guaranteed
				// to reach that construction, so re-register explicitly against this
				// fixture's real connection string (same pattern as
				// ComponentResultReadApiTests).
				services.AddSingleton<ISubscriptionRepository>(new SubscriptionRepository(_connectionString));
				services.AddSingleton<IPresetRepository>(new PresetRepository(_connectionString));
				services.AddSingleton<SubscriptionService>();
				services.AddSingleton<PresetService>();
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private SubscriptionsApiFactory _factory = null!;
	private HttpClient _client = null!;
#pragma warning restore CA1001

	public SubscriptionsControllerTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM subscriptions; DELETE FROM presets", connection);
		await delete.ExecuteNonQueryAsync();

		_factory = new SubscriptionsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	private async Task<Guid> InsertPresetAsync(string anchorVersion = "9.0")
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO presets (stack, generation, name, line_granularity, anchor_version, is_custom)
			VALUES ('VCF', '9', 'vcf-current', 'subminor', $1, false)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(anchorVersion);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		if (role is not null)
		{
			request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		}
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}
		return await _client.SendAsync(request);
	}

	private static object ValidBody() => new
	{
		product = "VCENTER",
		lane = "vmtools",
		line_granularity = "minor",
		anchor_version = "9.0",
		refresh_window_days = 7,
	};

	// ---- RBAC matrix -----------------------------------------------------------

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task MutatingEndpoints_BelowAdmin_Return403(string role)
	{
		HttpResponseMessage create = await SendAsync(HttpMethod.Post, "/api/v1/subscriptions", role, ValidBody());
		Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/api/v1/subscriptions/{Guid.NewGuid()}", role, ValidBody())).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/api/v1/subscriptions/{Guid.NewGuid()}", role, null)).StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task ReadEndpoints_AnyRole_Return200(string role)
	{
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/subscriptions", role, null)).StatusCode);
	}

	// ---- CRUD happy path ---------------------------------------------------------

	[Fact]
	public async Task Create_ThenGet_ThenUpdate_ThenDelete_RoundTrips()
	{
		HttpResponseMessage create = await SendAsync(HttpMethod.Post, "/api/v1/subscriptions", "Admin", ValidBody());
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		using JsonDocument createdBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		Guid id = createdBody.RootElement.GetProperty("id").GetGuid();
		Assert.Equal("VCENTER", createdBody.RootElement.GetProperty("product").GetString());
		Assert.True(createdBody.RootElement.GetProperty("is_enabled").GetBoolean());

		HttpResponseMessage get = await SendAsync(HttpMethod.Get, $"/api/v1/subscriptions/{id}", "Viewer", null);
		Assert.Equal(HttpStatusCode.OK, get.StatusCode);

		HttpResponseMessage update = await SendAsync(
			HttpMethod.Put, $"/api/v1/subscriptions/{id}", "Admin",
			new { product = "VCENTER", lane = "vmtools", line_granularity = "minor", anchor_version = "9.0.1", refresh_window_days = 14, is_enabled = false });
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);
		using JsonDocument updatedBody = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
		Assert.Equal("9.0.1", updatedBody.RootElement.GetProperty("anchor_version").GetString());
		Assert.False(updatedBody.RootElement.GetProperty("is_enabled").GetBoolean());

		HttpResponseMessage delete = await SendAsync(HttpMethod.Delete, $"/api/v1/subscriptions/{id}", "Admin", null);
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

		HttpResponseMessage getAfterDelete = await SendAsync(HttpMethod.Get, $"/api/v1/subscriptions/{id}", "Viewer", null);
		Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
	}

	[Fact]
	public async Task Get_UnknownId_Returns404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/subscriptions/{Guid.NewGuid()}", "Viewer", null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Delete_UnknownId_Returns404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/subscriptions/{Guid.NewGuid()}", "Admin", null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	// ---- Adopt-a-preset -----------------------------------------------------------

	[Fact]
	public async Task Create_WithPresetId_AdoptsDialsFromPreset()
	{
		Guid presetId = await InsertPresetAsync(anchorVersion: "9.0.3");

		HttpResponseMessage create = await SendAsync(
			HttpMethod.Post, "/api/v1/subscriptions", "Admin",
			new { product = "VCENTER", lane = "vmtools", preset_id = presetId, refresh_window_days = 7 });

		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		Assert.Equal("9.0.3", body.RootElement.GetProperty("anchor_version").GetString());
		Assert.Equal("subminor", body.RootElement.GetProperty("line_granularity").GetString());
		Assert.Equal(presetId, body.RootElement.GetProperty("preset_id").GetGuid());
	}

	[Fact]
	public async Task Create_WithUnknownPresetId_Returns404()
	{
		HttpResponseMessage create = await SendAsync(
			HttpMethod.Post, "/api/v1/subscriptions", "Admin",
			new { product = "VCENTER", lane = "vmtools", preset_id = Guid.NewGuid() });

		Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
	}

	// ---- Validation ---------------------------------------------------------------

	[Theory]
	[InlineData("not-a-real-lane")]
	[InlineData("")]
	public async Task Create_InvalidLane_Returns400(string lane)
	{
		HttpResponseMessage create = await SendAsync(
			HttpMethod.Post, "/api/v1/subscriptions", "Admin",
			new { product = "VCENTER", lane, line_granularity = "minor", anchor_version = "9.0" });

		Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
	}

	[Fact]
	public async Task Create_InvalidLineGranularity_Returns400()
	{
		HttpResponseMessage create = await SendAsync(
			HttpMethod.Post, "/api/v1/subscriptions", "Admin",
			new { product = "VCENTER", lane = "vmtools", line_granularity = "whole-release", anchor_version = "9.0" });

		Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
	}

	[Fact]
	public async Task Create_MissingBody_Returns400()
	{
		HttpResponseMessage create = await SendAsync(HttpMethod.Post, "/api/v1/subscriptions", "Admin", null);
		Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
	}
}
