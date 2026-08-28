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
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Api.Contracts;
using Waypoint.Core.Components;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
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
/// Issue #731's round-6 rehearsal: the full operator flow through the real API, no
/// fixture shortcuts for the activation step. Round-5 live-lab validation (epic #726)
/// proved content promotes (12 execution profiles) and components link, but
/// <c>baselines</c> stayed empty forever because nothing ever called
/// <see cref="ICatalogRepository.PromoteCandidateAsync"/>'s downstream
/// <c>CreateStagedBaselineAsync</c>/<c>ActivateAsync</c> pair through an HTTP route.
/// This test proves the closed loop: stage content (invented semantic candidate,
/// mirroring <c>ContentPullJobHandler</c>'s own promotion shape) -&gt; promote to a
/// catalog execution profile -&gt; stage a content revision -&gt; create+activate a baseline
/// via <c>BaselinesController</c> (the new surface, not a repository bypass) -&gt;
/// discover-link a component through the real linkage resolver (issue #985's mechanism,
/// reused from <see cref="RunPlanPreviewTests"/>) -&gt; <c>POST /runs/plan-preview</c>
/// reports <c>is_runnable=true</c>.
///
/// Deliberately vCenter, not ESXi: issue #998 (ESXi minor-vs-patch granularity) and
/// issue #986 (multi-release collision) are sibling design gaps this slice does not
/// solve -- an ESXi host may still land unplannable even after activation lands. This
/// rehearsal picks vCenter (whole-appliance VMware-transport selector, single content
/// release, no multi-source collision) so the activation wiring itself, not those
/// separate open granularity problems, is what the test proves.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class BaselineActivationEndToEndTests : IAsyncLifetime
{
	private sealed class EndToEndApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public EndToEndApiFactory(string connectionString)
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

				services.AddSingleton<IBaselineRepository>(new BaselineRepository(_connectionString));
				services.AddSingleton<BaselineActivationService>();

				services.AddSingleton(new Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository(_connectionString));
				services.AddSingleton<Waypoint.Infrastructure.ConfigDocs.PlanConfigResolutionService>();
				services.AddSingleton<ScanPlannerService>();
				services.AddSingleton<Waypoint.Core.Scans.IScanPlanRepository>(new ScanPlanRepository(_connectionString));
				services.AddSingleton<RunPlanPreviewService>();

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
	private EndToEndApiFactory _factory = null!;
	private HttpClient _client = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private ComponentRepository _components = null!;

	public BaselineActivationEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new EndToEndApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);
		_components = new ComponentRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				baselines, content_revisions,
				catalog_credential_requirements, catalog_execution_profiles, catalog_report_groups,
				catalog_content_releases, catalog_components, catalog_product_versions, catalog_products,
				catalog_source_revisions, components, run_scope_snapshots, scan_plan_items, scan_plans,
				jobs, runs, target_credential_bindings, targets, sites
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private static HttpRequestMessage WithRole(HttpMethod method, string url, string role)
	{
		HttpRequestMessage request = new(method, url);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return request;
	}

	private static SemanticCandidate VCenterCandidate() => new(
		"vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", "vsphere", "8.0.3", "stig", "vcenter", "vCenter Server",
		"vmware", "vcenter", null,
		IsAggregate: false, Title: "vCenter STIG", ManifestVersion: "2.3.0",
		Inputs: [], Supports: [], Depends: [], ContentDigest: "invented0000000000000000000000000000000000000000000000000000");

	private static CatalogPromotionRequest VCenterPromotionRequest() => new(
		SourceRevisionKey: "compliance-content",
		Vendor: "VMware vSphere",
		ProductDisplayName: "VMware vSphere",
		ProductVersionDisplayName: "vSphere 8.0 Update 3",
		ContentReleaseDisplayName: "stig 8.0.3",
		ReportGroupKey: "vcenter-stig",
		ReportGroupDisplayName: "vCenter STIG",
		ReportGroupPriority: 3,
		OutputKind: CatalogOutputKinds.HdfAndCkl);

	[Fact]
	public async Task StageContentPromoteActivateViaApi_DiscoverLink_PlanPreviewReportsRunnable()
	{
		// 1. Stage content: an invented promoted semantic candidate, mirroring what
		// ContentPullJobHandler's own pipeline produces after a real pull/parse/diff
		// (issue #730's own machinery -- out of scope here) -- and promote it into the
		// catalog execution-profile tables, exactly like round-5's live-lab pull did.
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(
			VCenterCandidate(), VCenterPromotionRequest(), CancellationToken.None);
		Assert.NotNull(promotion.ExecutionProfileId);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;

		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(profileDetail);
		Guid catalogComponentId = profileDetail!.Component.Id;
		string exactVersionKey = profileDetail.ProductVersion.VersionKey;

		// 2. Stage an immutable content revision (what a real content-pull records
		// alongside promotion, issue #731's RecordStagedRevisionAsync) then create+
		// activate a baseline through the NEW BaselinesController API surface -- the
		// exact gap round-5 found: nothing did this before this slice.
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
			"commit-e2e", "digest-e2e", "revisions/e2e", CancellationToken.None);

		HttpRequestMessage createRequest = WithRole(HttpMethod.Post, "/api/v1/baselines", "Admin");
		createRequest.Content = JsonContent.Create(new
		{
			content_revision_id = revision.Id,
			catalog_execution_profile_id = executionProfileId,
		}, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage createResponse = await _client.SendAsync(createRequest);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		BaselineResponse? staged = await createResponse.Content.ReadFromJsonAsync<BaselineResponse>(Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.NotNull(staged);
		Assert.Equal(BaselineStatuses.Staged, staged!.Status);

		HttpRequestMessage activateRequest = WithRole(HttpMethod.Post, $"/api/v1/baselines/{staged.Id}/activate", "Admin");
		activateRequest.Content = JsonContent.Create(new { confirmation = "ACTIVATE" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage activateResponse = await _client.SendAsync(activateRequest);
		Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
		BaselineResponse? active = await activateResponse.Content.ReadFromJsonAsync<BaselineResponse>(Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.Equal(BaselineStatuses.Active, active!.Status);

		Baseline? activeFromRepo = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(activeFromRepo);
		Assert.Equal(Guid.Parse(staged.Id), activeFromRepo!.Id);

		// 3. Discover-link a component through the REAL linkage resolver (issue #985) --
		// only the catalog key + exact version fact a live discovery pass would report,
		// no CatalogComponentId shortcut.
		Guid siteId = await CreateSiteAsync("e2e-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		DiscoveredComponent discovered = new(
			CatalogComponentKey: "vcenter", VendorIdentity: "e2e-vcenter", DisplayName: "vcsa-01.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: exactVersionKey);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> ambiguities) =
			await Waypoint.Infrastructure.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [discovered], CancellationToken.None);
		Assert.Empty(ambiguities);
		Assert.Equal(catalogComponentId, linked.Single().CatalogComponentId);

		await _components.UpsertDiscoveredAsync(targetId, linked, CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();
		Assert.Equal(catalogComponentId, seeded.CatalogComponentId);

		// 4. The round-6 proof: plan-preview through the API now reports runnable end to
		// end -- through the API-driven activation, not a repository bypass.
object scopeBody = new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		};
		HttpRequestMessage previewRequest = WithRole(HttpMethod.Post, "/api/v1/runs/plan-preview", "Cyber");
		previewRequest.Content = new StringContent(
			JsonSerializer.Serialize(new { scope = JsonSerializer.Serialize(scopeBody) }), Encoding.UTF8, "application/json");

		HttpResponseMessage previewResponse = await _client.SendAsync(previewRequest);
		Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
		using JsonDocument previewBody = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
		Assert.Empty(previewBody.RootElement.GetProperty("scope_omissions").EnumerateArray());
		Assert.Single(previewBody.RootElement.GetProperty("items").EnumerateArray());
		Assert.True(previewBody.RootElement.GetProperty("is_runnable").GetBoolean());
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}" });
		response.EnsureSuccessStatusCode();
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
		command.Parameters.AddWithValue($"e2e-cred-{Guid.NewGuid():N}");
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
}
