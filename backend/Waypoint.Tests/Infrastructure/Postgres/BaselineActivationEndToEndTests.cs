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

	// Issue #998's CORRECTED owner decision: the catalog product-version key is the
	// vendor's declared version scope, VERBATIM (the content directory segment itself,
	// e.g. "8.0" for a `vsphere/8.0/...` layout) -- never a patch-level triple. "8.0.3"
	// would now be an unrecognized key form and fail every VersionScopeMatcher lookup
	// closed, so this fixture's path/key both use the realistic minor-scoped directory
	// literal a real vsphere/8.0 tree declares.
	private static SemanticCandidate VCenterCandidate() => new(
		"vsphere/8.0/v2r3-stig/inspec/baseline/vcenter", "vsphere", "8.0", "stig", "vcenter", "v2r3-stig", "vCenter Server",
		"vmware", "vcenter", null,
		IsAggregate: false, Title: "vCenter STIG", ManifestVersion: "2.3.0",
		Inputs: [], Supports: [], Depends: [], ContentDigest: "invented0000000000000000000000000000000000000000000000000000");

	private static CatalogPromotionRequest VCenterPromotionRequest() => new(
		SourceRevisionKey: "compliance-content",
		Vendor: CatalogVendors.VMware,
		ProductDisplayName: "VMware vSphere",
		ProductVersionDisplayName: "vSphere 8.0",
		ContentReleaseDisplayName: "stig 8.0",
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

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<Waypoint.Infrastructure.Discovery.DiscoverJobHandler.CatalogLinkageIssue> issues) =
			await Waypoint.Infrastructure.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [discovered], CancellationToken.None);
		Assert.Empty(issues);
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

	// Issue #998 (rebase reconciliation with #1003): same declared-scope correction as
	// VCenterCandidate above -- "8.0.3" is an unrecognized key form under
	// VersionScopeMatcher and would fail every configured-fact lookup closed, so the
	// candidate declares the verbatim minor-scoped "8.0" a real vsphere/8.0 tree does.
	private static SemanticCandidate VmCandidate() => new(
		"vsphere/8.0/v2r3-stig/inspec/baseline/vm", "vsphere", "8.0", "stig", "vm", "v2r3-stig", "Virtual Machine",
		"vmware", "vm", null,
		IsAggregate: false, Title: "VM STIG", ManifestVersion: "2.3.0",
		Inputs: [], Supports: [], Depends: [], ContentDigest: "invented0000000000000000000000000000000000000000000000000003");

	private static CatalogPromotionRequest VmPromotionRequest() => new(
		SourceRevisionKey: "compliance-content",
		Vendor: CatalogVendors.VMware,
		ProductDisplayName: "VMware vSphere",
		ProductVersionDisplayName: "vSphere 8.0",
		ContentReleaseDisplayName: "stig 8.0",
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

	/// <summary>
	/// Same proof, for a VM component -- issue #1000's other named-impacted component
	/// (#740). Issue #998's rebase reconciliation strengthens this beyond self-equality:
	/// the Admin configures a PATCH-LEVEL version ("8.0.3") while the catalog key is the
	/// declared minor scope ("8.0") -- the shared resolver must link them via
	/// VersionScopeMatcher's scope test, proving the CONFIGURED path scope-matches
	/// exactly like the discovered path, never byte-equality.
	/// </summary>
	[Fact]
	public async Task ConfiguredExactVersion_OnVm_LinksAndMatcherReportsCompatible()
	{
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(VmCandidate(), VmPromotionRequest(), CancellationToken.None);
		Assert.NotNull(promotion.ExecutionProfileId);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;

		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(profileDetail);
		Guid catalogComponentId = profileDetail!.Component.Id;
		Assert.Equal("8.0", profileDetail.ProductVersion.VersionKey); // Declared minor scope, per VmCandidate.
		const string configuredPatchLevelVersion = "8.0.3"; // Invented full observed-style version within that scope.

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
		putRequest.Content = JsonContent.Create(new { exact_version = configuredPatchLevelVersion }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage putResponse = await _client.SendAsync(putRequest);
		Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

		Component afterConfigure = (await _components.GetAsync(beforeConfigure.Id, CancellationToken.None))!;
		Assert.Equal(catalogComponentId, afterConfigure.CatalogComponentId);
		Assert.Equal(configuredPatchLevelVersion, afterConfigure.ConfiguredFact?.ExactVersion); // Stored fact stays the full configured value, never rewritten to the scope key.

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
		// Issue #998 (rebase reconciliation): "1.2.3" would be an unrecognized key form
		// under VersionScopeMatcher and stay unlinked for the WRONG reason (fail-closed
		// key form, zero matches -- not ambiguity). "1.2" is a valid minor-scoped key,
		// so both seeded products genuinely match and this test keeps proving the
		// AMBIGUITY branch of the shared resolver, same as its discovered-path sibling
		// (DiscoverJobHandlerCatalogLinkageTests).
		string sharedVersion = "1.2";
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

	private static SemanticCandidate PhotonCandidate() => new(
		"photon/5.0/v3r3-srg/inspec/baseline", "photon", "5.0", "srg", "photon", "v3r3-srg", "Photon OS",
		CatalogTransports.Ssh, CatalogSelectorKinds.Target, null,
		IsAggregate: false, Title: "Photon OS SRG", ManifestVersion: "3.3.0",
		Inputs: [], Supports: [], Depends: [], ContentDigest: "invented1111111111111111111111111111111111111111111111111111");

	private static CatalogPromotionRequest PhotonPromotionRequest() => new(
		SourceRevisionKey: "compliance-content",
		Vendor: CatalogVendors.VMware,
		ProductDisplayName: "VMware Photon OS",
		ProductVersionDisplayName: "Photon OS 5.0",
		ContentReleaseDisplayName: "srg 5.0",
		ReportGroupKey: "srg",
		ReportGroupDisplayName: "SRG",
		ReportGroupPriority: 6,
		OutputKind: CatalogOutputKinds.Hdf);

	/// <summary>
	/// Issue #743: the whole-appliance SSH product's closing leg, the ssh analogue of
	/// <see cref="ConfiguredExactVersion_OnVCenterRoot_LinksAndPlanPreviewReportsRunnable"/>.
	/// An <c>ssh</c> target has NO discovery operation at all, so before this issue it
	/// had no component rows and therefore no reachable plan item -- an SSH product
	/// could never be planned by component at all. The Admin now DECLARES the product
	/// explicitly (<c>POST /targets/{id}/components</c>, "generic SSH does not guess a
	/// product"), the declared row links through the SAME shared configured-fact/linkage
	/// path every other provenance uses, and plan-preview reports it runnable.
	/// </summary>
	[Fact]
	public async Task DeclaredSshProductRoot_LinksViaConfiguredVersion_AndPlanPreviewReportsRunnable()
	{
		CatalogPromotionOutcome promotion = await _catalog.PromoteCandidateAsync(PhotonCandidate(), PhotonPromotionRequest(), CancellationToken.None);
		Assert.NotNull(promotion.ExecutionProfileId);
		Guid executionProfileId = promotion.ExecutionProfileId!.Value;

		CatalogExecutionProfileDetail? profileDetail = await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(profileDetail);
		Guid catalogComponentId = profileDetail!.Component.Id;
		string exactVersionKey = profileDetail.ProductVersion.VersionKey;

		ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
			"commit-743-photon", "digest-743-photon", "revisions/743-photon", CancellationToken.None);
		await ActivateBaselineAsync(revision.Id, executionProfileId);

		Guid siteId = await CreateSiteAsync("743-ssh-site");
		Guid targetId = await CreateTargetAsync(siteId, "ssh", "appliance-743", """{"host":"appliance-743.example.internal"}""");

		// No discovery pass exists for an ssh target -- the component list starts EMPTY.
		Assert.Empty(await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None));

		HttpRequestMessage declareRequest = WithRole(HttpMethod.Post, $"/api/v1/targets/{targetId}/components", "Admin");
		declareRequest.Content = JsonContent.Create(
			new { catalog_component_key = "photon", exact_version = exactVersionKey },
			options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage declareResponse = await _client.SendAsync(declareRequest);
		Assert.Equal(HttpStatusCode.Created, declareResponse.StatusCode);

		Component declared = Assert.Single(await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None));
		Assert.Equal(catalogComponentId, declared.CatalogComponentId);
		Assert.Equal(exactVersionKey, declared.ConfiguredFact!.ExactVersion);
		Assert.False(declared.FactConflict);

		object scopeBody = new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { declared.Id } },
		};
		HttpRequestMessage previewRequest = WithRole(HttpMethod.Post, "/api/v1/runs/plan-preview", "Cyber");
		previewRequest.Content = new StringContent(
			JsonSerializer.Serialize(new { scope = JsonSerializer.Serialize(scopeBody) }), Encoding.UTF8, "application/json");

		HttpResponseMessage previewResponse = await _client.SendAsync(previewRequest);
		Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
		using JsonDocument previewBody = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
		Assert.Empty(previewBody.RootElement.GetProperty("scope_omissions").EnumerateArray());
		JsonElement item = Assert.Single(previewBody.RootElement.GetProperty("items").EnumerateArray());
		Assert.True(previewBody.RootElement.GetProperty("is_runnable").GetBoolean());

		// Output routing comes from the CATALOG kind (SRG -> hdf), and the required
		// credential purpose is the whole-appliance ssh one -- neither inferred from the
		// target's connection kind.
		Assert.Equal(CatalogOutputKinds.Hdf, item.GetProperty("output_kind").GetString());
		Assert.Contains("srg-ssh", item.GetProperty("required_purposes").EnumerateArray().Select(p => p.GetString()));
	}

	/// <summary>
	/// Issue #743 AC "product/version selection is explicit and validated; generic SSH
	/// does not guess a product": an unknown/unsupported product key is rejected
	/// fail-closed (never created unlinked and never guessed), a discoverable target
	/// kind is rejected (discovery owns those roots), and a second declaration of the
	/// same key is a 409 rather than a duplicate or a silent mutation.
	/// </summary>
	[Fact]
	public async Task DeclareSshProductRoot_ValidatesFailClosed()
	{
		await _catalog.PromoteCandidateAsync(PhotonCandidate(), PhotonPromotionRequest(), CancellationToken.None);

		Guid siteId = await CreateSiteAsync("743-ssh-validation-site");
		Guid sshTargetId = await CreateTargetAsync(siteId, "ssh", "appliance-743-validate", """{"host":"appliance-743-validate.example.internal"}""");
		Guid vsphereTargetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-743-validate", """{"host":"vcsa-743-validate.example.internal"}""");

		HttpResponseMessage unknown = await DeclareRootAsync(sshTargetId, "not-a-catalog-product");
		Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
		Assert.Contains("unknown_catalog_component_key", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

		HttpResponseMessage discoverable = await DeclareRootAsync(vsphereTargetId, "photon");
		Assert.Equal(HttpStatusCode.BadRequest, discoverable.StatusCode);
		Assert.Contains("declared_component_unsupported_target_kind", await discoverable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

		Assert.Equal(HttpStatusCode.Created, (await DeclareRootAsync(sshTargetId, "photon")).StatusCode);
		HttpResponseMessage duplicate = await DeclareRootAsync(sshTargetId, "photon");
		Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
		Assert.Contains("component_exists", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1202: the catalog holds one top-level `photon` component PER product
	/// version (invented 3.0/4.0/5.0 fixtures here). Declaring with `exact_version:
	/// "5.0"` must return the 5.0 catalog component's own display name and capability
	/// -- never an arbitrary sibling version's name (the original bug: whichever
	/// candidate `ListTopLevelComponentsByKeyAsync` happened to return first, before
	/// linkage ran). Declaring with no `exact_version` must produce a version-neutral
	/// name (the unlinked row cannot honestly claim any one version's identity), and a
	/// later re-link to a DIFFERENT version must re-derive the name rather than keep
	/// whatever was stored at declaration or first-link time.
	/// </summary>
	[Fact]
	public async Task DeclaredSshProductRoot_MultipleCatalogVersions_DisplayNameMatchesActualLinkedVersion()
	{
		await PromotePhotonVersionAsync("3.0", "InSpec Profile VMware Photon OS 3.0 Appliance based deployments");
		await PromotePhotonVersionAsync("4.0", "InSpec Profile VMware Photon OS 4.0 Appliance based deployments");
		CatalogPromotionOutcome fiveDotZero = await PromotePhotonVersionAsync(
			"5.0", "InSpec Profile VMware Photon OS 5.0 Appliance based deployments");
		CatalogExecutionProfileDetail? fiveDotZeroProfile = await _catalog.GetExecutionProfileAsync(fiveDotZero.ExecutionProfileId!.Value, CancellationToken.None);
		Guid fiveDotZeroComponentId = fiveDotZeroProfile!.Component.Id;

		Guid siteId = await CreateSiteAsync("1202-multi-version-site");
		Guid targetId = await CreateTargetAsync(siteId, "ssh", "appliance-1202", """{"host":"appliance-1202.example.internal"}""");

		// Declaring the exact 5.0 version must return (and, on re-read, keep returning)
		// the 5.0 catalog component's own display name -- not the 3.0 or 4.0 sibling's.
		HttpRequestMessage declareRequest = WithRole(HttpMethod.Post, $"/api/v1/targets/{targetId}/components", "Admin");
		declareRequest.Content = JsonContent.Create(
			new { catalog_component_key = "photon", exact_version = "5.0" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage declareResponse = await _client.SendAsync(declareRequest);
		Assert.Equal(HttpStatusCode.Created, declareResponse.StatusCode);
		using JsonDocument declareBody = JsonDocument.Parse(await declareResponse.Content.ReadAsStringAsync());
		Assert.Equal("InSpec Profile VMware Photon OS 5.0 Appliance based deployments", declareBody.RootElement.GetProperty("display_name").GetString());
		Guid componentId = declareBody.RootElement.GetProperty("id").GetGuid();
		Assert.Equal(fiveDotZeroComponentId.ToString(), declareBody.RootElement.GetProperty("catalog_component_id").GetString());

		HttpResponseMessage getResponse = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/components/{componentId}", "Viewer"));
		using JsonDocument getBody = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal("InSpec Profile VMware Photon OS 5.0 Appliance based deployments", getBody.RootElement.GetProperty("display_name").GetString());

		// Issue #1202's Impact names the LIST surface ("an operator listing components on
		// that target ... sees a 4.0 product label"), so prove it on a list that also
		// holds a vendor-discovered sibling: the declared root must render its linked
		// catalog version's name, while the discovered vCenter keeps the vendor-observed
		// hostname discovery reported. Seeded through the repository (not an HTTP route)
		// because no discovery pass runs in this test; the linkage itself still goes
		// through the real resolver, exactly as the round-6 rehearsal above does.
		CatalogPromotionOutcome vcenterPromotion = await _catalog.PromoteCandidateAsync(
			VCenterCandidate(), VCenterPromotionRequest(), CancellationToken.None);
		CatalogExecutionProfileDetail? vcenterProfile =
			await _catalog.GetExecutionProfileAsync(vcenterPromotion.ExecutionProfileId!.Value, CancellationToken.None);
		DiscoveredComponent discoveredSibling = new(
			CatalogComponentKey: "vcenter", VendorIdentity: "vcenter-1202", DisplayName: "vcsa-1202.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: vcenterProfile!.ProductVersion.VersionKey);
		(IReadOnlyList<DiscoveredComponent> linkedSibling, IReadOnlyList<string> siblingAmbiguities) =
			await Waypoint.Infrastructure.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync(
				_catalog, [discoveredSibling], CancellationToken.None);
		Assert.Empty(siblingAmbiguities);
		Assert.NotNull(linkedSibling.Single().CatalogComponentId);
		await _components.UpsertDiscoveredAsync(targetId, linkedSibling, CancellationToken.None);

		HttpResponseMessage listResponse = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/targets/{targetId}/components", "Viewer"));
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
		using JsonDocument listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Dictionary<string, string> listedNamesByKey = listBody.RootElement.EnumerateArray().ToDictionary(
			element => element.GetProperty("catalog_component_key").GetString()!,
			element => element.GetProperty("display_name").GetString()!,
			StringComparer.Ordinal);
		Assert.Equal(
			"InSpec Profile VMware Photon OS 5.0 Appliance based deployments",
			listedNamesByKey["photon"]);
		Assert.Equal("vcsa-1202.example.internal", listedNamesByKey["vcenter"]);

		HttpResponseMessage capabilityResponse = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/components/{componentId}/capability", "Viewer"));
		Assert.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);
		using JsonDocument capabilityBody = JsonDocument.Parse(await capabilityResponse.Content.ReadAsStringAsync());
		Assert.True(capabilityBody.RootElement.GetProperty("is_compatible").GetBoolean());
		// `!`: a JSON array element the API contract declares as a string id -- GetString()
		// is typed `string?` for the null-literal case that cannot occur here, and the
		// spread into `string[]` is an error (CS8601) without the assertion.
		string[] compatibleProfileIds = [.. capabilityBody.RootElement.GetProperty("compatible_execution_profile_ids").EnumerateArray().Select(p => p.GetString()!)];
		Assert.Equal([fiveDotZero.ExecutionProfileId!.Value.ToString()], compatibleProfileIds);

		// Re-link to 4.0 -- the same PUT /components/{id} path a later Admin correction
		// uses -- must re-derive the name rather than keep the 5.0 name from the first
		// link.
		HttpRequestMessage relinkRequest = WithRole(HttpMethod.Put, $"/api/v1/components/{componentId}", "Admin");
		relinkRequest.Content = JsonContent.Create(
			new { exact_version = "4.0" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage relinkResponse = await _client.SendAsync(relinkRequest);
		Assert.Equal(HttpStatusCode.OK, relinkResponse.StatusCode);
		using JsonDocument relinkBody = JsonDocument.Parse(await relinkResponse.Content.ReadAsStringAsync());
		Assert.Equal("InSpec Profile VMware Photon OS 4.0 Appliance based deployments", relinkBody.RootElement.GetProperty("display_name").GetString());

		// A fresh declaration with NO exact_version starts unlinked -- its name must be
		// version-neutral, never a stale/arbitrary sibling version's descriptive name.
		Guid unversionedTargetId = await CreateTargetAsync(siteId, "ssh", "appliance-1202-unlinked", """{"host":"appliance-1202-unlinked.example.internal"}""");
		HttpResponseMessage unlinkedDeclareResponse = await DeclareRootAsync(unversionedTargetId, "photon");
		Assert.Equal(HttpStatusCode.Created, unlinkedDeclareResponse.StatusCode);
		using JsonDocument unlinkedBody = JsonDocument.Parse(await unlinkedDeclareResponse.Content.ReadAsStringAsync());
		string unlinkedDisplayName = unlinkedBody.RootElement.GetProperty("display_name").GetString()!;
		Assert.DoesNotContain("3.0", unlinkedDisplayName, StringComparison.Ordinal);
		Assert.DoesNotContain("4.0", unlinkedDisplayName, StringComparison.Ordinal);
		Assert.DoesNotContain("5.0", unlinkedDisplayName, StringComparison.Ordinal);
		Assert.Equal("photon", unlinkedDisplayName);
	}

	private async Task<CatalogPromotionOutcome> PromotePhotonVersionAsync(string version, string componentDisplayName)
	{
		SemanticCandidate candidate = new(
			$"photon/{version}/v3r3-srg/inspec/baseline", "photon", version, "srg", "photon", "v3r3-srg", componentDisplayName,
			CatalogTransports.Ssh, CatalogSelectorKinds.Target, null,
			IsAggregate: false, Title: "Photon OS SRG", ManifestVersion: "3.3.0",
			Inputs: [], Supports: [], Depends: [], ContentDigest: $"invented1202{version.Replace(".", "", StringComparison.Ordinal)}".PadRight(64, '0'));
		CatalogPromotionRequest request = new(
			SourceRevisionKey: "compliance-content",
			Vendor: CatalogVendors.VMware,
			ProductDisplayName: "VMware Photon OS",
			ProductVersionDisplayName: $"Photon OS {version}",
			ContentReleaseDisplayName: $"srg {version}",
			ReportGroupKey: "srg",
			ReportGroupDisplayName: "SRG",
			ReportGroupPriority: 6,
			OutputKind: CatalogOutputKinds.Hdf);
		CatalogPromotionOutcome outcome = await _catalog.PromoteCandidateAsync(candidate, request, CancellationToken.None);
		Assert.NotNull(outcome.ExecutionProfileId);
		return outcome;
	}

	private async Task<HttpResponseMessage> DeclareRootAsync(Guid targetId, string catalogComponentKey)
	{
		HttpRequestMessage request = WithRole(HttpMethod.Post, $"/api/v1/targets/{targetId}/components", "Admin");
		request.Content = JsonContent.Create(
			new { catalog_component_key = catalogComponentKey }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		return await _client.SendAsync(request);
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
