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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #513: <c>GET /dashboard</c> end to end against real Postgres and a real
/// (temp-directory) artifact store, mirroring <see cref="RunArtifactsApiTests"/>'s
/// seeding conventions for jobs/HDF. Covers the role gate, empty-fleet shape, per-site
/// CAT-count rollup from a completed scan run's HDF, and the two in-scope ATTENTION
/// signals (credential auth-failing, stale inventory). The dormant depot-token-expiring
/// signal type has no test here because it never fires -- see
/// <c>DashboardAggregateService</c>'s doc comment.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory and removes the temp dir.
public sealed class DashboardApiTests : IAsyncLifetime, IDisposable
{
	private sealed class DashboardApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _artifactStorePath;

		public DashboardApiFactory(string connectionString, string artifactStorePath)
		{
			_connectionString = connectionString;
			_artifactStorePath = artifactStorePath;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureAppConfiguration((_, configBuilder) =>
			{
				configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Scans:ArtifactStorePath"] = _artifactStorePath,
					["Discovery:StaleAfterMinutes"] = "60",
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

				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));

				foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository) })
				{
					ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
					if (descriptor != null)
					{
						services.Remove(descriptor);
					}
				}

				services.AddSingleton(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
				services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
			});
		}
	}

	private static readonly string[] ComplianceRunTypesForAssertion = ["scan", "remediate"];

	private readonly PostgresFixture _fixture;
	private readonly string _artifactStorePath = Directory.CreateTempSubdirectory("wp-dashboard-api").FullName;
	private DashboardApiFactory _factory = null!;
	private HttpClient _client = null!;

	public DashboardApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new DashboardApiFactory(_fixture.ConnectionString, _artifactStorePath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		Directory.Delete(_artifactStorePath, recursive: true);
	}

