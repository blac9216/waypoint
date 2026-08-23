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
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #40 (backend slice) end to end against real Postgres: the compliance-content
/// config/pull-history REST surface and the profile inventory read, backed by the real
/// repositories and the real job engine -- role gates, 404-when-unconfigured, and that
/// POST /compliance-content/pull creates a run plus exactly one queued content-pull
/// job (no handler resolves it here -- ContentPullJobHandlerTests covers the handler
/// itself in isolation).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ComplianceContentApiTests : IAsyncLifetime
{
	private sealed class ComplianceContentApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ComplianceContentApiFactory(string connectionString)
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

				// This factory wires repositories directly (no ConnectionStrings:Waypoint
				// in test config), so AddWaypointInfrastructure's connection-string-gated
				// registration never runs -- register everything the two controllers
				// depend on explicitly, matching SystemApiTests' pattern.
				services.AddSingleton<IComplianceContentRepository>(new ComplianceContentRepository(_connectionString));
				services.AddSingleton<IProfileRepository>(new ProfileRepository(_connectionString));
				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ComplianceContentApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ComplianceContentApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetComplianceContentDataAsync();

		_factory = new ComplianceContentApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	private async Task ResetComplianceContentDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE compliance_content_pulls, compliance_content, profiles RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task Get_WhenNotConfigured_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/compliance-content");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Put_WithAdminRole_ThenGet_RoundTrips()
	{
		HttpRequestMessage put = new(HttpMethod.Put, "/api/v1/compliance-content")
		{
			Content = JsonContent.Create(new { repository_url = "https://example.internal/compliance-content.git", ref_type = "tag", ref_value = "v1.2.3" })
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage putResponse = await _client.SendAsync(put);
		Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

		HttpRequestMessage get = new(HttpMethod.Get, "/api/v1/compliance-content");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage getResponse = await _client.SendAsync(get);

		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal("tag", document.RootElement.GetProperty("ref_type").GetString());
		Assert.Equal("v1.2.3", document.RootElement.GetProperty("ref_value").GetString());
		// WaypointJsonOptions omits null properties from the wire (WhenWritingNull) --
		// an unconfigured pull leaves pulled_commit absent, not present-as-null.
		Assert.False(document.RootElement.TryGetProperty("pulled_commit", out _));
	}

	[Fact]
	public async Task Put_WithViewerRole_Returns403()
	{
		HttpRequestMessage put = new(HttpMethod.Put, "/api/v1/compliance-content")
		{
			Content = JsonContent.Create(new { repository_url = "https://example.internal/compliance-content.git", ref_type = "tag", ref_value = "v1" })
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Put_WithInvalidRefType_Returns400()
	{
		HttpRequestMessage put = new(HttpMethod.Put, "/api/v1/compliance-content")
		{
			Content = JsonContent.Create(new { repository_url = "https://example.internal/compliance-content.git", ref_type = "commit", ref_value = "deadbeef" })
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PostPull_WhenNotConfigured_Returns409()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/compliance-content/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task PostPull_WithAdminRole_CreatesRunAndOneQueuedContentPullJob()
	{
		ComplianceContentRepository repository = new(_fixture.ConnectionString);
		await repository.PutConfigAsync("https://example.internal/compliance-content.git", "branch", "main", CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/compliance-content/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = Guid.Parse(document.RootElement.GetProperty("run_id").GetString()!);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobs = new("SELECT job_type, state FROM jobs WHERE run_id = $1", connection);
		jobs.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await jobs.ExecuteReaderAsync();
		int rowCount = 0;
		while (await reader.ReadAsync())
		{
			rowCount++;
			Assert.Equal("content-pull", reader.GetString(0));
			Assert.Equal("queued", reader.GetString(1));
		}

		Assert.Equal(1, rowCount);
	}

	[Fact]
	public async Task PostPull_WithViewerRole_Returns403()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/compliance-content/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task GetPulls_ReturnsHistoryNewestFirst()
	{
		ComplianceContentRepository repository = new(_fixture.ConnectionString);
		await repository.PutConfigAsync("https://example.internal/compliance-content.git", "branch", "main", CancellationToken.None);
		await repository.RecordPullAsync(null, "branch", "main", "commit-1", ComplianceContentPullStatuses.Succeeded, null, "alice", CancellationToken.None);
		await repository.RecordPullAsync(null, "branch", "main", null, ComplianceContentPullStatuses.Failed, "git fetch failed", "alice", CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/compliance-content/pulls");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] items = [.. document.RootElement.EnumerateArray()];
		Assert.Equal(2, items.Length);
		Assert.Equal("failed", items[0].GetProperty("status").GetString());
		Assert.Equal("succeeded", items[1].GetProperty("status").GetString());
	}

	[Fact]
	public async Task GetProfiles_ReturnsInventorySeededByReplaceAll()
	{
		ProfileRepository repository = new(_fixture.ConnectionString);
		await repository.ReplaceAllAsync(
			[new ProfileUpsert("dod-vsphere-8-esxi-stig", "DoD vSphere 8 ESXi STIG", "1.0", "abc123", ProfileStates.Current)],
			CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/profiles");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] items = [.. document.RootElement.EnumerateArray()];
		Assert.Single(items);
		Assert.Equal("dod-vsphere-8-esxi-stig", items[0].GetProperty("profile_key").GetString());
		Assert.Equal("current", items[0].GetProperty("state").GetString());
	}
}
