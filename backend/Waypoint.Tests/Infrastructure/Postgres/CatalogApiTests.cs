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
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #193 (epic #9 slice 1) end to end against real Postgres: the depot catalog
/// REST surface backed by the real repository and the real job engine -- role gates,
/// filters/pagination with X-Total-Count, and that POST /catalog/sync creates a run
/// plus exactly one queued catalog-index job (no handler registered yet is correct
/// for this slice; <c>JobDispatcherHostedServiceTests.NoHandlerRegistered_...</c>
/// already proves that job fails with a clear note once dispatched).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class CatalogApiTests : IAsyncLifetime
{
	private sealed class CatalogApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public CatalogApiFactory(string connectionString)
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
				services.AddSingleton<IJobQueueRepository>(new JobQueueRepository(
					_connectionString, NullLogger<JobQueueRepository>.Instance));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private CatalogApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public CatalogApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetCatalogDataAsync();

		_factory = new CatalogApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task GetArtifacts_ReturnsSeededRows_WithFilteringAndXTotalCount()
	{
		DepotArtifactRepository repository = new(_fixture.ConnectionString);
		string tag = Guid.NewGuid().ToString("N");
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-1", "sha-1", "indexed", """{"product":"VCF","version":"9.0"}"""), CancellationToken.None);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-2", "sha-2", "present", """{"product":"VCF","version":"9.1"}"""), CancellationToken.None);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-3", "sha-3", "indexed", """{"product":"NSX","version":"4.2"}"""), CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/artifacts?product=VCF&limit=200&offset=0");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? totals));
		Assert.Equal(2, int.Parse(totals!.Single(), System.Globalization.CultureInfo.InvariantCulture));

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] externalIds = document.RootElement.EnumerateArray()
			.Select(item => item.GetProperty("external_id").GetString()!)
			.Where(id => id.StartsWith(tag, StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(2, externalIds.Length);
	}

	[Fact]
	public async Task GetArtifacts_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/catalog/artifacts");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task PostSync_WithAdminRole_CreatesRunAndOneQueuedCatalogIndexJob()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = Guid.Parse(document.RootElement.GetProperty("run_id").GetString()!);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand runType = new("SELECT run_type FROM runs WHERE id = $1", connection))
		{
			runType.Parameters.AddWithValue(runId);
			Assert.Equal("catalog-index", (string)(await runType.ExecuteScalarAsync())!);
		}

		await using NpgsqlCommand jobs = new("SELECT job_type, state FROM jobs WHERE run_id = $1", connection);
		jobs.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await jobs.ExecuteReaderAsync();
		int rowCount = 0;
		while (await reader.ReadAsync())
		{
			rowCount++;
			Assert.Equal("catalog-index", reader.GetString(0));
			Assert.Equal("queued", reader.GetString(1));
		}

		Assert.Equal(1, rowCount);
	}

	[Fact]
	public async Task PostSync_WithViewerRole_Returns403()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	private async Task ResetCatalogDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