#pragma warning restore CA1001

	[Fact]
	public async Task Get_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/dashboard");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Get_WithEmptyFleet_ReturnsZeroedKpisAndNoAttention()
	{
		JsonElement root = await GetDashboardAsync();

		Assert.Equal(0, root.GetProperty("targets").GetProperty("total").GetInt32());
		Assert.False(root.GetProperty("findings").GetProperty("counts_available").GetBoolean());
		Assert.Empty(root.GetProperty("sites").EnumerateArray());
		Assert.Empty(root.GetProperty("recent_runs").EnumerateArray());
		Assert.Empty(root.GetProperty("attention").EnumerateArray());
	}

	[Fact]
	public async Task Get_WithCompletedScanRun_RollsUpSitePostureAndFleetFindings()
	{
		(Guid siteId, Guid targetId) = await CreateSiteAndTargetAsync("dash-site");
		Guid runId = await CreateCompletedScanRunAsync(siteId);
		Guid jobId = await FanOutScanJobAsync(runId, targetId);
		WriteHdf(jobId, catI: 1, catII: 2, catIII: 0);

		JsonElement root = await GetDashboardAsync();

		JsonElement site = root.GetProperty("sites").EnumerateArray().Single();
		Assert.Equal(siteId.ToString(), site.GetProperty("id").GetString());
		Assert.Equal(1, site.GetProperty("target_count").GetInt32());
		Assert.Equal(1, site.GetProperty("cat_i_open").GetInt32());
		Assert.Equal(2, site.GetProperty("cat_ii_open").GetInt32());
		Assert.Equal(0, site.GetProperty("cat_iii_open").GetInt32());
		Assert.Equal(0.0, site.GetProperty("compliance_percent").GetDouble());

		JsonElement findings = root.GetProperty("findings");
		Assert.True(findings.GetProperty("counts_available").GetBoolean());
		Assert.Equal(1, findings.GetProperty("cat_i_open").GetInt32());
		Assert.Equal(2, findings.GetProperty("cat_ii_open").GetInt32());

		JsonElement run = root.GetProperty("recent_runs").EnumerateArray().Single();
		Assert.Equal(runId.ToString(), run.GetProperty("id").GetString());
		Assert.Equal("completed", run.GetProperty("state").GetString());
	}

	[Fact]
	public async Task Get_WithOperationalRunsNewerThanCompliance_StillReturnsComplianceRuns()
	{
		// Issue #717 starvation regression: a burst of operational runs newer than the
		// compliance runs must NOT push scan/remediate off the capped RECENT RUNS list.
		// Older compliance runs first, then a wall of newer operational runs that would
		// have filled every one of the 8 returned slots under the old unfiltered cap.
		DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddHours(-2);
		Guid scanRunId = await CreateRunAtAsync("scan", baseTime);
		Guid remediateRunId = await CreateRunAtAsync("remediate", baseTime.AddMinutes(1));
		for (int i = 0; i < 15; i++)
		{
			string operationalType = i % 2 == 0 ? "discover" : "credential-test";
			await CreateRunAtAsync(operationalType, baseTime.AddMinutes(10 + i));
		}

		JsonElement root = await GetDashboardAsync();

		List<string> ids = [.. root.GetProperty("recent_runs").EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];
		List<string> runTypes = [.. root.GetProperty("recent_runs").EnumerateArray().Select(r => r.GetProperty("run_type").GetString()!)];

		Assert.Contains(scanRunId.ToString(), ids);
		Assert.Contains(remediateRunId.ToString(), ids);
		Assert.All(runTypes, t => Assert.Contains(t, ComplianceRunTypesForAssertion));
		Assert.DoesNotContain("discover", runTypes);
		Assert.DoesNotContain("credential-test", runTypes);
	}

	[Fact]
	public async Task Get_WithAuthFailingCredential_SurfacesAttentionSignal()
	{
		await CreateAuthFailingCredentialAsync("dash-svc-cred");

		JsonElement root = await GetDashboardAsync();

		JsonElement attention = root.GetProperty("attention").EnumerateArray().Single();
		Assert.Equal("credential_auth_failing", attention.GetProperty("kind").GetString());
		Assert.Contains("dash-svc-cred", attention.GetProperty("text").GetString());
	}

	[Fact]
	public async Task Get_WithStaleTarget_SurfacesAttentionSignal()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("stale-site");
		await StampLastRefreshedAsync(targetId, DateTimeOffset.UtcNow.AddHours(-6));

		JsonElement root = await GetDashboardAsync();

		JsonElement attention = root.GetProperty("attention").EnumerateArray().Single();
		Assert.Equal("inventory_stale", attention.GetProperty("kind").GetString());
	}

	// Issue #524: negative-case fixtures. The positive-path tests above prove the
	// filters EMIT; without these, a mutation that dropped either predicate entirely
	// (e.g. `Where(_ => true)`) would pass the suite -- nothing here ever asserted the
	// excluded row stays excluded.

	[Fact]
	public async Task Get_WithHealthyCredential_DoesNotSurfaceAttentionSignal()
	{
		await CreateCredentialWithHealthAsync("dash-healthy-cred", "valid");

		JsonElement root = await GetDashboardAsync();

		Assert.Empty(root.GetProperty("attention").EnumerateArray());
	}

	[Fact]
	public async Task Get_WithFreshTarget_DoesNotSurfaceAttentionSignal()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("fresh-site");
		await StampLastRefreshedAsync(targetId, DateTimeOffset.UtcNow);

		JsonElement root = await GetDashboardAsync();

		Assert.Empty(root.GetProperty("attention").EnumerateArray());
	}

	/// <summary>
	/// Pins the <c>Discovery:StaleAfterMinutes</c> boundary itself (the fixture host
	/// configures 60 minutes -- see <see cref="DashboardApiFactory"/>), mirroring
	/// issue #426's precedent in <c>AutoDiscoverOnScanInitiationTests</c>:
	/// <c>DashboardAggregateService.BuildStaleInventoryAttention</c> compares with
	/// strict <c>&lt;</c>, so equality falls on the FRESH side. A 30-second margin on
	/// each side of the 60-minute edge brackets that boundary without racing the test's
	/// clock read against the service's own <c>DateTimeOffset.UtcNow</c> read.
	/// </summary>
	[Fact]
	public async Task Get_WithTargetJustInsideStaleWindow_DoesNotSurfaceAttentionSignal()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("boundary-fresh-site");
		await StampLastRefreshedAsync(targetId, DateTimeOffset.UtcNow.AddMinutes(-60).AddSeconds(30));

		JsonElement root = await GetDashboardAsync();

		Assert.Empty(root.GetProperty("attention").EnumerateArray());
	}

	[Fact]
	public async Task Get_WithTargetJustOutsideStaleWindow_SurfacesAttentionSignal()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("boundary-stale-site");
		await StampLastRefreshedAsync(targetId, DateTimeOffset.UtcNow.AddMinutes(-60).AddSeconds(-30));

		JsonElement root = await GetDashboardAsync();

		JsonElement attention = root.GetProperty("attention").EnumerateArray().Single();
		Assert.Equal("inventory_stale", attention.GetProperty("kind").GetString());
	}

	private async Task<JsonElement> GetDashboardAsync()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/dashboard");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.Clone();
	}

	private void WriteHdf(Guid jobId, int catI, int catII, int catIII)
	{
		List<object> controls = [];
		for (int i = 0; i < catI; i++)
		{
			controls.Add(new { tags = new { severity = "high" }, results = new[] { new { status = "failed" } } });
		}

		for (int i = 0; i < catII; i++)
		{
			controls.Add(new { tags = new { severity = "medium" }, results = new[] { new { status = "failed" } } });
		}

		for (int i = 0; i < catIII; i++)
		{
			controls.Add(new { tags = new { severity = "low" }, results = new[] { new { status = "failed" } } });
		}

		object hdf = new
		{
			platform = new { name = "vmware_vsphere", release = "invented" },
			profiles = new[] { new { name = "invented-stub-profile", version = "0.0.0", controls = controls.ToArray() } },
		};

		string path = Path.Combine(_artifactStorePath, $"{jobId:N}.json");
		File.WriteAllText(path, JsonSerializer.Serialize(hdf));
	}

	private async Task<(Guid SiteId, Guid TargetId)> CreateSiteAndTargetAsync(string namePrefix)
	{
		SiteRepository sites = new(_fixture.ConnectionString);
		TargetRepository targets = new(_fixture.ConnectionString);

		Guid siteId = (await sites.CreateAsync($"{namePrefix}-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(Waypoint.Core.Sites.TargetWriteOutcome outcome, Guid? targetId) = await targets.CreateAsync(
			siteId, "vsphere", $"{namePrefix}-target-{Guid.NewGuid():N}", """{"host":"vcsa-01.example.internal"}""", null, CancellationToken.None);
		Assert.Equal(Waypoint.Core.Sites.TargetWriteOutcome.Ok, outcome);
		return (siteId, targetId!.Value);
	}

	private async Task<Guid> CreateCompletedScanRunAsync(Guid siteId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO runs (run_type, scope, state, initiated_by, started_at, completed_at)
			VALUES ('scan', $1::jsonb, 'completed', 'tester', now(), now())
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(JsonSerializer.Serialize(new { site_id = siteId }));
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// Seeds a run with an explicit <c>created_at</c> so a starvation-regression test can
	/// interleave operational and compliance runs on a known timeline (issue #717).
	/// </summary>
	private async Task<Guid> CreateRunAtAsync(string runType, DateTimeOffset createdAt)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO runs (run_type, scope, state, initiated_by, created_at, started_at, completed_at)
			VALUES ($1, '{}'::jsonb, 'completed', 'tester', $2, $2, $2)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runType);
		insert.Parameters.AddWithValue(createdAt);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> FanOutScanJobAsync(Guid runId, Guid targetId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, target_id, target_name, priority, state, payload)
			VALUES ($1, 'scan', $2, $3, 3, 'uploaded', $4::jsonb)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(targetId);
		insert.Parameters.AddWithValue($"target-{targetId:N}");
		insert.Parameters.AddWithValue(JsonSerializer.Serialize(new { target_id = targetId }));
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task CreateAuthFailingCredentialAsync(string name) =>
		await CreateCredentialWithHealthAsync(name, "auth_failing");

	private async Task CreateCredentialWithHealthAsync(string name, string health)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type, owner, health) VALUES ($1, 'ssh', 'shared', $2)", connection);
		insert.Parameters.AddWithValue(name);
		insert.Parameters.AddWithValue(health);
		await insert.ExecuteNonQueryAsync();
	}

	private async Task StampLastRefreshedAsync(Guid targetId, DateTimeOffset lastRefreshed)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new("UPDATE targets SET last_refreshed = $1 WHERE id = $2", connection);
		update.Parameters.AddWithValue(lastRefreshed);
		update.Parameters.AddWithValue(targetId);
		await update.ExecuteNonQueryAsync();
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE jobs, runs, targets, sites, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
