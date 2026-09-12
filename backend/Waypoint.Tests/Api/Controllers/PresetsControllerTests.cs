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
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Infrastructure.Postgres;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api.Controllers;

/// <summary>
/// <c>PresetsController</c> (issue #1450) end to end against real Postgres: shipped
/// presets are read-only, <c>POST .../clone</c> produces an independent custom preset,
/// editing that clone never mutates the shipped source (#1045's own AC), and a write to
/// a shipped preset is rejected.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class PresetsControllerTests : IAsyncLifetime
{
	private sealed class PresetsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public PresetsApiFactory(string connectionString)
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
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private PresetsApiFactory _factory = null!;
	private HttpClient _client = null!;
#pragma warning restore CA1001

	public PresetsControllerTests(PostgresFixture fixture)
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

		_factory = new PresetsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	private async Task<Guid> InsertShippedPresetAsync(string name = "vcf-current")
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO presets (stack, generation, name, line_granularity, anchor_version, is_custom)
			VALUES ('VCF', '9', $1, 'minor', '9.0', false)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(name);
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

	// ---- RBAC matrix -----------------------------------------------------------

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task MutatingEndpoints_BelowAdmin_Return403(string role)
	{
		Guid presetId = await InsertShippedPresetAsync();

		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/presets/{presetId}/clone", role, null)).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/api/v1/presets/{presetId}", role, new { name = "x" })).StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task ReadEndpoints_AnyRole_Return200(string role)
	{
		Guid presetId = await InsertShippedPresetAsync();

		Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/presets", role, null)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/presets/{presetId}", role, null)).StatusCode);
	}

	[Fact]
	public async Task Get_UnknownId_Returns404()
	{
		Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/presets/{Guid.NewGuid()}", "Viewer", null)).StatusCode);
	}

	// ---- Clone -> independent custom preset ----------------------------------------

	[Fact]
	public async Task Clone_ProducesIndependentCustomPreset()
	{
		Guid shippedId = await InsertShippedPresetAsync();

		HttpResponseMessage clone = await SendAsync(HttpMethod.Post, $"/api/v1/presets/{shippedId}/clone", "Admin", new { name = "my custom preset" });

		Assert.Equal(HttpStatusCode.Created, clone.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await clone.Content.ReadAsStringAsync());
		Assert.True(body.RootElement.GetProperty("is_custom").GetBoolean());
		Assert.Equal(shippedId, body.RootElement.GetProperty("source_preset_id").GetGuid());
		Assert.NotEqual(shippedId, body.RootElement.GetProperty("id").GetGuid());
		Assert.Equal("my custom preset", body.RootElement.GetProperty("name").GetString());
	}

	[Fact]
	public async Task Clone_UnknownSource_Returns404()
	{
		Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/v1/presets/{Guid.NewGuid()}/clone", "Admin", null)).StatusCode);
	}

	/// <summary>#1045's own AC: editing a clone never mutates the shipped source, and a later shipped-preset update leaves the clone untouched.</summary>
	[Fact]
	public async Task EditingClone_NeverMutatesSource_AndSourceUpdateNeverTouchesClone()
	{
		Guid shippedId = await InsertShippedPresetAsync();
		HttpResponseMessage clone = await SendAsync(HttpMethod.Post, $"/api/v1/presets/{shippedId}/clone", "Admin", null);
		using JsonDocument cloneBody = JsonDocument.Parse(await clone.Content.ReadAsStringAsync());
		Guid cloneId = cloneBody.RootElement.GetProperty("id").GetGuid();

		HttpResponseMessage update = await SendAsync(
			HttpMethod.Put, $"/api/v1/presets/{cloneId}", "Admin",
			new { name = "edited clone", line_granularity = "major", anchor_version = "9.1" });
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);

		HttpResponseMessage sourceAfterEdit = await SendAsync(HttpMethod.Get, $"/api/v1/presets/{shippedId}", "Viewer", null);
		using JsonDocument sourceBody = JsonDocument.Parse(await sourceAfterEdit.Content.ReadAsStringAsync());
		Assert.Equal("vcf-current", sourceBody.RootElement.GetProperty("name").GetString());
		Assert.Equal("minor", sourceBody.RootElement.GetProperty("line_granularity").GetString());

		// Simulate an appliance update refreshing the shipped preset's own row directly.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand appliancUpdate = new("UPDATE presets SET name = 'vcf-current (v2)' WHERE id = $1", connection);
		appliancUpdate.Parameters.AddWithValue(shippedId);
		await appliancUpdate.ExecuteNonQueryAsync();

		HttpResponseMessage cloneAfterSourceUpdate = await SendAsync(HttpMethod.Get, $"/api/v1/presets/{cloneId}", "Viewer", null);
		using JsonDocument cloneAfterBody = JsonDocument.Parse(await cloneAfterSourceUpdate.Content.ReadAsStringAsync());
		Assert.Equal("edited clone", cloneAfterBody.RootElement.GetProperty("name").GetString());
	}

	[Fact]
	public async Task Update_ShippedPreset_Returns409()
	{
		Guid shippedId = await InsertShippedPresetAsync();

		HttpResponseMessage update = await SendAsync(HttpMethod.Put, $"/api/v1/presets/{shippedId}", "Admin", new { name = "hacked" });

		Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
		HttpResponseMessage unchanged = await SendAsync(HttpMethod.Get, $"/api/v1/presets/{shippedId}", "Viewer", null);
		using JsonDocument body = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
		Assert.Equal("vcf-current", body.RootElement.GetProperty("name").GetString());
	}

	[Fact]
	public async Task Update_UnknownId_Returns404()
	{
		Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/api/v1/presets/{Guid.NewGuid()}", "Admin", new { name = "x" })).StatusCode);
	}

	[Fact]
	public async Task Update_InvalidLineGranularity_Returns400()
	{
		Guid shippedId = await InsertShippedPresetAsync();
		HttpResponseMessage clone = await SendAsync(HttpMethod.Post, $"/api/v1/presets/{shippedId}/clone", "Admin", null);
		using JsonDocument cloneBody = JsonDocument.Parse(await clone.Content.ReadAsStringAsync());
		Guid cloneId = cloneBody.RootElement.GetProperty("id").GetGuid();

		HttpResponseMessage update = await SendAsync(HttpMethod.Put, $"/api/v1/presets/{cloneId}", "Admin", new { line_granularity = "whole-release" });

		Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
	}
}
