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
/// Issue #737 (epic #726 Wave 2 capstone, ADR-0024 "one Postgres component job" per
/// accepted plan item), end to end against real Postgres and the real API: a
/// <c>target_scope</c>-driven scan run now fans out one <c>scan</c> job PER ACCEPTED
/// <c>scan_plan_items</c> row instead of one job per target, each linked via
/// <c>jobs.scan_plan_item_id</c>, prioritized from the item's own catalog priority
/// (<see cref="ScanTargetPriority.ForPlanItem"/>), and carrying only its own
/// component's required credential purposes. <see cref="ScanRunFanOutTests"/> and
/// <see cref="ScanRunTargetScopeTests"/> continue to cover the unaffected legacy
/// target-granular path.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ScanRunComponentFanOutTests : IAsyncLifetime
{
	private sealed class ComponentFanOutApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ComponentFanOutApiFactory(string connectionString)
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
				services.AddSingleton<IProfileRepository>(new ProfileRepository(_connectionString));
				services.AddSingleton<IComponentRepository>(new ComponentRepository(_connectionString));
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
				services.AddSingleton<ScopeResolutionService>();
				services.AddSingleton<IRunScopeSnapshotRepository>(new RunScopeSnapshotRepository(_connectionString));
				services.AddSingleton<IBaselineRepository>(new BaselineRepository(_connectionString));
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
	private ComponentFanOutApiFactory _factory = null!;
	private HttpClient _client = null!;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private Guid _profileId;

	public ScanRunComponentFanOutTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new ComponentFanOutApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
		_components = new ComponentRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);

		ProfileRepository profiles = new(_fixture.ConnectionString);
		await profiles.ReplaceAllAsync(
			[new ProfileUpsert("component-fanout-profile", "Component Fan-Out Test Profile", "1.0.0", "invented-commit-fanout", ProfileStates.Current)],
			CancellationToken.None);
		_profileId = (await profiles.ListAsync(CancellationToken.None)).Single().Id;
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	[Fact]
	public async Task CreateScanRun_WithTwoAcceptedComponentsOnOneTarget_FansOutTwoLinkedJobs_NotOneTargetJob()
	{
		// AC "one Postgres component job per accepted plan item": two independent
		// components (two ESXi hosts) under the SAME target must fan out as TWO scan
		// jobs, each linked to its own scan_plan_items row -- never one job for the
		// whole target (the pre-#737 shape).
		Guid siteId = await CreateSiteAsync("fanout-two-components");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);

		Guid componentA = await SeedComponentAsync(targetId, "host-a", priority: 4);
		Guid componentB = await SeedComponentAsync(targetId, "host-b", priority: 4);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { componentA, componentB } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		List<(Guid? ScanPlanItemId, short Priority)> scanJobs = await ReadScanJobsAsync(runId);
		Assert.Equal(2, scanJobs.Count);
		Assert.All(scanJobs, job => Assert.NotNull(job.ScanPlanItemId));
		Assert.Equal(2, scanJobs.Select(j => j.ScanPlanItemId).Distinct().Count());

		// Each job's scan_plan_item_id resolves back to exactly one of the two
		// accepted components -- proving the FK links a REAL, distinct plan item, not
		// a placeholder shared across jobs.
		HashSet<Guid> linkedComponentIds = [.. await ReadLinkedComponentIdsAsync(runId)];
		Assert.Equal(new HashSet<Guid> { componentA, componentB }, linkedComponentIds);
	}

	[Fact]
	public async Task CreateScanRun_PriorityMapping_UsesCatalogPriorityClampedToQueueBounds()
	{
		// AC-2: "Priority ordering matches the activated catalog while respecting
		// queue validation." catalog_report_groups.priority is itself CHECK-constrained
		// to 1-6 (migration 0050), so this proves the pass-through/clamp is faithful
		// for an in-bounds value, not merely that it never crashes.
		Guid siteId = await CreateSiteAsync("fanout-priority");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);

		Guid componentId = await SeedComponentAsync(targetId, "host-priority", priority: 2);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { componentId } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		List<(Guid? ScanPlanItemId, short Priority)> scanJobs = await ReadScanJobsAsync(runId);
		Assert.Single(scanJobs);
		Assert.Equal((short)2, scanJobs[0].Priority);
	}

	[Fact]
	public async Task CreateScanRun_GatedUnNarrowableComponents_CollapseToExactlyOneWholeTargetJob()
	{
		// Issue #737 item-4 fan-out gate (the round-1 blocker's invariant): a target
		// whose accepted components are all UN-narrowable (here two ssh/target VCSA
		// appliance components -- ScanComponentNarrowing.CanNarrow == false) must NOT fan
		// out one whole-target job per component (that would be N duplicate whole-target
		// scans). They collapse to EXACTLY ONE whole-target job, marked unnarrowed. No
		// configuration can produce duplicate whole-target executions.
		Guid siteId = await CreateSiteAsync("fanout-gated-collapse");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		Guid sshCred = await SeedCredentialAsync("ssh");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);
		await SeedBindingAsync(targetId, "vcsa-ssh", sshCred);

		// Two un-narrowable ssh/target components on the same target.
		await SeedComponentWithRequirementsAsync(
			targetId, "vcsa-svc-a", ["vcsa-ssh"], transport: CatalogTransports.Ssh, selectorKind: CatalogSelectorKinds.Target);
		await SeedComponentWithRequirementsAsync(
			targetId, "vcsa-svc-b", ["vcsa-ssh"], transport: CatalogTransports.Ssh, selectorKind: CatalogSelectorKinds.Target);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "all" },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		// Exactly ONE scan job for the whole target, not two.
		List<Guid> scanJobIds = await ReadScanJobIdsAsync(runId);
		Assert.Single(scanJobIds);

		// It is marked unnarrowed in its payload (a whole-target scan), and still links
		// back into the plan for provenance.
		string payload = await ReadJobPayloadAsync(scanJobIds[0]);
		using JsonDocument doc = JsonDocument.Parse(payload);
		Assert.True(doc.RootElement.TryGetProperty("unnarrowed", out JsonElement unnarrowed) && unnarrowed.GetBoolean());
		Assert.False(doc.RootElement.TryGetProperty("selector_kind", out _), "a collapsed whole-target job carries no object selector.");

		List<(Guid? ScanPlanItemId, short Priority)> scanJobs = await ReadScanJobsAsync(runId);
		Assert.Single(scanJobs);
		Assert.NotNull(scanJobs[0].ScanPlanItemId);
	}

	[Fact]
	public async Task CreateScanRun_MixedNarrowableAndGated_FansOutPerNarrowablePlusExactlyOneCollapsed()
	{
		// Issue #737 item-4: on one target with TWO narrowable esxi components and TWO
		// un-narrowable ssh components, the run fans out one job per narrowable component
		// (2) PLUS exactly ONE collapsed whole-target job for the gated remainder = 3
		// jobs total -- never 4, and never a duplicate whole-target scan.
		Guid siteId = await CreateSiteAsync("fanout-mixed-gate");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		Guid sshCred = await SeedCredentialAsync("ssh");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);
		await SeedBindingAsync(targetId, "vcsa-ssh", sshCred);

		Guid esxiA = await SeedComponentAsync(targetId, "esxi-a", priority: 4);
		Guid esxiB = await SeedComponentAsync(targetId, "esxi-b", priority: 4);
		await SeedComponentWithRequirementsAsync(
			targetId, "vcsa-svc-a", ["vcsa-ssh"], transport: CatalogTransports.Ssh, selectorKind: CatalogSelectorKinds.Target);
		await SeedComponentWithRequirementsAsync(
			targetId, "vcsa-svc-b", ["vcsa-ssh"], transport: CatalogTransports.Ssh, selectorKind: CatalogSelectorKinds.Target);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "all" },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		List<Guid> scanJobIds = await ReadScanJobIdsAsync(runId);
		Assert.Equal(3, scanJobIds.Count);

		// Exactly one of the three is the collapsed whole-target (unnarrowed) job.
		int unnarrowedCount = 0;
		foreach (Guid jobId in scanJobIds)
		{
			using JsonDocument doc = JsonDocument.Parse(await ReadJobPayloadAsync(jobId));
			if (doc.RootElement.TryGetProperty("unnarrowed", out JsonElement u) && u.GetBoolean())
			{
				unnarrowedCount++;
			}
		}

		Assert.Equal(1, unnarrowedCount);
		_ = (esxiA, esxiB);
	}

	[Fact]
	public async Task CreateScanRun_WithTwoComponentsOnOneTarget_JobCredentialBindingsAreItemScoped_NotUnioned()
	{
		// AC-4 "without over-halting": component A requires only vsphere-api; sibling
		// component B (same target) additionally requires vcsa-ssh. Pre-#737 the
		// static per-KIND matrix (or even the #736 per-target union) would attach
		// vcsa-ssh's binding to every job on the target. Post-#737, component A's own
		// job must carry ONLY vsphere-api -- vcsa-ssh must never appear on it, proving
		// credential bindings are scoped to the OWNING COMPONENT's own RequiredPurposes,
		// not the target-level union of every sibling's requirements.
		Guid siteId = await CreateSiteAsync("fanout-item-scoped-bindings");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		Guid sshCred = await SeedCredentialAsync("ssh");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);
		await SeedBindingAsync(targetId, "vcsa-ssh", sshCred);

		Guid componentA = await SeedComponentWithRequirementsAsync(targetId, "esxi-only", ["vsphere-api"]);
		Guid componentB = await SeedComponentWithRequirementsAsync(targetId, "vcsa-dual", ["vsphere-api", "vcsa-ssh"]);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { componentA, componentB } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		Guid jobForA = await ReadJobIdForComponentAsync(runId, componentA);
		Guid jobForB = await ReadJobIdForComponentAsync(runId, componentB);

		HashSet<string> purposesForA = [.. await ReadJobPurposesAsync(jobForA)];
		HashSet<string> purposesForB = [.. await ReadJobPurposesAsync(jobForB)];

		Assert.Equal(new HashSet<string> { "vsphere-api" }, purposesForA);
		Assert.Equal(new HashSet<string> { "vsphere-api", "vcsa-ssh" }, purposesForB);
	}

	[Fact]
	public async Task CreateScanRun_OneComponentJobFailing_NeverHaltsSiblingCompletion()
	{
		// AC-1/AC-5: two independent component jobs on the same run; failing ONE must
		// not prevent the run from reaching a terminal completed_with_failures state
		// once its sibling also finishes -- run completion (JobQueueRepository.TryCompleteRunAsync)
		// already aggregates purely per-job-row under run_id, so this proves that
		// generalizes correctly to component-granular jobs with zero changes to the
		// completion logic itself.
		Guid siteId = await CreateSiteAsync("fanout-sibling-isolation");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);

		Guid componentA = await SeedComponentAsync(targetId, "host-fail", priority: 3);
		Guid componentB = await SeedComponentAsync(targetId, "host-ok", priority: 3);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { componentA, componentB } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		List<Guid> jobIds = await ReadScanJobIdsAsync(runId);
		Assert.Equal(2, jobIds.Count);

		JobQueueRepository jobs = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueueRepository>.Instance);

		// Claim and fail the first job -- its sibling stays queued.
		ClaimedJob? claimedFirst = await jobs.ClaimJobAsync("worker-fail", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		Assert.NotNull(claimedFirst);
		bool advancedFirst = await jobs.AdvanceStateAsync(
			claimedFirst!.Id, "worker-fail", JobStates.Running, JobStates.Failed, "invented failure for sibling-isolation proof", clearLease: true, CancellationToken.None);
		Assert.True(advancedFirst);

		// The run must NOT be terminal yet -- one sibling is still queued.
		Assert.Equal("running", await ReadRunStateAsync(runId));

		// Claim and succeed the second job.
		ClaimedJob? claimedSecond = await jobs.ClaimJobAsync("worker-ok", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		Assert.NotNull(claimedSecond);
		bool advancedSecond = await jobs.AdvanceStateAsync(
			claimedSecond!.Id, "worker-ok", JobStates.Running, JobStates.Done, null, clearLease: true, CancellationToken.None);
		Assert.True(advancedSecond);

		// Now the run reaches its terminal state, reflecting the mixed outcome --
		// the failed sibling never blocked the other component's own completion, and
		// the run honestly reports the failure rather than silently succeeding.
		Assert.Equal("completed_with_failures", await ReadRunStateAsync(runId));
	}

	[Fact]
	public async Task CreateScanRun_TwoReplicasClaimingComponentJobsConcurrently_NeverDoubleClaim()
	{
		// "Replica-safe claims" AC: multiple component-granular scan jobs fanned out
		// from the SAME run, claimed concurrently by two independent JobQueueRepository
		// instances (simulating two runner replicas) -- proves the existing
		// FOR UPDATE SKIP LOCKED claim boundary (ADR-0014) is unweakened by
		// component-granular fan-out, following the same idiom as
		// JobQueueRepositoryClaimTests.TwoDispatcherInstances_RacingForTheSameQueue_NeverClaimTheSameJob.
		Guid siteId = await CreateSiteAsync("fanout-concurrent-claim");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01");
		Guid vcenterCred = await SeedCredentialAsync("vcenter");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);

		Guid[] componentIds = new Guid[20];
		for (int i = 0; i < componentIds.Length; i++)
		{
			componentIds[i] = await SeedComponentAsync(targetId, $"host-{i}", priority: (i % 6) + 1);
		}

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = componentIds },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);
		List<Guid> jobIds = await ReadScanJobIdsAsync(runId);
		Assert.Equal(componentIds.Length, jobIds.Count);

		JobQueueRepository dispatcherA = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueueRepository>.Instance);
		JobQueueRepository dispatcherB = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueueRepository>.Instance);

		List<ClaimedJob> claimedByA = [];
		List<ClaimedJob> claimedByB = [];

		async Task DrainAsync(JobQueueRepository dispatcher, List<ClaimedJob> into)
		{
			while (true)
			{
				ClaimedJob? job = await dispatcher.ClaimJobAsync(
					$"worker-{Guid.NewGuid():N}", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
				if (job is null || job.RunId != runId)
				{
					return;
				}

				into.Add(job);
			}
		}

		await Task.WhenAll(DrainAsync(dispatcherA, claimedByA), DrainAsync(dispatcherB, claimedByB));

		List<Guid> allClaimed = [.. claimedByA.Select(j => j.Id), .. claimedByB.Select(j => j.Id)];
		Assert.Equal(componentIds.Length, allClaimed.Count);
		Assert.Equal(componentIds.Length, allClaimed.Distinct().Count());
	}

	/// <summary>Seeds one complete compatible catalog chain (SRG, no benchmark needed) with an active baseline and links a discovered component on <paramref name="targetId"/> to it, at the given catalog report-group priority.</summary>
	private async Task<Guid> SeedComponentAsync(Guid targetId, string suffix, int priority)
	{
		return await SeedComponentWithRequirementsAsync(targetId, suffix, ["vsphere-api"], priority);
	}

	private async Task<Guid> SeedComponentWithRequirementsAsync(
		Guid targetId,
		string suffix,
		string[] purposes,
		int priority = 1,
		string transport = CatalogTransports.VMware,
		string selectorKind = CatalogSelectorKinds.Esxi,
		string? selectorName = null)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{suffix}-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", $"vsphere-{suffix}-{Guid.NewGuid():N}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"comp-{suffix}", "Component", transport, selectorKind, selectorName, null),
			CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{suffix}-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}-{Guid.NewGuid():N}", "Test Group", priority, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		foreach (string purpose in purposes)
		{
			await _catalog.AddCredentialRequirementAsync(executionProfile.Id, purpose, isRequired: true, CancellationToken.None);
		}

		ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
			$"commit-{suffix}-{Guid.NewGuid():N}", $"digest-{suffix}-{Guid.NewGuid():N}", $"revisions/{suffix}-{Guid.NewGuid():N}", CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

		// advanceAbsence: false -- this suite seeds several independent components
		// under the SAME target across SEPARATE UpsertDiscoveredAsync calls (each
		// building its own catalog/baseline chain first). The default
		// advanceAbsence: true marks any component under the target NOT present in
		// THIS call's item list absent -- exactly the discovery "this pass saw
		// everything" semantics a real single discovery sweep needs, but wrong for a
		// test that deliberately seeds one component per call: a second call would
		// silently retire/absent the first call's component before the run is ever
		// created. See ComponentRepository.UpsertDiscoveredAsync's own doc comment
		// (issue #865) for the same "partial view must not advance absence" rule this
		// borrows.
		string vendorIdentity = $"host-{suffix}-{Guid.NewGuid():N}";
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"{vendorIdentity}.example.internal", null, catalogComponent.Id, "8.0.3")], CancellationToken.None, advanceAbsence: false);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	private async Task<HttpResponseMessage> PostRunAsync(object scopeBody)
	{
		return await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(scopeBody) });
	}

	private static async Task<Guid> ReadRunIdAsync(HttpResponseMessage response)
	{
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return Guid.Parse(created.RootElement.GetProperty("run_id").GetString()!);
	}

	private async Task<List<(Guid? ScanPlanItemId, short Priority)>> ReadScanJobsAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT scan_plan_item_id, priority FROM jobs WHERE run_id = $1 AND job_type = 'scan'", connection);
		command.Parameters.AddWithValue(runId);
		List<(Guid?, short)> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			results.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt16(1)));
		}

		return results;
	}

	private async Task<List<Guid>> ReadScanJobIdsAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT id FROM jobs WHERE run_id = $1 AND job_type = 'scan'", connection);
		command.Parameters.AddWithValue(runId);
		List<Guid> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			results.Add(reader.GetGuid(0));
		}

		return results;
	}

	private async Task<List<Guid>> ReadLinkedComponentIdsAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			SELECT i.component_id
			FROM jobs j
			JOIN scan_plan_items i ON i.id = j.scan_plan_item_id
			WHERE j.run_id = $1 AND j.job_type = 'scan'
			""", connection);
		command.Parameters.AddWithValue(runId);
		List<Guid> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			results.Add(reader.GetGuid(0));
		}

		return results;
	}

	private async Task<Guid> ReadJobIdForComponentAsync(Guid runId, Guid componentId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			SELECT j.id
			FROM jobs j
			JOIN scan_plan_items i ON i.id = j.scan_plan_item_id
			WHERE j.run_id = $1 AND j.job_type = 'scan' AND i.component_id = $2
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(componentId);
		object? result = await command.ExecuteScalarAsync();
		Assert.NotNull(result);
		return (Guid)result!;
	}

	private async Task<string> ReadJobPayloadAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT payload FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		object? result = await command.ExecuteScalarAsync();
		Assert.NotNull(result);
		return (string)result!;
	}

	private async Task<List<string>> ReadJobPurposesAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT purpose FROM job_credential_bindings WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		List<string> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			results.Add(reader.GetString(0));
		}

		return results;
	}

	private async Task<string> ReadRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		object? result = await command.ExecuteScalarAsync();
		Assert.NotNull(result);
		return (string)result!;
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}" });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> CreateTargetAsync(Guid siteId, string kind, string name)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, discovery_status, last_refreshed)
			VALUES ($1, $2, $3, $4::jsonb, 'discovered', now())
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(siteId);
		command.Parameters.AddWithValue(kind);
		command.Parameters.AddWithValue($"{name}-{Guid.NewGuid():N}");
		command.Parameters.AddWithValue($$"""{"host":"{{name}}.example.internal"}""");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedCredentialAsync(string credentialType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type, username) VALUES ($1, $2, 'svc-scan@example.internal') RETURNING id", connection);
		command.Parameters.AddWithValue($"fanout-cred-{credentialType}-{Guid.NewGuid():N}");
		command.Parameters.AddWithValue(credentialType);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task SeedBindingAsync(Guid targetId, string purpose, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO target_credential_bindings (target_id, purpose, credential_id) VALUES ($1, $2, $3)", connection);
		command.Parameters.AddWithValue(targetId);
		command.Parameters.AddWithValue(purpose);
		command.Parameters.AddWithValue(credentialId);
		await command.ExecuteNonQueryAsync();
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
			"""
			TRUNCATE TABLE
				job_credential_bindings, jobs, runs, run_secrets, run_scope_snapshots, scan_plan_items, scan_plans,
				baselines, content_revisions,
				component_observations, components, target_credential_bindings, targets, sites, credentials,
				catalog_import_report_entries, catalog_import_reports, catalog_declared_inputs,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions,
				benchmark_component_mappings, benchmark_rules, benchmark_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
