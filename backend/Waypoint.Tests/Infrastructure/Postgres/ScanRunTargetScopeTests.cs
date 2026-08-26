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
using Waypoint.Core.Components;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #733 (epic #726 Wave 2, ADR-0023), end to end against real Postgres and the
/// real API: <c>POST /api/v1/runs</c> for a scan run additionally accepts
/// <c>scope.target_scope</c> (tri-state all/explicit), resolves it via
/// <see cref="ScopeResolutionService"/> against the merged component model (PR #839),
/// and freezes the requested-versus-resolved scope into <c>run_scope_snapshots</c>
/// (migration 0056) BEFORE any job is fanned out. This is additive alongside the
/// shipped target-granular <c>target_ids</c>/<c>profile_id</c> shape --
/// <see cref="ScanRunFanOutTests"/> continues to cover that legacy path unmodified.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ScanRunTargetScopeTests : IAsyncLifetime
{
	private sealed class TargetScopeApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public TargetScopeApiFactory(string connectionString)
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

				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));
				services.AddSingleton<Waypoint.Core.ComplianceContent.IProfileRepository>(
					new Waypoint.Infrastructure.ComplianceContent.ProfileRepository(_connectionString));
				services.AddSingleton<IComponentRepository>(new ComponentRepository(_connectionString));
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
				services.AddSingleton<ScopeResolutionService>();
				services.AddSingleton<IRunScopeSnapshotRepository>(new RunScopeSnapshotRepository(_connectionString));

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
	private TargetScopeApiFactory _factory = null!;
	private HttpClient _client = null!;
	private ProfileRepository _profiles = null!;
	private ComponentRepository _components = null!;
	private Guid _profileId;

	public ScanRunTargetScopeTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new TargetScopeApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
		_components = new ComponentRepository(_fixture.ConnectionString);

		_profiles = new ProfileRepository(_fixture.ConnectionString);
		await _profiles.ReplaceAllAsync(
			[new ProfileUpsert("vsphere-targetscope-profile", "vSphere Target-Scope Test Profile", "1.0.0", "invented-commit-targetscope", ProfileStates.Current)],
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
	public async Task CreateScanRun_WithExplicitEmptyTargetScope_PersistsEmptySnapshot_NeverWidensJobFanOut()
	{
		// Issue #733 AC "No scan silently falls back from an empty explicit selection
		// to the whole site" -- the legacy target_ids/profile_id fan-out (unaffected by
		// this slice) still creates one job per target; the NEW assertion is that the
		// scope snapshot itself records zero resolved components, not the whole site's.
		Guid siteId = await CreateSiteAsync("empty-explicit-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			profile_id = _profileId,
			target_scope = new { mode = "explicit", component_ids = Array.Empty<Guid>() },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		RunScopeSnapshotRepository snapshots = new(_fixture.ConnectionString);
		RunScopeSnapshot? snapshot = await snapshots.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(snapshot);
		Assert.Equal("explicit", snapshot!.RequestedMode);
		Assert.Empty(snapshot.ResolvedComponentIds);
		Assert.Empty(snapshot.Omissions);
	}

	[Fact]
	public async Task CreateScanRun_WithExplicitScopeNamingAResolvableComponent_PersistsRequestedAndResolvedScope()
	{
		Guid siteId = await CreateSiteAsync("resolvable-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-9001", "esxi-01.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			profile_id = _profileId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		// Catalog-linked at the matching exact version -- resolves as runnable, and
		// the snapshot records BOTH the requested id and the resolved id (same value
		// here, but proving the round trip persists both facets independently,
		// issue #733 AC "run history can display requested versus resolved scope").
		// Fan-out itself (job creation) is unaffected by target_scope in this slice --
		// job fan-out stays the legacy target-granular shape until #735-#737's
		// component-job layer lands.
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		RunScopeSnapshotRepository snapshots = new(_fixture.ConnectionString);
		RunScopeSnapshot? snapshot = await snapshots.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(snapshot);
		Assert.Contains(seeded.Id.ToString(), snapshot!.RequestedScopeJson);
		Assert.Equal([seeded.Id], snapshot.ResolvedComponentIds);
		Assert.Empty(snapshot.Omissions);
	}

	/// <summary>Seeds one complete compatible catalog chain -- mirrors <see cref="ScopeResolutionServiceTests.SeedCompatibleCatalogComponentAsync"/> for this HTTP-level suite.</summary>
	private async Task<Guid> SeedCompatibleCatalogComponentAsync()
	{
		CatalogRepository catalog = new(_fixture.ConnectionString);
		CatalogSourceRevision source = await catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Stig, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		await catalog.CreateExecutionProfileAsync(catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		return catalogComponent.Id;
	}

	[Fact]
	public async Task CreateScanRun_WithExplicitScopeNamingOnlyUnresolvableComponents_Returns400NoRunnableComponent()
	{
		// ADR-0023 "initiation fails only when refresh validates no runnable
		// component": a non-empty request that resolves to zero runnable components
		// must reject before any run/job row exists.
		Guid siteId = await CreateSiteAsync("unrunnable-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			profile_id = _profileId,
			target_scope = new { mode = "explicit", component_ids = new[] { Guid.NewGuid() } },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "no_runnable_component");

		HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, "/api/v1/runs", "Viewer", body: null);
		using JsonDocument list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.Empty(list.RootElement.EnumerateArray());
	}

	[Fact]
	public async Task CreateScanRun_WithInvalidTargetScopeMode_Returns400ValidationError()
	{
		Guid siteId = await CreateSiteAsync("bad-mode-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			profile_id = _profileId,
			target_scope = new { mode = "bogus" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_WithoutTargetScope_NeverWritesASnapshotRow()
	{
		// Backward compatibility: a request that only uses the legacy shape must not
		// gain a spurious snapshot row.
		Guid siteId = await CreateSiteAsync("legacy-only-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		RunScopeSnapshotRepository snapshots = new(_fixture.ConnectionString);
		Assert.Null(await snapshots.GetForRunAsync(runId, CancellationToken.None));
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
		command.Parameters.AddWithValue($"targetscope-cred-{Guid.NewGuid():N}");
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
			"TRUNCATE TABLE run_scope_snapshots, component_observations, components, jobs, runs, targets, sites, " +
			"catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components, " +
			"catalog_product_versions, catalog_products, catalog_source_revisions RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
