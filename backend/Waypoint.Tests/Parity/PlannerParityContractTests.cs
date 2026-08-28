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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Core.Components;
using Waypoint.Core.Scans;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Issue #749 PLANNER-PARITY slice (epic #726), extending the merged catalog-parity
/// suite (PR #836) now that the immutable planner (PR #857, issue #734) exists. Each
/// <see cref="PlannerParityRow"/> in <see cref="PlannerDerivationMatrix"/> seeds an
/// invented catalog execution profile + active baseline for a documented family, then
/// seeds the family's own documented concrete-instance shape (e.g. two vCenters, three
/// ESXi hosts) as real <c>components</c> rows linked to that catalog identity, and
/// asserts <c>ScanPlannerService.CompileAsync</c> yields EXACTLY the expected accepted
/// <see cref="ScanPlanItem"/> set: one item per concrete instance (never per
/// capability-group row), with the documented transport, selector, priority/report
/// group, benchmark identity, and output kind.
///
/// <b>Honest boundary (see <see cref="PlannerDerivationMatrix"/>'s own doc comment):</b>
/// this suite asserts PLAN-ITEM expansion, the planner's own output, which is already
/// component-granular. Job-row fan-out remains target-granular until issue #737; this
/// suite does not touch job rows at all.
/// </summary>
[Collection("Postgres")]
public sealed class PlannerParityContractTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ScanPlannerService _planner = null!;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private BenchmarkRepository _benchmarks = null!;
	private TargetRepository _targets = null!;
	private SiteRepository _sites = null!;

	public PlannerParityContractTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_components = new ComponentRepository(_fixture.ConnectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString));
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);
		_benchmarks = new BenchmarkRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_sites = new SiteRepository(_fixture.ConnectionString);
		// Issue #735: ScanPlannerService now also resolves each item's config-doc
		// snapshot -- mechanical constructor-signature update only, matching every
		// other ScanPlannerService call site; no change to this file's own assertions.
		_planner = new ScanPlannerService(
			_components, _catalog, _baselines, _targets,
			new Waypoint.Infrastructure.ConfigDocs.PlanConfigResolutionService(new Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository(_fixture.ConnectionString)));
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				scan_plan_items, scan_plans, run_scope_snapshots,
				baselines, content_revisions,
				component_observations, components, targets, sites,
				catalog_import_report_entries, catalog_import_reports, catalog_declared_inputs,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions,
				benchmark_component_mappings, benchmark_rules, benchmark_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	public static IEnumerable<object[]> MatrixRows() =>
		PlannerDerivationMatrix.Rows.Select(row => new object[] { row });

	[Theory]
	[MemberData(nameof(MatrixRows))]
	public async Task CompileAsync_FamilyRow_ExpandsToExactlyOnePlanItemPerConcreteInstance(PlannerParityRow row)
	{
		Guid siteId = (await _sites.CreateAsync($"site-{row.MatrixRowId}-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{row.MatrixRowId}-{Guid.NewGuid():N}", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		BenchmarkRevision? benchmarkRevision = row.BenchmarkKey is null
			? null
			: await ImportBenchmarkAsync(row.BenchmarkKey);

		List<(Guid ComponentId, PlannerParityInstance Instance)> seeded = [];

		foreach (PlannerParityInstance instance in row.Instances)
		{
			Guid executionProfileId = await SeedExecutionProfileAsync(row, instance, benchmarkRevision?.Id);
			await ActivateBaselineAsync(executionProfileId, $"{row.MatrixRowId}-{instance.ComponentKey}", benchmarkRevision?.Id);
			CatalogExecutionProfileDetail detail = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!;

			for (int i = 0; i < instance.InstanceCount; i++)
			{
				string vendorIdentity = $"host-{row.MatrixRowId}-{instance.ComponentKey}-{i}";
				Guid componentId = await SeedComponentLinkedToAsync(targetId!.Value, detail.Component.Id, row.ProductVersionKey, vendorIdentity);
				seeded.Add((componentId, instance));
			}
		}

		ScanPlan plan = await _planner.CompileAsync(null, [.. seeded.Select(s => s.ComponentId)], CancellationToken.None);

		// Deliverable 1's core assertion: exact expansion count, one plan item per
		// concrete instance -- never one item per documented capability-group row.
		Assert.Empty(plan.Skips);
		Assert.True(plan.IsRunnable);
		Assert.Equal(row.TotalInstanceCount, plan.Items.Count);
		Assert.Equal(seeded.Count, plan.Items.Count);

		foreach ((Guid componentId, PlannerParityInstance instance) in seeded)
		{
			ScanPlanItem item = Assert.Single(plan.Items, i => i.ComponentId == componentId);

			Assert.Equal(row.Transport, item.Transport);
			Assert.Equal(instance.SelectorKind, item.SelectorKind);
			Assert.Equal(instance.SelectorName, item.SelectorName);
			Assert.Equal(instance.ReportGroupKey, item.ReportGroupKey);
			Assert.Equal(instance.Priority, item.Priority);
			Assert.Equal(row.OutputKind, item.OutputKind);

			// Scope note (see PlannerDerivationMatrix doc comment): #736 is actively
			// changing HOW purposes derive, so assert non-empty + deterministic ordering,
			// not a hardcoded set beyond what this fixture itself declared.
			Assert.NotEmpty(item.RequiredPurposes);
			Assert.Equal(item.RequiredPurposes.OrderBy(p => p, StringComparer.Ordinal), item.RequiredPurposes);

			if (row.Kind == CatalogKinds.Stig)
			{
				Assert.NotNull(item.BaselineId);
				Assert.NotNull(item.BenchmarkRevisionId);
				Assert.Equal(benchmarkRevision!.Id, item.BenchmarkRevisionId);
			}
			else
			{
				Assert.NotNull(item.BaselineId);
				Assert.Null(item.BenchmarkRevisionId);
			}
		}

		// Determinism (mirrors ScanPlannerServiceTests' own digest-parity coverage,
		// extended to a real multi-instance family fixture): recompiling the same
		// resolved set in reverse order yields the same digest and item count.
		ScanPlan reCompiled = await _planner.CompileAsync(
			null, [.. seeded.Select(s => s.ComponentId).Reverse()], CancellationToken.None);
		Assert.Equal(plan.PlanDigest, reCompiled.PlanDigest);
		Assert.Equal(plan.Items.Count, reCompiled.Items.Count);
	}

	/// <summary>
	/// Deliverable 2 (skip-parity): a family with no active baseline and a family with
	/// an unmapped STIG benchmark each produce explicit skips while a healthy sibling in
	/// the SAME plan compilation still plans -- epic #726 §3/§5's per-component
	/// isolation, exercised end-to-end through a real multi-family planner compile
	/// rather than the single-component cases <c>ScanPlannerServiceTests</c> already
	/// covers.
	/// </summary>
	[Fact]
	public async Task CompileAsync_SkipParity_NoActiveBaselineAndUnmappedBenchmark_SkipAcrossTwoFamiliesWithHealthySiblingStillPlanned()
	{
		Guid siteId = (await _sites.CreateAsync($"site-skip-parity-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-skip-parity-{Guid.NewGuid():N}", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		// Healthy sibling: vSphere SRG family (no benchmark concept), active baseline.
		PlannerParityRow healthyRow = PlannerDerivationMatrix.Rows.Single(r => r.MatrixRowId == "vsphere-9-0-srg-vmware");
		PlannerParityInstance healthyInstance = healthyRow.Instances[0];
		Guid healthyProfileId = await SeedExecutionProfileAsync(healthyRow, healthyInstance, benchmarkRevisionId: null);
		await ActivateBaselineAsync(healthyProfileId, "skip-parity-healthy", benchmarkRevisionId: null);
		CatalogExecutionProfileDetail healthyDetail = (await _catalog.GetExecutionProfileAsync(healthyProfileId, CancellationToken.None))!;
		Guid healthyComponent = await SeedComponentLinkedToAsync(targetId!.Value, healthyDetail.Component.Id, healthyRow.ProductVersionKey, "host-skip-parity-healthy");

		// Family 1: NSX 4-x STIG component with a compatible profile but NO active
		// baseline -- ScanPlanSkipReasons.NoActiveBaseline.
		PlannerParityRow nsxRow = PlannerDerivationMatrix.Rows.Single(r => r.MatrixRowId == "nsx-4-x-stig");
		PlannerParityInstance nsxInstance = nsxRow.Instances[0];
		Guid nsxProfileId = await SeedExecutionProfileAsync(nsxRow, nsxInstance, benchmarkRevisionId: null);
		CatalogExecutionProfileDetail nsxDetail = (await _catalog.GetExecutionProfileAsync(nsxProfileId, CancellationToken.None))!;
		Guid nsxComponent = await SeedComponentLinkedToAsync(targetId.Value, nsxDetail.Component.Id, nsxRow.ProductVersionKey, "host-skip-parity-nsx-nobaseline");

		// Family 2: vSphere 8-0 STIG (vmware) vCenter component whose active baseline has
		// no benchmark revision mapped -- ScanPlanSkipReasons.UnmappedBenchmark.
		PlannerParityRow vsphereStigRow = PlannerDerivationMatrix.Rows.Single(r => r.MatrixRowId == "vsphere-8-0-stig-vmware");
		PlannerParityInstance vcenterInstance = vsphereStigRow.Instances.Single(i => i.ComponentKey == "vcenter");
		Guid unmappedProfileId = await SeedExecutionProfileAsync(vsphereStigRow, vcenterInstance, benchmarkRevisionId: null);
		await ActivateBaselineAsync(unmappedProfileId, "skip-parity-unmapped", benchmarkRevisionId: null);
		CatalogExecutionProfileDetail unmappedDetail = (await _catalog.GetExecutionProfileAsync(unmappedProfileId, CancellationToken.None))!;
		Guid unmappedComponent = await SeedComponentLinkedToAsync(targetId.Value, unmappedDetail.Component.Id, vsphereStigRow.ProductVersionKey, "host-skip-parity-unmapped-benchmark");

		ScanPlan plan = await _planner.CompileAsync(
			null, [healthyComponent, nsxComponent, unmappedComponent], CancellationToken.None);

		Assert.True(plan.IsRunnable);
		ScanPlanItem accepted = Assert.Single(plan.Items);
		Assert.Equal(healthyComponent, accepted.ComponentId);

		Assert.Equal(2, plan.Skips.Count);
		Assert.Contains(plan.Skips, s => s.ComponentId == nsxComponent && s.Reason == ScanPlanSkipReasons.NoActiveBaseline);
		Assert.Contains(plan.Skips, s => s.ComponentId == unmappedComponent && s.Reason == ScanPlanSkipReasons.UnmappedBenchmark);
	}

	/// <summary>
	/// Mutation honesty (issue #749 deliverable 4): proves the transport assertion in
	/// the theory above is load-bearing. Swaps the NSX 4-x STIG row's expected transport
	/// to <c>vmware</c> (wrong -- NSX is <c>nsx-api</c>) and confirms the theory actually
	/// fails, then restores it. See the PR body for the observed failure output; this
	/// test documents the restored, correct state and stands as the permanent regression
	/// guard that the mutated value would have caught.
	/// </summary>
	[Fact]
	public void MutationGuard_NsxRowTransport_IsNsxApi_NeverVMwareOrSsh()
	{
		PlannerParityRow nsxRow = PlannerDerivationMatrix.Rows.Single(r => r.MatrixRowId == "nsx-4-x-stig");
		Assert.Equal(CatalogTransports.NsxApi, nsxRow.Transport);
		Assert.NotEqual(CatalogTransports.VMware, nsxRow.Transport);
		Assert.NotEqual(CatalogTransports.Ssh, nsxRow.Transport);
	}

	private async Task<Guid> SeedComponentLinkedToAsync(Guid targetId, Guid catalogComponentId, string exactVersion, string vendorIdentity)
	{
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"host-{vendorIdentity}", null, catalogComponentId, exactVersion)], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	private async Task<Guid> SeedExecutionProfileAsync(PlannerParityRow row, PlannerParityInstance instance, Guid? benchmarkRevisionId)
	{
		string suffix = $"{row.MatrixRowId}-{instance.ComponentKey}";
		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, row.VendorFamily, $"{row.VendorFamily}-{suffix}", row.VendorFamily, CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, row.ProductVersionKey, row.ProductVersionKey, CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"{instance.ComponentKey}-{suffix}", instance.ComponentKey, row.Transport, instance.SelectorKind, instance.SelectorName, null),
			CancellationToken.None);
		CatalogContentRelease contentRelease = await _catalog.UpsertContentReleaseAsync(
			sourceRevision.Id, row.Kind, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync(instance.ReportGroupKey, instance.ReportGroupKey, instance.Priority, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "1.0.0", row.OutputKind, CancellationToken.None);

		foreach (string purpose in instance.CredentialPurposes)
		{
			await _catalog.AddCredentialRequirementAsync(executionProfile.Id, purpose, isRequired: true, CancellationToken.None);
		}

		if (row.BenchmarkKey is not null)
		{
			await _catalog.SetBenchmarkReferenceAsync(executionProfile.Id, row.BenchmarkKey, "V1R1", CancellationToken.None);
		}

		return executionProfile.Id;
	}

	private async Task<Guid> ActivateBaselineAsync(Guid executionProfileId, string suffix, Guid? benchmarkRevisionId)
	{
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", $"digest-{suffix}", $"revisions/{suffix}", CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId, CancellationToken.None);
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "admin", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.Activated, outcome);
		return staged.Id;
	}

	private async Task<BenchmarkRevision> ImportBenchmarkAsync(string benchmarkKey)
	{
		BenchmarkImportCandidate candidate = new(benchmarkKey, "Test Benchmark", "V1", "R1", $"digest-{benchmarkKey}", []);
		return await _benchmarks.ImportRevisionAsync(candidate, BenchmarkSources.ManualUpload, CancellationToken.None);
	}
}
