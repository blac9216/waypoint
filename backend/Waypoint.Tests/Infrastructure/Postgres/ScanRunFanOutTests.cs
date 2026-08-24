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

using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #273 (first slice of the #23 split), end to end against real Postgres:
/// <c>POST /api/v1/runs</c> for a scan run validates <c>scope.site_id</c>/target refs
/// against <see cref="SiteRepository"/>/<see cref="TargetRepository"/>, fans out one
/// <c>scan</c> job per target ordered by catalog priority, and isolates per-target
/// failures as independent job rows. Execution itself (InSpec) is #274 -- not
/// exercised here.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ScanRunFanOutTests : IAsyncLifetime
{
	private sealed class ScanRunApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ScanRunApiFactory(string connectionString)
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

				// Same "point the real repositories at the fixture container" pattern
				// SitesTargetsApiTests uses, extended to the split job repositories
				// (issue #415) so RunsController's fan-out call lands in the same
				// database the assertions below read back from.
				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));

				// Issue #639: RunCreationService.CreateScanRunAsync now resolves
				// scope.profile_id through IProfileRepository -- same "point the real
				// repository at the fixture container" pattern as Site/TargetRepository
				// above, otherwise it resolves against appsettings.json's base connection
				// string (a different database than the fixture) and every scan-run POST 500s.
				services.AddSingleton<Waypoint.Core.ComplianceContent.IProfileRepository>(
					new Waypoint.Infrastructure.ComplianceContent.ProfileRepository(_connectionString));

				foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository) })
				{
					var jobsDescriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
					if (jobsDescriptor != null)
					{
						services.Remove(jobsDescriptor);
					}
				}

				services.AddSingleton(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
				services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ScanRunApiFactory _factory = null!;
	private HttpClient _client = null!;
	private ProfileRepository _profiles = null!;

	/// <summary>
	/// Issue #639: every scan run now requires <c>scope.profile_id</c> to reference an
	/// installed profile, so each test seeds exactly one via <see cref="IProfileRepository.ReplaceAllAsync"/>
	/// (the same upsert content-pull uses) and reads its surrogate id back through
	/// <see cref="_profiles"/> rather than duplicating a raw INSERT.
	/// </summary>
	private Guid _profileId;

	public ScanRunFanOutTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new ScanRunApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();

		_profiles = new ProfileRepository(_fixture.ConnectionString);
		await _profiles.ReplaceAllAsync(
			[new ProfileUpsert("vsphere-fanout-profile", "vSphere Fan-out Test Profile", "1.0.0", "invented-commit-fanout", ProfileStates.Current)],
			CancellationToken.None);
		_profileId = (await _profiles.ListAsync(CancellationToken.None)).Single().Id;
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	[Fact]
	public async Task CreateScanRun_FansOutOneJobPerTarget_OrderedByCatalogPriority()
	{
		Guid siteId = await CreateSiteAsync("fanout-site");
		Guid nsx = await CreateTargetAsync(siteId, "nsx-api", "nsx-01", """{"host":"nsx-01.example.internal"}""");
		Guid vcsa = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		Guid ssh = await CreateTargetAsync(siteId, "ssh", "photon-01", """{"host":"photon-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string runId = created.RootElement.GetProperty("run_id").GetString()!;

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/jobs", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, jobsResponse.StatusCode);
		using JsonDocument jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
		JsonElement[] allRows = jobs.RootElement.EnumerateArray().ToArray();

		// Issue #259: the never-discovered vsphere target also gets an auto-queued
		// discover job in the same run, so the full row set is 4 -- scan fan-out is
		// this test's actual subject, so it filters to job_type = "scan" rather than
		// asserting the whole run has exactly 3 rows. The discover job itself is
		// covered by AutoDiscoverOnScanInitiationTests.
		Assert.Equal(4, allRows.Length);
		JsonElement[] rows = allRows.Where(row => row.GetProperty("job_type").GetString() == "scan").ToArray();
		Assert.Equal(3, rows.Length);

		// Priority-ordered: NSX(1) before vCenter/vsphere(3) before SRG/ssh(6) --
		// GetJobsForRunAsync orders "by priority then created_at".
		short[] priorities = rows.Select(row => row.GetProperty("priority").GetInt16()).ToArray();
		Assert.Equal([(short)1, (short)3, (short)6], priorities);

		string[] targetIds = rows.Select(row => row.GetProperty("target_id").GetString()!).ToArray();
		Assert.Contains(nsx.ToString(), targetIds);
		Assert.Contains(vcsa.ToString(), targetIds);
		Assert.Contains(ssh.ToString(), targetIds);
	}

	[Fact]
	public async Task CreateScanRun_SiteWithMoreThanTwoHundredTargets_FansOutAll()
	{
		// Issue #279 regression: ResolveScanTargetsAsync used to fetch a full-site
		// scan's targets via PageRequest { Limit = 200 }, which PageRequest.Limit's
		// own MaxLimit silently clamped to -- a site with more than 200 targets fanned
		// out over only the first page, no error/warning. 250 invented targets (one
		// over two full pages of the old limit) proves the fix fetches every row.
		const int TargetCount = 250;
		Guid siteId = await CreateSiteAsync("large-site");

		// Issue #585: one shared ssh credential bound to every target (via the 0043
		// dual-write mirror) so the full-site scan resolves srg-ssh for all 250 rows.
		Guid sharedCredentialId = await SeedKindCompatibleCredentialAsync(TargetKinds.Ssh);
		TargetRepository targets = new(_fixture.ConnectionString);
		for (int i = 0; i < TargetCount; i++)
		{
			string name = $"esxi-{i:D4}";
			string connectionJson = $$"""{"host":"esxi-{{i:D4}}.example.internal"}""";
			(TargetWriteOutcome outcome, Guid? id) = await targets.CreateAsync(
				siteId, TargetKinds.Ssh, name, connectionJson, sharedCredentialId, CancellationToken.None);
			Assert.Equal(TargetWriteOutcome.Ok, outcome);
			Assert.NotNull(id);
		}

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string runId = created.RootElement.GetProperty("run_id").GetString()!;

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(DISTINCT id) FROM jobs WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(Guid.Parse(runId));
		long distinctJobRows = (long)(await count.ExecuteScalarAsync())!;

		Assert.Equal(TargetCount, distinctJobRows);
	}

	[Fact]
	public async Task CreateScanRun_ScopedToTargetIds_OnlyFansOutThoseTargets()
	{
		Guid siteId = await CreateSiteAsync("scoped-site");
		Guid included = await CreateTargetAsync(siteId, "vsphere", "vcsa-included", """{"host":"vcsa-01.example.internal"}""");
		await CreateTargetAsync(siteId, "nsx-api", "nsx-excluded", """{"host":"nsx-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { included }, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string runId = created.RootElement.GetProperty("run_id").GetString()!;

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/jobs", "Viewer", body: null);
		using JsonDocument jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
		JsonElement[] rows = jobs.RootElement.EnumerateArray().ToArray();

		// Issue #259: the included (never-discovered) vsphere target also gets its own
		// auto-queued discover job in the same run -- still proves the scoping this
		// test is actually about, since every row must reference "included", never the
		// excluded nsx-api target.
		Assert.Equal(2, rows.Length);
		Assert.All(rows, row => Assert.Equal(included.ToString(), row.GetProperty("target_id").GetString()));
		Assert.Contains(rows, row => row.GetProperty("job_type").GetString() == "scan");
		Assert.Contains(rows, row => row.GetProperty("job_type").GetString() == "discover");
	}

	[Fact]
	public async Task CreateScanRun_MissingSiteId_Returns400()
	{
		HttpResponseMessage response = await PostRunAsync(new { });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_UnknownSiteId_Returns404()
	{
		HttpResponseMessage response = await PostRunAsync(new { site_id = Guid.NewGuid(), profile_id = _profileId });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task CreateScanRun_UnknownProfileId_Returns404()
	{
		Guid siteId = await CreateSiteAsync("unknown-profile-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = Guid.NewGuid() });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task CreateScanRun_MissingProfileId_Returns400()
	{
		Guid siteId = await CreateSiteAsync("missing-profile-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_UnknownTargetId_Returns404_NoRunCreated()
	{
		Guid siteId = await CreateSiteAsync("bad-target-site");
		Guid bogus = Guid.NewGuid();

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { bogus }, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");

		HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, "/api/v1/runs", "Viewer", body: null);
		using JsonDocument list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.Empty(list.RootElement.EnumerateArray());
	}

	[Fact]
	public async Task CreateScanRun_TargetFromAnotherSite_Returns404()
	{
		Guid siteA = await CreateSiteAsync("site-a");
		Guid siteB = await CreateSiteAsync("site-b");
		Guid targetInB = await CreateTargetAsync(siteB, "vsphere", "vcsa-b", """{"host":"vcsa-b.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteA, target_ids = new[] { targetInB }, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task CreateScanRun_SiteWithNoTargets_Returns400()
	{
		Guid siteId = await CreateSiteAsync("empty-site");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_BelowCyberRole_Returns403()
	{
		Guid siteId = await CreateSiteAsync("role-gate-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Viewer",
			new { run_type = "scan", scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }) });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task CreateScanRun_EachTargetIsAnIndependentJobRow()
	{
		// Isolation constraint (AC): each target's job is its own row, not a shared
		// one -- proven directly against the jobs table rather than inferred from the
		// API response, so a future accidental collapse to one job would fail here.
		Guid siteId = await CreateSiteAsync("isolation-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-02", """{"host":"vcsa-02.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		// Issue #259: scoped to job_type = 'scan' -- both never-discovered vsphere
		// targets also get their own auto-queued discover job in the same run (see
		// AutoDiscoverOnScanInitiationTests), which is a separate, unrelated isolation
		// property from the one this test asserts (each target's SCAN job is its own row).
		await using NpgsqlCommand count = new("SELECT count(DISTINCT id) FROM jobs WHERE run_id = $1 AND job_type = 'scan'", connection);
		count.Parameters.AddWithValue(runId);
		long distinctJobRows = (long)(await count.ExecuteScalarAsync())!;

		Assert.Equal(2, distinctJobRows);
	}

	private async Task<HttpResponseMessage> PostRunAsync(object scopeBody)
	{
		return await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(scopeBody) });
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}" });
		if (!response.IsSuccessStatusCode)
		{
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"CreateSiteAsync failed: {(int)response.StatusCode} {errorBody}");
		}
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	/// <summary>
	/// Issue #585: scan-run creation now rejects any target whose required credential
	/// purpose has no binding (400 <c>credential_binding_gaps</c>), so every target
	/// this fixture creates carries a kind-compatible credential as its
	/// <c>credential_ref</c> -- the 0043 dual-write mirrors it into the default-purpose
	/// binding the resolution step reads. Fan-out mechanics stay this class's subject;
	/// the resolution rules themselves are covered by
	/// <see cref="CredentialBindingResolutionTests"/>.
	/// </summary>
	private async Task<Guid> CreateTargetAsync(Guid siteId, string kind, string name, string connectionJson)
	{
		Guid credentialId = await SeedKindCompatibleCredentialAsync(kind);
		using JsonDocument connectionDocument = JsonDocument.Parse(connectionJson);
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/sites/{siteId}/targets");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		string body = JsonSerializer.Serialize(new { kind, name, connection = connectionDocument.RootElement, credential_ref = credentialId });
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");

		HttpResponseMessage response = await _client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> SeedKindCompatibleCredentialAsync(string kind)
	{
		string credentialType = kind switch
		{
			"vsphere" => "vcenter",
			"nsx-api" => "nsx",
			_ => "ssh",
		};
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type, username) VALUES ($1, $2, 'svc-scan@example.internal') RETURNING id", connection);
		command.Parameters.AddWithValue($"fanout-cred-{Guid.NewGuid():N}");
		command.Parameters.AddWithValue(credentialType);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await _client.SendAsync(request);
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE jobs, runs, targets, sites RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
