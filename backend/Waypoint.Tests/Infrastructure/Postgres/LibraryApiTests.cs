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
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #36 end to end against real Postgres: <c>GET /library/items</c> and
/// <c>GET /library/request-manifest</c> backed by the real <see cref="DepotArtifactRepository"/>
/// and <see cref="ApplianceStateRepository"/>, proving the mode-aware presence flip
/// (connected -&gt; <c>in_depot</c>, disconnected -&gt; <c>missing</c>) actually reads the live
/// <c>appliance_state.mode</c> row, same as <c>SystemApiTests</c>'s mode-flip coverage.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class LibraryApiTests : IAsyncLifetime
{
	private sealed class LibraryApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public LibraryApiFactory(string connectionString)
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

				services.AddSingleton<IDepotArtifactRepository>(new DepotArtifactRepository(_connectionString));
				services.AddSingleton<IApplianceStateRepository>(new ApplianceStateRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private LibraryApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public LibraryApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetLibraryDataAsync();

		_factory = new LibraryApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task GetItems_Connected_NotPresentArtifactIsInDepot()
	{
		await SetModeAsync("connected");
		string tag = Guid.NewGuid().ToString("N");
		DepotArtifactRepository repository = new(_fixture.ConnectionString);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-1", "sha-1", "indexed", """{"product":"VCF","version":"9.0"}"""), CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/library/items");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("connected", document.RootElement.GetProperty("mode").GetString());
		JsonElement item = document.RootElement.GetProperty("items").EnumerateArray()
			.Single(i => i.GetProperty("external_id").GetString() == $"{tag}-1");
		Assert.Equal("in_depot", item.GetProperty("presence").GetString());
	}

	[Fact]
	public async Task GetItems_Disconnected_NotPresentArtifactIsMissing()
	{
		await SetModeAsync("disconnected");
		string tag = Guid.NewGuid().ToString("N");
		DepotArtifactRepository repository = new(_fixture.ConnectionString);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-1", "sha-1", "indexed", """{"product":"VCF","version":"9.0"}"""), CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/library/items");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("disconnected", document.RootElement.GetProperty("mode").GetString());
		JsonElement item = document.RootElement.GetProperty("items").EnumerateArray()
			.Single(i => i.GetProperty("external_id").GetString() == $"{tag}-1");
		Assert.Equal("missing", item.GetProperty("presence").GetString());
	}

	[Fact]
	public async Task GetItems_PresentArtifact_IncludesItInFamiliesPresentCount()
	{
		await SetModeAsync("connected");
		string tag = Guid.NewGuid().ToString("N");
		string product = $"family-{tag}";
		DepotArtifactRepository repository = new(_fixture.ConnectionString);
		await repository.UpsertAsync(
			new DepotArtifactUpsert($"{tag}-1", "sha-1", "present", $$"""{"product":"{{product}}","version":"1.0"}"""), CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/library/items");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(request);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement family = document.RootElement.GetProperty("families").EnumerateArray()
			.Single(f => f.GetProperty("name").GetString() == product);
		Assert.Equal(1, family.GetProperty("present_count").GetInt32());
		Assert.Equal(0, family.GetProperty("missing_count").GetInt32());
	}

	[Fact]
	public async Task GetItems_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/library/items");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task RequestManifest_Disconnected_ListsOnlyMissingArtifacts()
	{
		await SetModeAsync("disconnected");
		string tag = Guid.NewGuid().ToString("N");
		DepotArtifactRepository repository = new(_fixture.ConnectionString);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-present", "sha-1", "present", """{"product":"VCF","version":"9.0"}"""), CancellationToken.None);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-missing", "sha-2", "indexed", """{"product":"VCF","version":"9.1"}"""), CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/library/request-manifest");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("disconnected", document.RootElement.GetProperty("appliance_mode").GetString());
		string[] wantedIds = document.RootElement.GetProperty("wanted").EnumerateArray()
			.Select(w => w.GetProperty("external_id").GetString()!)
			.Where(id => id.StartsWith(tag, StringComparison.Ordinal))
			.ToArray();
		Assert.Equal([$"{tag}-missing"], wantedIds);
	}

	[Fact]
	public async Task RequestManifest_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/library/request-manifest");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	private async Task SetModeAsync(string mode)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE appliance_state SET mode = $1 WHERE id = 1", connection);
		command.Parameters.AddWithValue(mode);
		await command.ExecuteNonQueryAsync();
	}

	private async Task ResetLibraryDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
		await using NpgsqlCommand resetMode = new("UPDATE appliance_state SET mode = 'connected' WHERE id = 1", connection);
		await resetMode.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
