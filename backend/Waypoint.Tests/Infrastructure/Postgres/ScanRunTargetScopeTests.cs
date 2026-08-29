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
				services.AddSingleton<IComponentRepository>(new ComponentRepository(_connectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_connectionString)));
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
				services.AddSingleton<ScopeResolutionService>();
				services.AddSingleton<IRunScopeSnapshotRepository>(new RunScopeSnapshotRepository(_connectionString));

				// Issue #734: RunCreationService now also depends on the plan
				// compiler/repository -- registered here for the same reason every
				// other RunCreationService dependency is re-registered against this
				// factory's own connection string rather than inheriting Program.cs's
				// composition.
				services.AddSingleton<Waypoint.Core.ComplianceContent.IBaselineRepository>(new BaselineRepository(_connectionString));

				// Issue #735: ScanPlannerService now also depends on PlanConfigResolutionService
				// (config-doc resolution), which in turn depends on ConfigDocRepository -- both
				// re-registered against this factory's own connection string for the same
				// reason as every other RunCreationService dependency above.
				services.AddSingleton(new Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository(_connectionString));
				services.AddSingleton<Waypoint.Infrastructure.ConfigDocs.PlanConfigResolutionService>();
				services.AddSingleton<ScanPlannerService>();
				services.AddSingleton<Waypoint.Core.Scans.IScanPlanRepository>(new ScanPlanRepository(_connectionString));

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
		_components = new ComponentRepository(_fixture.ConnectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString));

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

	/// <summary>
	/// Seeds one complete compatible catalog chain WITH an active SRG baseline --
	/// mirrors <see cref="ScopeResolutionServiceTests.SeedCompatibleCatalogComponentAsync"/>
	/// for this HTTP-level suite, plus the issue #734 planner's own requirement (a
	/// scope-resolved-runnable component must also have an active baseline to become
	/// an accepted plan item, or run creation now rejects it with
	/// <c>no_plannable_component</c>). SRG (no benchmark reference) keeps this fixture
	/// minimal; the STIG/benchmark-mapping paths are covered by
	/// <c>ScanPlannerServiceTests</c>. Issue #1012: carries the real <c>vsphere-api</c>
	/// requirement a vmware-transport component always has per docs/compliance-parity.md
	/// (see <c>RunPlanPreviewTests.SeedCompatibleCatalogComponentAsync</c>'s identical
	/// remark) -- without it, ScanPlannerService's own defense-in-depth would skip this
	/// fixture's plan item instead of exercising this file's actual target-scope-shape
	/// assertions.
	/// </summary>
	private async Task<Guid> SeedCompatibleCatalogComponentAsync()
	{
		CatalogRepository catalog = new(_fixture.ConnectionString);
		BaselineRepository baselines = new(_fixture.ConnectionString);
		CatalogSourceRevision source = await catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0", "8.0", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await catalog.AddCredentialRequirementAsync(executionProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);

		ContentRevision revision = await baselines.RecordStagedRevisionAsync(
			$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);
		Baseline staged = await baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		await baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

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
			target_scope = new { mode = "explicit", component_ids = new[] { Guid.NewGuid() } },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "no_runnable_component");

		HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, "/api/v1/runs", "Viewer", body: null);
		using JsonDocument list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.Empty(list.RootElement.EnumerateArray());
	}

	[Fact]
	public async Task CreateScanRun_WhenPlanCompileHitsAnIntegrityViolation_FailsClosed_NoRunOrJobRowsPersisted()
	{
		// Round-2 review of PR #857 (finding 2), the taxonomy split's negative proof at
		// the HTTP boundary: a resolved-runnable component whose active baseline is
		// internally inconsistent (an SRG execution profile whose active baseline carries
		// a benchmark revision -- corrupt catalog state) must fail the WHOLE run creation
		// closed with a distinct plan_integrity_failure diagnostic, NOT silently drop the
		// component as a skip. Because RunCreationService compiles the plan BEFORE
		// creating the run row, zero run/job rows are persisted.
		Guid siteId = await CreateSiteAsync("integrity-fail-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		Guid catalogComponentId = await SeedCorruptSrgCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-integrity-9001", "esxi-integrity.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None)).Single();

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "plan_integrity_failure");

		// Fail-closed: no run row (and therefore no job/plan/snapshot rows) was created.
		HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, "/api/v1/runs", "Viewer", body: null);
		using JsonDocument list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.Empty(list.RootElement.EnumerateArray());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobCount = new("SELECT COUNT(*) FROM jobs", connection);
		Assert.Equal(0L, (long)(await jobCount.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Seeds one complete SRG catalog chain whose active baseline is deliberately corrupt:
	/// it carries a benchmark revision an SRG profile can never legitimately have (ADR-0022,
	/// no XCCDF benchmark concept for SRG). Restores nothing -- the corruption is the point
	/// -- so the planner's integrity guard fires. Mirror of
	/// <see cref="SeedCompatibleCatalogComponentAsync"/> with the one poisoned field.
	/// </summary>
	private async Task<Guid> SeedCorruptSrgCatalogComponentAsync()
	{
		CatalogRepository catalog = new(_fixture.ConnectionString);
		BaselineRepository baselines = new(_fixture.ConnectionString);
		BenchmarkRepository benchmarks = new(_fixture.ConnectionString);
		CatalogSourceRevision source = await catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0", "8.0", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		// SRG content release => execution profile has NO benchmark reference (SRG).
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkImportCandidate candidate = new(
			$"invented-stray-{Guid.NewGuid():N}", "Stray Benchmark", "V1", "R1", $"digest-{Guid.NewGuid():N}", []);
		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkRevision strayBenchmark = await benchmarks.ImportRevisionAsync(
			candidate, Waypoint.Core.ComplianceContent.Xccdf.BenchmarkSources.ManualUpload, CancellationToken.None);

		ContentRevision revision = await baselines.RecordStagedRevisionAsync(
			$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);
		// The poison: an SRG profile's active baseline staged WITH a benchmark revision.
		Baseline staged = await baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: strayBenchmark.Id, CancellationToken.None);
		await baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

		return catalogComponent.Id;
	}

	[Fact]
	public async Task CreateScanRun_WithExplicitSingleComponentScope_NeverFansOutLegacyJobToUnselectedTarget()
	{
		// Issue #1122 (round-12 live validation): the failing-test-first proof. Two
		// targets exist in the site; the request explicitly scopes to ONE component on
		// target B only. On unfixed main this asserts false -- RunCreationService's
		// per-target loop falls every target absent from planRequirementsByTarget
		// (target A here) through to BuildLegacyTargetJobSpec, producing an extra scan
		// job against A that the operator never selected, carries no baseline, and runs
		// the pre-catalog hardcoded-profile path (ADR-0023 "explicit subsets never
		// widen silently", issue #733 AC).
		Guid siteId = await CreateSiteAsync("legacy-fanout-site");
		Guid targetA = await CreateTargetAsync(siteId, "vsphere", "vcsa-a", """{"host":"vcsa-a.example.internal"}""");
		Guid targetB = await CreateTargetAsync(siteId, "vsphere", "vcsa-b", """{"host":"vcsa-b.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetB, [new DiscoveredComponent("esxi", "host-b-9001", "esxi-b.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetB, includeRetired: true, CancellationToken.None)).Single();

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { seeded.Id } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		(Guid TargetId, string JobType)[] scanJobs = await ReadScanJobTargetsAsync(runId);

		// Exactly one scan job, naming target B (the selected component's owner) --
		// never target A.
		Assert.Single(scanJobs);
		Assert.Equal(targetB, scanJobs[0].TargetId);
		Assert.DoesNotContain(scanJobs, job => job.TargetId == targetA);
	}

	[Fact]
	public async Task CreateScanRun_WithExplicitMultiComponentScopeOnOneTarget_FansOutOnlyThoseComponents()
	{
		// Explicit scope naming two components on the SAME target must fan out exactly
		// two scan jobs, both against that target -- never a third (legacy) job, and
		// never touching a second, unselected target in the same site.
		Guid siteId = await CreateSiteAsync("multi-component-site");
		Guid targetA = await CreateTargetAsync(siteId, "vsphere", "vcsa-multi", """{"host":"vcsa-multi.example.internal"}""");
		Guid untouchedTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-untouched", """{"host":"vcsa-untouched.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			targetA,
			[
				new DiscoveredComponent("esxi", "host-multi-9001", "esxi-multi-01.example.internal", null, catalogComponentId, "8.0.3"),
				new DiscoveredComponent("esxi", "host-multi-9002", "esxi-multi-02.example.internal", null, catalogComponentId, "8.0.3"),
			],
			CancellationToken.None);
		Guid[] seededIds = (await _components.ListForTargetAsync(targetA, includeRetired: true, CancellationToken.None))
			.Select(component => component.Id).ToArray();
		Assert.Equal(2, seededIds.Length);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = seededIds },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		(Guid TargetId, string JobType)[] scanJobs = await ReadScanJobTargetsAsync(runId);

		Assert.Equal(2, scanJobs.Length);
		Assert.All(scanJobs, job => Assert.Equal(targetA, job.TargetId));
		Assert.DoesNotContain(scanJobs, job => job.TargetId == untouchedTarget);
	}

	[Fact]
	public async Task CreateScanRun_WithAllModeScope_StillFansOutDiscoveredComponents_AndNeverLegacyFansOutAnUnplannableTarget()
	{
		// Requirement 2: the legitimate "all" case must still expand and fan out
		// correctly against refreshed inventory (ADR-0023 §3), while a second target
		// in the same site with no catalog-compatible component gets NO job at all --
		// not the legacy whole-target fallback either. "all" is target_scope-shaped
		// too (ScopeResolutionService.ResolveAsync), so the same per-request gate this
		// issue adds applies to it, not only to "explicit".
		Guid siteId = await CreateSiteAsync("all-mode-site");
		Guid plannableTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-plannable", """{"host":"vcsa-plannable.example.internal"}""");
		Guid unplannableTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-unplannable", """{"host":"vcsa-unplannable.example.internal"}""");

		Guid catalogComponentId = await SeedCompatibleCatalogComponentAsync();
		await _components.UpsertDiscoveredAsync(
			plannableTarget, [new DiscoveredComponent("esxi", "host-all-9001", "esxi-all-01.example.internal", null, catalogComponentId, "8.0.3")], CancellationToken.None);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "all" },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		(Guid TargetId, string JobType)[] scanJobs = await ReadScanJobTargetsAsync(runId);

		Assert.Single(scanJobs);
		Assert.Equal(plannableTarget, scanJobs[0].TargetId);
		Assert.DoesNotContain(scanJobs, job => job.TargetId == unplannableTarget);
	}

	/// <summary>
	/// Reads (target_id, job_type) for every <c>scan</c>-typed job row on
	/// <paramref name="runId"/> directly from Postgres -- the failing-test-first proof
	/// for issue #1122 needs the raw row set, not the HTTP jobs-list projection,
	/// because the defect is an EXTRA row the API would otherwise just as faithfully
	/// echo back.
	/// </summary>
	private async Task<(Guid TargetId, string JobType)[]> ReadScanJobTargetsAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT target_id, job_type FROM jobs WHERE run_id = $1 AND job_type = 'scan' ORDER BY target_id", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		List<(Guid, string)> rows = [];
		while (await reader.ReadAsync())
		{
			rows.Add((reader.GetGuid(0), reader.GetString(1)));
		}

		return [.. rows];
	}

	[Fact]
	public async Task CreateScanRun_WithInvalidTargetScopeMode_Returns400ValidationError()
	{
		Guid siteId = await CreateSiteAsync("bad-mode-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
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
			"TRUNCATE TABLE scan_plan_items, scan_plans, run_scope_snapshots, component_observations, components, jobs, runs, targets, sites, " +
			"baselines, content_revisions, benchmark_component_mappings, benchmark_rules, benchmark_revisions, " +
			"catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components, " +
			"catalog_product_versions, catalog_products, catalog_source_revisions RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
