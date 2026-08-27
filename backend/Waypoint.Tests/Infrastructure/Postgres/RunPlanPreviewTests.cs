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
/// Issues #733/#734 remainder ("`/runs/plan-preview`'s mandatory discovery refresh" per
/// docs/api-contract.md, planned by PR #819): end to end against real Postgres and the
/// real API, <c>POST /api/v1/runs/plan-preview</c> runs the identical resolve→compile
/// pipeline <c>POST /runs</c> uses (see <see cref="ScanRunTargetScopeTests"/> for that
/// path's own coverage) but persists NOTHING -- this suite's own required proof is the
/// zero-row guarantee (AC-2) and digest parity against a subsequent create (AC-4).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class RunPlanPreviewTests : IAsyncLifetime
{
	private sealed class PlanPreviewApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public PlanPreviewApiFactory(string connectionString)
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

				services.AddSingleton<Waypoint.Core.ComplianceContent.IBaselineRepository>(new BaselineRepository(_connectionString));
				// Issue #735: ScanPlannerService now folds a config-doc snapshot, so the
				// config resolver + its repository must point at the SAME fixture database as
				// every other repo above (otherwise resolution hits the default host DB and
				// the compile throws -> 500). Register both against the fixture connection.
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
	private PlanPreviewApiFactory _factory = null!;
	private HttpClient _client = null!;
	private ProfileRepository _profiles = null!;
	private ComponentRepository _components = null!;
	private Guid _profileId;

	public RunPlanPreviewTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new PlanPreviewApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
		_components = new ComponentRepository(_fixture.ConnectionString);

		_profiles = new ProfileRepository(_fixture.ConnectionString);
		await _profiles.ReplaceAllAsync(
			[new ProfileUpsert("vsphere-preview-profile", "vSphere Plan-Preview Test Profile", "1.0.0", "invented-commit-preview", ProfileStates.Current)],
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
	public async Task Preview_WithResolvableComponent_Returns200_AndPersistsZeroRows()
	{
		// Issue #734 AC-2 (this issue's own required reading): preview must leave zero
		// run/snapshot/plan/binding rows -- verified directly against the tables, not
		// just "the response looked right."
		Guid siteId = await CreateSiteAsync("preview-zero-persist-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-9001", "esxi-preview-01.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpResponseMessage response = await PreviewAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("explicit", body.RootElement.GetProperty("requested_mode").GetString());
		Assert.Equal([seeded.Id], body.RootElement.GetProperty("resolved_component_ids").EnumerateArray().Select(e => e.GetGuid()));
		Assert.Empty(body.RootElement.GetProperty("scope_omissions").EnumerateArray());
		Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
		Assert.True(body.RootElement.GetProperty("is_runnable").GetBoolean());
		Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("plan_digest").GetString()));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Assert.Equal(0L, await CountAsync(connection, "runs"));
		Assert.Equal(0L, await CountAsync(connection, "jobs"));
		Assert.Equal(0L, await CountAsync(connection, "run_scope_snapshots"));
		Assert.Equal(0L, await CountAsync(connection, "scan_plans"));
		Assert.Equal(0L, await CountAsync(connection, "scan_plan_items"));
		Assert.Equal(0L, await CountAsync(connection, "job_credential_bindings"));
	}

	/// <summary>
	/// Issue #985's plan-preview proof: unlike every other test in this file (which
	/// seeds <c>catalog_component_id</c> directly via <see cref="SeedCompatibleCatalogComponentAsync"/>'s
	/// return value passed straight into <see cref="DiscoveredComponent"/>), this test
	/// resolves the link through the REAL discovery-time linkage mechanism
	/// (<see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync"/>)
	/// from nothing but a catalog-key + exact-version fact, exactly as a live discovery
	/// pass would -- documenting the full chain issue #985's own evidence named as
	/// broken: discovered fact -&gt; resolved linkage -&gt; persisted catalog_component_id
	/// -&gt; is_runnable=true through the unmodified capability matcher.
	/// </summary>
	[Fact]
	public async Task Preview_WithComponentLinkedThroughRealDiscoveryLinkage_ReportsRunnable()
	{
		Guid siteId = await CreateSiteAsync("preview-real-linkage-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		Waypoint.Core.ComplianceContent.CatalogExecutionProfileDetail profileDetail =
			(await new CatalogRepository(_fixture.ConnectionString).ListExecutionProfilesByComponentAsync(catalogComponentId, CancellationToken.None)).Single();
		string seededVersionKey = profileDetail.ProductVersion.VersionKey;

		// No CatalogComponentId supplied here -- only the fact a real discovery pass
		// would report (catalog key + exact version). ResolveCatalogLinkageAsync must
		// find the row SeedCompatibleCatalogComponentAsync just seeded on its own.
		DiscoveredComponent discovered = new(
			CatalogComponentKey: "esxi", VendorIdentity: "host-985-preview", DisplayName: "esxi-985-preview.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: seededVersionKey);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> ambiguities) =
			await Waypoint.Infrastructure.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync(
				new CatalogRepository(_fixture.ConnectionString), [discovered], CancellationToken.None);
		Assert.Empty(ambiguities);
		Assert.Equal(catalogComponentId, linked.Single().CatalogComponentId);

		await _components.UpsertDiscoveredAsync(targetId, linked, CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();
		Assert.Equal(catalogComponentId, seeded.CatalogComponentId);

		HttpResponseMessage response = await PreviewAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal([seeded.Id], body.RootElement.GetProperty("resolved_component_ids").EnumerateArray().Select(e => e.GetGuid()));
		Assert.Empty(body.RootElement.GetProperty("scope_omissions").EnumerateArray());
		Assert.True(body.RootElement.GetProperty("is_runnable").GetBoolean());
	}

	[Fact]
	public async Task Preview_ThenCreate_WithIdenticalInputs_ProducesIdenticalDigest()
	{
		// Issue #734 AC-4: preview and create use the same planner and produce the same
		// plan digest for identical inputs.
		Guid siteId = await CreateSiteAsync("preview-digest-parity-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-9002", "esxi-preview-02.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		object scopeBody = new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		};

		HttpResponseMessage previewResponse = await PreviewAsync(scopeBody);
		Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
		using JsonDocument previewBody = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
		string previewDigest = previewBody.RootElement.GetProperty("plan_digest").GetString()!;

		// Issue #895: create takes the EXACT SAME scopeBody preview was just called
		// with -- no profile_id added -- proving the wizard's one-payload preview→create
		// handoff this issue fixes, not merely that the two ENDPOINTS can each be made
		// to produce the same digest given two different, hand-tailored payloads.
		HttpResponseMessage createResponse = await PostRunAsync(scopeBody);
		Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		ScanPlanRepository plans = new(_fixture.ConnectionString);
		Waypoint.Core.Scans.ScanPlan? persistedPlan = await plans.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(persistedPlan);
		Assert.Equal(previewDigest, persistedPlan!.PlanDigest);
	}

	[Fact]
	public async Task Preview_WithDifferentResolvedScope_ProducesDifferentDigest()
	{
		// Sensitivity counterpart of the parity test above: a changed input (a second,
		// distinct resolvable component added to the request) must change the digest --
		// proving the digest is not a constant/degenerate value.
		Guid siteId = await CreateSiteAsync("preview-digest-sensitivity-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetId,
			[
				new DiscoveredComponent("esxi", "host-9003", "esxi-preview-03.example.internal", null, catalogComponentId, "8.0.3"),
				new DiscoveredComponent("esxi", "host-9004", "esxi-preview-04.example.internal", null, catalogComponentId, "8.0.3"),
			],
			CancellationToken.None);
		Component[] seeded = [.. (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))];
		Assert.Equal(2, seeded.Length);

		HttpResponseMessage firstResponse = await PreviewAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded[0].Id } },
		});
		using JsonDocument firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
		string firstDigest = firstBody.RootElement.GetProperty("plan_digest").GetString()!;

		HttpResponseMessage secondResponse = await PreviewAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded[0].Id, seeded[1].Id } },
		});
		using JsonDocument secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
		string secondDigest = secondBody.RootElement.GetProperty("plan_digest").GetString()!;

		Assert.NotEqual(firstDigest, secondDigest);
	}

	[Fact]
	public async Task Preview_ThenCreate_WithResolvedConfigDoc_FoldsIdenticalConfigSnapshotIntoBothDigests()
	{
		// Issue #735 preview-parity: the config-resolution fold (Input/Attestation snapshot)
		// must go through the SAME ScanPlannerService chokepoint for preview and create, so
		// identical config docs yield identical digests. The base parity test seeds no
		// declared inputs, so its digest never exercises the config fold; this one does --
		// a declared (optional) input plus a resolvable Global Input doc, asserting both the
		// digest parity AND that the persisted item actually carries the resolved snapshot.
		Guid siteId = await CreateSiteAsync("preview-config-parity-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		(Guid catalogComponentId, Guid executionProfileId) = await SeedCompatibleCatalogComponentWithDeclaredInputAsync();
		await SaveInputConfigDocAsync(executionProfileId, "target_ip: 192.0.2.77\n");

		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-9100", "esxi-preview-cfg.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		object scopeBody = new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		};

		HttpResponseMessage previewResponse = await PreviewAsync(scopeBody);
		Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
		using JsonDocument previewBody = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
		string previewDigest = previewBody.RootElement.GetProperty("plan_digest").GetString()!;

		// Issue #895: same scopeBody preview used, no profile_id added -- see the base
		// parity test's comment.
		HttpResponseMessage createResponse = await PostRunAsync(scopeBody);
		Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		ScanPlanRepository plans = new(_fixture.ConnectionString);
		Waypoint.Core.Scans.ScanPlan? persistedPlan = await plans.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(persistedPlan);
		Assert.Equal(previewDigest, persistedPlan!.PlanDigest);

		// The digest parity is only meaningful if a config snapshot was actually folded:
		// the persisted item must carry the resolved Input.
		Waypoint.Core.Scans.ScanPlanItem item = Assert.Single(persistedPlan.Items);
		Waypoint.Core.ConfigDocs.PlanInputResolution input = Assert.Single(item.InputResolutionsOrEmpty);
		Assert.Equal("target_ip", input.InputName);
		Assert.Equal(Waypoint.Core.ConfigDocs.ConfigResolutionStates.Resolved, input.State);
	}

	[Fact]
	public async Task Preview_WithOnlyUnresolvableComponents_Returns200_AsHonestEmptyPlan()
	{
		// Docs/api-contract.md: "Zero-runnable-component previews are still 200 (an
		// honest empty plan), not an error; the caller decides whether to proceed" --
		// the opposite of create's no_runnable_component 400 for the identical scope.
		Guid siteId = await CreateSiteAsync("preview-empty-plan-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PreviewAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { Guid.NewGuid() } },
		});

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Empty(body.RootElement.GetProperty("resolved_component_ids").EnumerateArray());
		Assert.NotEmpty(body.RootElement.GetProperty("scope_omissions").EnumerateArray());
		Assert.False(body.RootElement.GetProperty("is_runnable").GetBoolean());
	}

	[Fact]
	public async Task Preview_SurfacesMissingCredentialBindingAsGap_AndDemotesTheItemToASkip()
	{
		// Issue #736's per-component credential coverage, surfaced read-only by preview
		// exactly as create's post-resolution demotion would (this issue's "conflict
		// surfacing" AC): a component whose plan item requires a purpose with no target
		// binding shows up as both a credential_gaps entry and a plan skip, not a silent
		// accepted item.
		Guid siteId = await CreateSiteAsync("preview-credential-gap-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentWithRequiredPurposeAsync();
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-9005", "esxi-preview-05.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		// Deliberately never bind the vcsa-ssh purpose this profile requires.
		HttpResponseMessage response = await PreviewAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		JsonElement[] gaps = [.. body.RootElement.GetProperty("credential_gaps").EnumerateArray()];
		Assert.Single(gaps);
		Assert.Equal("vcsa-ssh", gaps[0].GetProperty("purpose").GetString());
		Assert.Equal("missing_binding", gaps[0].GetProperty("reason").GetString());

		Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
		JsonElement[] skips = [.. body.RootElement.GetProperty("skips").EnumerateArray()];
		Assert.Single(skips);
		Assert.Equal(seeded.Id, skips[0].GetProperty("component_id").GetGuid());
	}

	[Fact]
	public async Task Preview_RequiresCyberRole()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs/plan-preview", "Viewer",
			new { scope = JsonSerializer.Serialize(new { site_id = Guid.NewGuid(), target_scope = new { mode = "all" } }) });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Preview_WithProfileId_Returns400ValidationError()
	{
		// Preview never selects a profile (ADR-0022 section 7) -- and, as of issue
		// #895, create applies the identical rule for a target_scope request (see
		// CreateScanRun_TargetScopeWithProfileId_Returns400ValidationError below), so
		// one scope payload now serves both endpoints.
		Guid siteId = await CreateSiteAsync("preview-rejects-profile-site");

		HttpResponseMessage response = await PreviewAsync(new
		{
			site_id = siteId,
			profile_id = _profileId,
			target_scope = new { mode = "all" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_TargetScopeWithProfileId_Returns400ValidationError()
	{
		// Issue #895's repro, pinned as a regression test directly on create (not just
		// preview): a target_scope run must reject scope.profile_id with the same
		// actionable shape preview already uses -- the wizard's preview→create handoff
		// depends on both endpoints applying the identical rule to the identical scope.
		Guid siteId = await CreateSiteAsync("create-rejects-profile-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			profile_id = _profileId,
			target_scope = new { mode = "all" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_TargetScopeWithoutProfileId_Succeeds()
	{
		// Issue #895: a target_scope run resolves its execution content from each
		// accepted plan item's own catalog execution profile (the active baseline),
		// never a run-level profile_id -- so omitting profile_id entirely must succeed
		// end to end through fan-out, exactly like preview already tolerates it.
		Guid siteId = await CreateSiteAsync("create-succeeds-without-profile-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-9200", "esxi-no-profile.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		ScanPlanRepository plans = new(_fixture.ConnectionString);
		Waypoint.Core.Scans.ScanPlan? persistedPlan = await plans.GetForRunAsync(runId, CancellationToken.None);
		Assert.NotNull(persistedPlan);
		Assert.Single(persistedPlan!.Items);
	}

	[Fact]
	public async Task Preview_WithoutTargetScope_Returns400ValidationError()
	{
		Guid siteId = await CreateSiteAsync("preview-requires-target-scope-site");

		HttpResponseMessage response = await PreviewAsync(new { site_id = siteId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	/// <summary>Mirrors <see cref="ScanRunTargetScopeTests.SeedCompatibleCatalogComponentAsync"/> -- SRG chain, no credential requirement.</summary>
	private async Task<Guid> SeedCompatibleCatalogComponentAsync()
	{
		CatalogRepository catalog = new(_fixture.ConnectionString);
		BaselineRepository baselines = new(_fixture.ConnectionString);
		CatalogSourceRevision source = await catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		ContentRevision revision = await baselines.RecordStagedRevisionAsync(
			$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);
		Baseline staged = await baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		await baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

		return catalogComponent.Id;
	}

	/// <summary>
	/// Same SRG chain as <see cref="SeedCompatibleCatalogComponentAsync"/> but with the
	/// execution profile's component declaring a <c>vcsa-ssh</c> requirement, via a real
	/// <c>catalog_credential_requirements</c> row -- so the compiled plan item's
	/// <c>RequiredPurposes</c> is non-empty and preview has something to evaluate
	/// coverage for.
	/// </summary>
	private async Task<Guid> SeedCompatibleCatalogComponentWithRequiredPurposeAsync()
	{
		CatalogRepository catalog = new(_fixture.ConnectionString);
		BaselineRepository baselines = new(_fixture.ConnectionString);
		CatalogSourceRevision source = await catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("vcsa", "VCSA", CatalogTransports.Ssh, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand insert = new(
				"INSERT INTO catalog_credential_requirements (execution_profile_id, purpose, is_required) VALUES ($1, $2, true)", connection);
			insert.Parameters.AddWithValue(executionProfile.Id);
			insert.Parameters.AddWithValue("vcsa-ssh");
			await insert.ExecuteNonQueryAsync();
		}

		ContentRevision revision = await baselines.RecordStagedRevisionAsync(
			$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);
		Baseline staged = await baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		await baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

		return catalogComponent.Id;
	}

	/// <summary>
	/// Same SRG chain as <see cref="SeedCompatibleCatalogComponentAsync"/> but the profile
	/// declares one optional Input (<c>target_ip</c>), so a resolvable config doc produces a
	/// non-empty config snapshot the digest folds -- and, being optional, it never trips the
	/// missing-required-input skip when the doc is present anyway. Returns both the catalog
	/// component id and the execution profile id the config doc keys against (issue #735).
	/// </summary>
	private async Task<(Guid CatalogComponentId, Guid ExecutionProfileId)> SeedCompatibleCatalogComponentWithDeclaredInputAsync()
	{
		CatalogRepository catalog = new(_fixture.ConnectionString);
		BaselineRepository baselines = new(_fixture.ConnectionString);
		CatalogSourceRevision source = await catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await catalog.UpsertDeclaredInputAsync(executionProfile.Id, "target_ip", "string", isRequired: false, CancellationToken.None);

		ContentRevision revision = await baselines.RecordStagedRevisionAsync(
			$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);
		Baseline staged = await baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		await baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

		return (catalogComponent.Id, executionProfile.Id);
	}

	private async Task SaveInputConfigDocAsync(Guid executionProfileId, string bodyYaml)
	{
		Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository configDocs = new(_fixture.ConnectionString);
		(Waypoint.Core.ConfigDocs.ConfigDocSaveOutcome outcome, _, _) = await configDocs.SaveAsync(
			Guid.NewGuid(), Waypoint.Core.ConfigDocs.ConfigDocKinds.Input, $"unused-profile-name-{Guid.NewGuid():N}",
			Waypoint.Core.ConfigDocs.ConfigDocLayers.Global, null, "admin", bodyYaml, CancellationToken.None, executionProfileId);
		Assert.Equal(Waypoint.Core.ConfigDocs.ConfigDocSaveOutcome.Ok, outcome);
	}

	private static async Task<long> CountAsync(NpgsqlConnection connection, string table)
	{
		await using NpgsqlCommand command = new($"SELECT COUNT(*) FROM {table}", connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private async Task<HttpResponseMessage> PreviewAsync(object scopeBody)
	{
		return await SendAsync(HttpMethod.Post, "/api/v1/runs/plan-preview", "Cyber",
			new { scope = JsonSerializer.Serialize(scopeBody) });
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
		command.Parameters.AddWithValue($"preview-cred-{Guid.NewGuid():N}");
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
			"TRUNCATE TABLE scan_plan_items, scan_plans, run_scope_snapshots, component_observations, components, jobs, runs, targets, sites, " +
			"baselines, content_revisions, benchmark_component_mappings, benchmark_rules, benchmark_revisions, " +
			"catalog_credential_requirements, catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components, " +
			"catalog_product_versions, catalog_products, catalog_source_revisions RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
