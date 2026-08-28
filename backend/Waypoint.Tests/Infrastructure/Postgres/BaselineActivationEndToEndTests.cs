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
				services.AddSingleton<IComponentRepository>(new ComponentRepository(_connectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_connectionString)));
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
		_components = new ComponentRepository(_fixture.ConnectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString));
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

	private static SemanticCandidate VmCandidate() => new(
		"vsphere/8.0.3/v2r3-stig/inspec/baseline/vm", "vsphere", "8.0.3", "stig", "vm", "Virtual Machine",
		"vmware", "vm", null,
		IsAggregate: false, Title: "VM STIG", ManifestVersion: "2.3.0",
		Inputs: [], Supports: [], Depends: [], ContentDigest: "invented0000000000000000000000000000000000000000000000000003");

	private static CatalogPromotionRequest VmPromotionRequest() => new(
		SourceRevisionKey: "compliance-content",
		Vendor: "VMware vSphere",
		ProductDisplayName: "VMware vSphere",
		ProductVersionDisplayName: "vSphere 8.0 Update 3",
		ContentReleaseDisplayName: "stig 8.0.3",
		ReportGroupKey: "vm-stig",
		ReportGroupDisplayName: "VM STIG",
		ReportGroupPriority: 3,
		OutputKind: CatalogOutputKinds.HdfAndCkl);

	private async Task ActivateBaselineAsync(Guid contentRevisionId, Guid executionProfileId)
	{
		HttpRequestMessage createRequest = WithRole(HttpMethod.Post, "/api/v1/baselines", "Admin");
		createRequest.Content = JsonContent.Create(new
		{
			content_revision_id = contentRevisionId,
			catalog_execution_profile_id = executionProfileId,
		}, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage createResponse = await _client.SendAsync(createRequest);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		BaselineResponse? staged = await createResponse.Content.ReadFromJsonAsync<BaselineResponse>(Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpRequestMessage activateRequest = WithRole(HttpMethod.Post, $"/api/v1/baselines/{staged!.Id}/activate", "Admin");
		activateRequest.Content = JsonContent.Create(new { confirmation = "ACTIVATE" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage activateResponse = await _client.SendAsync(activateRequest);
		Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
	}

	/// <summary>
	/// Issue #1000's own end-to-end proof -- the CONFIGURED-fact sibling of this class's
	/// round-6 discovered-fact rehearsal above. That test proves stage-content ->
	/// promote -> activate-baseline -> discover-link -> plan-preview-runnable for a
	/// component discovery itself can version. This proves the identical closing leg
	/// for a component discovery can NEVER version -- the synthetic vCenter root --
	/// where only an Admin <c>PUT /api/v1/components/{id}</c> configured-fact write can
	/// ever supply the version at all. Deliberately added as a sibling [Fact] in THIS
	/// class (not a separate Postgres-collection class): a separate class truncating
	/// the same catalog_* tables raced this class's own truncation nondeterministically
	/// during this issue's own development (xUnit's default class-level parallelism
	/// runs different Postgres-collection classes concurrently against the one shared
	/// database) -- same class means same fixture lifecycle, zero new cross-class
	/// truncation-race surface.
	///
	/// Before this issue: PUT wrote <c>configured_fact.exact_version</c> but never
	/// touched <c>catalog_component_id</c>, so this exact scenario produced
	/// <c>scope_omissions</c> with reason <c>catalog_incompatible</c> and detail "not
	/// linked to a known catalog component" even with the active baseline below already
	/// staged -- reproduced by hand against pre-fix code during this issue's own
	/// development (see PR description for the captured failing-test-first output).
	/// </summary>
	[Fact]
	public async Task ConfiguredExactVersion_OnVCenterRoot_LinksAndPlanPreviewReportsRunnable()
	{
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(VCenterCandidate(), VCenterPromotionRequest(), CancellationToken.None);
		Assert.NotNull(promotion.ExecutionProfileId);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;

		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(profileDetail);
		Guid catalogComponentId = profileDetail!.Component.Id;
		string exactVersionKey = profileDetail.ProductVersion.VersionKey;

		ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
			"commit-1000-vcenter", "digest-1000-vcenter", "revisions/1000-vcenter", CancellationToken.None);
		await ActivateBaselineAsync(revision.Id, executionProfileId);

		// Seed the component the way discovery ACTUALLY produces the synthetic vCenter
		// root (DiscoverJobHandler.MapToComponents): ExactVersion null, CatalogComponentId
		// null -- discovery structurally never supplies a version for this component.
		Guid siteId = await CreateSiteAsync("1000-vcenter-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-1000", """{"host":"vcsa-1000.example.internal"}""");

		DiscoveredComponent rootMapping = new(
			CatalogComponentKey: CatalogSelectorKinds.VCenter, VendorIdentity: null, DisplayName: "vCenter Server",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: null);
		await _components.UpsertDiscoveredAsync(targetId, [rootMapping], CancellationToken.None);

		Component beforeConfigure = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();
		Assert.Null(beforeConfigure.CatalogComponentId);
		Assert.Null(beforeConfigure.ConfiguredFact);

		// THIS issue's fix: PUT the Admin-configured exact version onto the
		// undiscoverable-version root component.
		HttpRequestMessage putRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{beforeConfigure.Id}", "Admin");
		putRequest.Content = JsonContent.Create(new { exact_version = exactVersionKey }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage putResponse = await _client.SendAsync(putRequest);
		Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

		Component afterConfigure = (await _components.GetAsync(beforeConfigure.Id, CancellationToken.None))!;
		Assert.Equal(catalogComponentId, afterConfigure.CatalogComponentId);
		Assert.False(afterConfigure.FactConflict);

		// The round-6-equivalent closing proof: plan-preview through the real API
		// reports runnable for the configured-fact-linked component.
		object scopeBody = new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { afterConfigure.Id } },
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

	/// <summary>Same proof, for a VM component -- issue #1000's other named-impacted component (#740).</summary>
	[Fact]
	public async Task ConfiguredExactVersion_OnVm_LinksAndMatcherReportsCompatible()
	{
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(VmCandidate(), VmPromotionRequest(), CancellationToken.None);
		Assert.NotNull(promotion.ExecutionProfileId);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;

		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(profileDetail);
		Guid catalogComponentId = profileDetail!.Component.Id;
		string exactVersionKey = profileDetail.ProductVersion.VersionKey;

		ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
			"commit-1000-vm", "digest-1000-vm", "revisions/1000-vm", CancellationToken.None);
		await ActivateBaselineAsync(revision.Id, executionProfileId);

		Guid siteId = await CreateSiteAsync("1000-vm-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-1000-vm", """{"host":"vcsa-1000-vm.example.internal"}""");

		DiscoveredComponent vmMapping = new(
			CatalogComponentKey: CatalogSelectorKinds.Vm, VendorIdentity: "vm-1000-configured", DisplayName: "vm-1000",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: null);
		await _components.UpsertDiscoveredAsync(targetId, [vmMapping], CancellationToken.None);

		Component beforeConfigure = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();
		Assert.Null(beforeConfigure.CatalogComponentId);

		HttpRequestMessage putRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{beforeConfigure.Id}", "Admin");
		putRequest.Content = JsonContent.Create(new { exact_version = exactVersionKey }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage putResponse = await _client.SendAsync(putRequest);
		Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

		Component afterConfigure = (await _components.GetAsync(beforeConfigure.Id, CancellationToken.None))!;
		Assert.Equal(catalogComponentId, afterConfigure.CatalogComponentId);

		IReadOnlyList<CatalogExecutionProfileDetail> profiles =
			await _catalog.ListExecutionProfilesByComponentAsync(catalogComponentId, CancellationToken.None);
		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(
			afterConfigure, profiles[0].ProductVersion.Id, profiles[0].ProductVersion.VersionKey, profiles);
		Assert.True(match.IsCompatible, string.Join("; ", match.IncompatibilityReasons));
	}

	/// <summary>
	/// Ambiguity fails closed for the configured-fact path exactly like the discovered
	/// path (<c>DiscoverJobHandlerCatalogLinkageTests.DiscoveredHost_WithAmbiguousCatalogMatch_StaysUnlinked_ReportsAmbiguity</c>)
	/// -- proves the SHARED resolver, not a forked copy with different behavior.
	/// </summary>
	[Fact]
	public async Task ConfiguredExactVersion_AmbiguousAcrossProducts_StaysUnlinked()
	{
		string sharedComponentKey = $"ambiguous-1000-{Guid.NewGuid():N}";
		string sharedVersion = "1.2.3";
		await SeedTopLevelCatalogComponentAsync("vendor-a-1000", $"product-a-{Guid.NewGuid():N}", sharedVersion, sharedComponentKey);
		await SeedTopLevelCatalogComponentAsync("vendor-b-1000", $"product-b-{Guid.NewGuid():N}", sharedVersion, sharedComponentKey);

		Guid siteId = await CreateSiteAsync("1000-ambiguous-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-1000-ambiguous", """{"host":"vcsa-1000-ambiguous.example.internal"}""");

		DiscoveredComponent mapping = new(
			CatalogComponentKey: sharedComponentKey, VendorIdentity: "ambiguous-1000-component", DisplayName: "ambiguous",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: null);
		await _components.UpsertDiscoveredAsync(targetId, [mapping], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpRequestMessage putRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{seeded.Id}", "Admin");
		putRequest.Content = JsonContent.Create(new { exact_version = sharedVersion }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage putResponse = await _client.SendAsync(putRequest);
		Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

		Component afterConfigure = (await _components.GetAsync(seeded.Id, CancellationToken.None))!;
		Assert.Null(afterConfigure.CatalogComponentId);
	}

	/// <summary>
	/// Re-discovery must not clobber a configured-fact-driven link (issue #1000's own
	/// stale-link fix): a vCenter-root component links from PUT, then a later discovery
	/// pass for that SAME target (which never carries a version for the root, by
	/// construction) must not stomp the link back to null.
	/// </summary>
	[Fact]
	public async Task Rediscovery_OfVersionlessComponent_DoesNotClobberConfiguredFactLink()
	{
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(VCenterCandidate(), VCenterPromotionRequest(), CancellationToken.None);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;
		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Guid catalogComponentId = profileDetail!.Component.Id;
		string exactVersionKey = profileDetail.ProductVersion.VersionKey;

		Guid siteId = await CreateSiteAsync("1000-rediscovery-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-1000-redisc", """{"host":"vcsa-1000-redisc.example.internal"}""");

		DiscoveredComponent rootMapping = new(
			CatalogComponentKey: CatalogSelectorKinds.VCenter, VendorIdentity: null, DisplayName: "vCenter Server",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: null);
		await _components.UpsertDiscoveredAsync(targetId, [rootMapping], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpRequestMessage putRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{seeded.Id}", "Admin");
		putRequest.Content = JsonContent.Create(new { exact_version = exactVersionKey }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(putRequest)).StatusCode);

		Component afterConfigure = (await _components.GetAsync(seeded.Id, CancellationToken.None))!;
		Assert.Equal(catalogComponentId, afterConfigure.CatalogComponentId);

		// A second discovery pass for the SAME target reports the same versionless root
		// mapping again -- exactly what a real recurring/manual refresh does every time
		// for this component kind.
		await _components.UpsertDiscoveredAsync(targetId, [rootMapping], CancellationToken.None);

		Component afterRediscovery = (await _components.GetAsync(seeded.Id, CancellationToken.None))!;
		Assert.Equal(catalogComponentId, afterRediscovery.CatalogComponentId); // Must NOT have been clobbered to null.
	}

	/// <summary>
	/// Clearing the configured fact must honestly unlink (issue #1000 AC) when no
	/// discovered fact exists to fall back on.
	/// </summary>
	[Fact]
	public async Task ClearingConfiguredFact_WithNoDiscoveredFact_HonestlyUnlinks()
	{
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(VCenterCandidate(), VCenterPromotionRequest(), CancellationToken.None);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;
		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		string exactVersionKey = profileDetail!.ProductVersion.VersionKey;

		Guid siteId = await CreateSiteAsync("1000-clear-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-1000-clear", """{"host":"vcsa-1000-clear.example.internal"}""");

		DiscoveredComponent rootMapping = new(
			CatalogComponentKey: CatalogSelectorKinds.VCenter, VendorIdentity: null, DisplayName: "vCenter Server",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: null);
		await _components.UpsertDiscoveredAsync(targetId, [rootMapping], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpRequestMessage putRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{seeded.Id}", "Admin");
		putRequest.Content = JsonContent.Create(new { exact_version = exactVersionKey }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(putRequest)).StatusCode);
		Assert.NotNull((await _components.GetAsync(seeded.Id, CancellationToken.None))!.CatalogComponentId);

		HttpRequestMessage clearRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{seeded.Id}", "Admin");
		clearRequest.Content = JsonContent.Create(new { exact_version = (string?)null }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage clearResponse = await _client.SendAsync(clearRequest);
		Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);

		Component afterClear = (await _components.GetAsync(seeded.Id, CancellationToken.None))!;
		Assert.Null(afterClear.ConfiguredFact);
		Assert.Null(afterClear.CatalogComponentId);
	}

	private async Task SeedTopLevelCatalogComponentAsync(string vendor, string productKey, string versionKey, string componentKey)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-1000-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, vendor, productKey, productKey, CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, versionKey, versionKey, CancellationToken.None);
		await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition(componentKey, componentKey, CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null),
			CancellationToken.None);
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
