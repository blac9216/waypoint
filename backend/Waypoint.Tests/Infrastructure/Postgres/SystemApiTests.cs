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
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Downloads;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #226 end to end against real Postgres and the real filesystem: <c>GET
/// /system</c> backed by the real <see cref="ApplianceStateRepository"/> (reading the
/// migration-0001-seeded <c>appliance_state</c> singleton) and the real
/// <see cref="ArtifactStoreDiskUsageProvider"/> pointed at a temp directory standing
/// in for the artifact-store volume. Role gate and JSON shape at the controller level
/// (against fakes) are covered in <c>Waypoint.Tests.Api.SystemEndpointTests</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class SystemApiTests : IAsyncLifetime
{
	private sealed class SystemApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _artifactStorePath;

		public SystemApiFactory(string connectionString, string artifactStorePath)
		{
			_connectionString = connectionString;
			_artifactStorePath = artifactStorePath;
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

				services.AddSingleton<IApplianceStateRepository>(new ApplianceStateRepository(_connectionString));
				services.AddSingleton<IArtifactStoreDiskUsageProvider>(new ArtifactStoreDiskUsageProvider(
					Options.Create(new DownloadOptions { ArtifactStorePath = _artifactStorePath }),
					NullLogger<ArtifactStoreDiskUsageProvider>.Instance));

				// Issue #443: SystemController now also depends on IWorkerRegistryReader.
				// This factory wires repositories directly (no ConnectionStrings:Waypoint
				// in test config), so AddWaypointInfrastructure's connection-string-gated
				// registration never runs -- register it explicitly, same as the two
				// repositories above.
				services.AddSingleton<IWorkerRegistryReader>(new WorkerRegistryRepository(_connectionString));

				// Issue #241: same reasoning as the IWorkerRegistryReader registration
				// above -- SystemController now also depends on IDepotSyncStatusRepository
				// and IApplianceUptimeProvider, so both are registered explicitly here.
				services.AddSingleton<IDepotSyncStatusRepository>(new DepotSyncStatusRepository(_connectionString));
				services.AddSingleton<IApplianceUptimeProvider, ApplianceUptimeProvider>();
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _artifactStorePath;
	private SystemApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public SystemApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
		_artifactStorePath = Path.Combine(Path.GetTempPath(), "waypoint-system-api-tests-" + Guid.NewGuid().ToString("N"));
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetApplianceStateAsync();

		_factory = new SystemApiFactory(_fixture.ConnectionString, _artifactStorePath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		if (Directory.Exists(_artifactStorePath))
		{
			Directory.Delete(_artifactStorePath, recursive: true);
		}

		return Task.CompletedTask;
	}

	[Fact]
	public async Task Get_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/system");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Get_WithViewerRole_ReturnsSeededApplianceStateAndOneArtifactStore()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;

		// Migration 0001's default seed row.
		Assert.Equal("0.0.0-dev", root.GetProperty("version").GetString());
		Assert.Equal("connected", root.GetProperty("mode").GetString());

		JsonElement store = Assert.Single(root.GetProperty("stores").EnumerateArray());
		Assert.Equal(_artifactStorePath, store.GetProperty("path").GetString());
		Assert.True(store.GetProperty("total_bytes").GetInt64() >= 0);
		Assert.True(store.GetProperty("used_bytes").GetInt64() >= 0);
		Assert.True(store.GetProperty("free_bytes").GetInt64() >= 0);
	}

	/// <summary>
	/// Acceptance criterion from issue #10's PR (#228 discussion) / api-contract.md:
	/// mode reflects the live <c>appliance_state</c> row, not a cached/static value.
	/// </summary>
	[Fact]
	public async Task Get_ReflectsApplianceStateModeChange_Live()
	{
		await SetModeAsync("disconnected");

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("disconnected", document.RootElement.GetProperty("mode").GetString());
	}

	/// <summary>Issue #241: uptime_seconds is present and non-negative -- the real provider reads the live process clock, so an exact value can't be pinned here (see the fake-backed pin in SystemEndpointTests).</summary>
	[Fact]
	public async Task Get_ReturnsNonNegativeUptimeSeconds()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(document.RootElement.GetProperty("uptime_seconds").GetInt64() >= 0);
	}

	/// <summary>Issue #241: no catalog-index run has ever completed -- depot_sync is absent, not an error.</summary>
	[Fact]
	public async Task Get_NoCatalogIndexRunCompleted_OmitsDepotSyncField()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(document.RootElement.TryGetProperty("depot_sync", out _));
	}

	/// <summary>Issue #241: a completed catalog-index run in the real `runs` table is reflected as depot_sync.</summary>
	[Fact]
	public async Task Get_WithCompletedCatalogIndexRun_ReturnsDepotSyncFromRunsTable()
	{
		await InsertCompletedCatalogIndexRunAsync(state: "completed");

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement depotSync = document.RootElement.GetProperty("depot_sync");
		Assert.True(depotSync.GetProperty("succeeded").GetBoolean());
	}

	/// <summary>Issue #241: `completed_with_failures` still counts as a completed sync, just not a succeeded one.</summary>
	[Fact]
	public async Task Get_WithCompletedWithFailuresCatalogIndexRun_ReportsSucceededFalse()
	{
		await InsertCompletedCatalogIndexRunAsync(state: "completed_with_failures");

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(document.RootElement.GetProperty("depot_sync").GetProperty("succeeded").GetBoolean());
	}

	/// <summary>Issue #241: an <c>aborted</c> catalog-index run also sets <c>completed_at</c> but did not run to
	/// completion, so it is excluded from depot_sync (which reports only completed/completed_with_failures syncs).</summary>
	[Fact]
	public async Task Get_WithAbortedCatalogIndexRun_OmitsDepotSyncField()
	{
		await InsertCompletedCatalogIndexRunAsync(state: "aborted");

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(document.RootElement.TryGetProperty("depot_sync", out _));
	}

	private async Task InsertCompletedCatalogIndexRunAsync(string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO runs (run_type, state, completed_at)
			VALUES ('catalog-index', $1, now())
			""", connection);
		insert.Parameters.AddWithValue(state);
		await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	private async Task SetModeAsync(string mode)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand update = new("UPDATE appliance_state SET mode = $1 WHERE id = 1", connection);
		update.Parameters.AddWithValue(mode);
		await update.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	private async Task ResetApplianceStateAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand reset = new(
			"""
			UPDATE appliance_state
			SET version = '0.0.0-dev', build_sha = NULL, mode = 'connected', update_status = 'idle', update_available_version = NULL
			WHERE id = 1
			""", connection);
		await reset.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
