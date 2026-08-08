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
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #10 (M1 vertical slice) end to end against real Postgres: the
/// <c>/downloads</c> REST surface backed by the real repositories and the real job
/// engine -- role gates, that POST fans out one <c>download</c> job (its own
/// single-job run) per requested artifact, list/pagination, and that DELETE cancels
/// cleanly. No handler dispatch happens in these tests (no dispatcher is started) --
/// the full claim-through-verify loop is <c>DownloadJobHandlerEndToEndTests</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class DownloadsApiTests : IAsyncLifetime
{
	private sealed class DownloadsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public DownloadsApiFactory(string connectionString)
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
				services.AddSingleton<IDownloadRepository>(new DownloadRepository(_connectionString));
				services.AddSingleton<IJobQueueRepository>(new JobQueueRepository(
					_connectionString, NullLogger<JobQueueRepository>.Instance));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private DownloadsApiFactory _factory = null!;
	private HttpClient _client = null!;
	private DepotArtifactRepository _artifacts = null!;

#pragma warning restore CA1001

	public DownloadsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetCatalogDataAsync();

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_factory = new DownloadsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task PostDownloads_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.PostAsync("/api/v1/downloads", JsonBody(new { depot_artifact_ids = Array.Empty<string>() }));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task PostDownloads_BelowAdmin_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = Array.Empty<string>() }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>
	/// The N-artifacts fan-out acceptance criterion: queuing three artifacts creates
	/// three independent single-job runs (Continue policy falls out of "N runs", not
	/// one run with N jobs -- see DownloadsController's class doc comment) and three
	/// queued <c>download</c> jobs, one per artifact.
	/// </summary>
	[Fact]
	public async Task PostDownloads_WithAdminRole_QueuesOneDownloadJobPerArtifact()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact1 = await SeedArtifactAsync($"{tag}-1");
		Guid artifact2 = await SeedArtifactAsync($"{tag}-2");
		Guid artifact3 = await SeedArtifactAsync($"{tag}-3");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact1.ToString(), artifact2.ToString(), artifact3.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] downloadIds = document.RootElement.GetProperty("download_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(3, downloadIds.Length);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand jobCount = new(
			"SELECT count(*) FROM jobs WHERE job_type = 'download' AND state = 'queued'", connection))
		{
			Assert.Equal(3L, (long)(await jobCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand runCount = new(
			"SELECT count(DISTINCT run_id) FROM downloads WHERE id::text = ANY($1)", connection))
		{
			runCount.Parameters.AddWithValue(downloadIds);
			Assert.Equal(3L, (long)(await runCount.ExecuteScalarAsync())!);
		}
	}

	[Fact]
	public async Task PostDownloads_UnknownArtifactId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { Guid.NewGuid().ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetDownloads_ReturnsQueuedRows_WithXTotalCount()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);
		await QueueDownloadAsync(artifact);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? totals));
		Assert.Equal(1, int.Parse(totals!.Single(), System.Globalization.CultureInfo.InvariantCulture));

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement[0];
		Assert.Equal("queued", row.GetProperty("state").GetString());
		Assert.Equal(0, row.GetProperty("retry_count").GetInt32());
	}

	/// <summary>Cancel acceptance criterion: DELETE moves the download to <c>cancelled</c> and aborts its owning run's queued job.</summary>
	[Fact]
	public async Task DeleteDownload_WithAdminRole_CancelsCleanly()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);
		(string downloadId, _) = await QueueDownloadAsync(artifact);

		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/downloads/{downloadId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("cancelled", document.RootElement.GetProperty("state").GetString());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE run_id = (SELECT run_id FROM downloads WHERE id = $1)", connection);
		jobState.Parameters.AddWithValue(Guid.Parse(downloadId));
		Assert.Equal("cancelled", (string)(await jobState.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task DeleteDownload_UnknownId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/downloads/{Guid.NewGuid()}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	private async Task<Guid> SeedArtifactAsync(string externalIdTag)
	{
		return await _artifacts.UpsertAsync(
			new DepotArtifactUpsert(externalIdTag, "0000000000000000000000000000000000000000000000000000000000000000", "indexed", "{}"),
			CancellationToken.None);
	}

	private async Task<(string DownloadId, string RunId)> QueueDownloadAsync(Guid artifactId)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifactId.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string downloadId = document.RootElement.GetProperty("download_ids")[0].GetString()!;
		string runId = document.RootElement.GetProperty("run_id").GetString()!;
		return (downloadId, runId);
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

	private async Task ResetCatalogDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
