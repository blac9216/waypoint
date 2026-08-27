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
using Waypoint.Core.Components;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Scans;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #734 (epic #726 Wave 2, ADR-0023/0024), against a real PostgreSQL 16
/// container: compiles an already-resolved component set (the output of
/// <see cref="ScopeResolutionService"/>, PR #854) into an immutable
/// <see cref="ScanPlan"/>. Fixtures are INVENTED managed-object-reference-shaped
/// identifiers only (CLAUDE.md sanitization policy).
///
/// Covers issue #734's plan matrix: catalog-compatible-with-active-baseline (STIG and
/// SRG), no-active-baseline skip, unmapped-benchmark skip, unsupported (no catalog
/// link) skip, the "some skip, siblings still plan" isolation ADR-0023/0024 require,
/// the all-skip => unrunnable-plan case, and determinism/digest-parity across repeated
/// compiles of the same resolved set. Also covers the skip-vs-integrity-failure split
/// (round-2 review of PR #857): the enumerated architecturally-skippable reasons
/// skip-and-continue, but a data-integrity violation (an SRG profile whose active
/// baseline carries a benchmark revision) throws <see cref="ScanPlanIntegrityException"/>
/// and fails the whole plan closed rather than silently dropping the component.
/// </summary>
[Collection("Postgres")]
public sealed class ScanPlannerServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ScanPlannerService _planner = null!;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private TargetRepository _targets = null!;
	private SiteRepository _sites = null!;
	private ConfigDocRepository _configDocs = null!;

	public ScanPlannerServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_components = new ComponentRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_configDocs = new ConfigDocRepository(_fixture.ConnectionString);
		_planner = new ScanPlannerService(_components, _catalog, _baselines, _targets, new PlanConfigResolutionService(_configDocs));
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
				config_versions, config_docs,
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

	private async Task<Guid> SeedSiteAndTargetAsync()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return targetId!.Value;
	}

	private async Task<Guid> SeedComponentLinkedToAsync(Guid targetId, Guid catalogComponentId, string exactVersion, string vendorIdentity)
	{
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"host-{vendorIdentity}", null, catalogComponentId, exactVersion)], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	/// <summary>Unlinked component -- never has a catalog_component_id, so planning it is always <see cref="ScanPlanSkipReasons.Unsupported"/>.</summary>
	private async Task<Guid> SeedUnsupportedComponentAsync(Guid targetId, string vendorIdentity)
	{
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"host-{vendorIdentity}", null, null, "9.9.9")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	/// <summary>Full 0050 identity tree down to one execution profile with one required credential purpose and one declared input. STIG when <paramref name="withBenchmark"/> is a non-null key; SRG otherwise.</summary>
	private async Task<Guid> SeedExecutionProfileAsync(string suffix, string exactVersion, string? withBenchmark)
	{
		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", $"vsphere-{suffix}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, exactVersion, exactVersion, CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"esxi-{suffix}", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null),
			CancellationToken.None);
		CatalogContentRelease contentRelease = await _catalog.UpsertContentReleaseAsync(
			sourceRevision.Id, withBenchmark is null ? CatalogKinds.Srg : CatalogKinds.Stig, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 2, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(executionProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		// Declared OPTIONAL so these planner-shape tests (accepted-item / digest / skip-reason
		// coverage) exercise config resolution without tripping issue #735's missing-required-
		// input skip -- they seed no config doc. The dedicated required-input skip behavior has
		// its own coverage in PlanConfigResolutionServiceTests.
		await _catalog.UpsertDeclaredInputAsync(executionProfile.Id, "target_ip", "string", isRequired: false, CancellationToken.None);

		if (withBenchmark is not null)
		{
			await _catalog.SetBenchmarkReferenceAsync(executionProfile.Id, withBenchmark, "V1R1", CancellationToken.None);
		}

		return executionProfile.Id;
	}

	private async Task<Guid> ActivateBaselineAsync(Guid executionProfileId, string suffix, Guid? benchmarkRevisionId)
	{
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", $"digest-{suffix}", $"revisions/{suffix}", CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId, CancellationToken.None);
		Waypoint.Core.ComplianceContent.BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "admin", CancellationToken.None);
		Assert.Equal(Waypoint.Core.ComplianceContent.BaselineActivationOutcome.Activated, outcome);
		return staged.Id;
	}

	[Fact]
	public async Task CompileAsync_CompatibleComponentWithActiveStigBaseline_ProducesOneAcceptedItem()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("stig", "8.0.3", withBenchmark: "vmware-esxi-8-stig");
		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkRevision benchmarkRevision = await ImportBenchmarkAsync("vmware-esxi-8-stig");
		Guid baselineId = await ActivateBaselineAsync(executionProfileId, "stig", benchmarkRevision.Id);
		Guid componentId = await SeedComponentLinkedToAsync(targetId, (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id, "8.0.3", "host-1001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.True(plan.IsRunnable);
		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Empty(plan.Skips);
		Assert.Equal(componentId, item.ComponentId);
		Assert.Equal(executionProfileId, item.CatalogExecutionProfileId);
		Assert.Equal(baselineId, item.BaselineId);
		Assert.Equal(benchmarkRevision.Id, item.BenchmarkRevisionId);
		Assert.Equal(["vsphere-api"], item.RequiredPurposes);
		Assert.Equal(["target_ip"], item.DeclaredInputNames);
		Assert.False(string.IsNullOrWhiteSpace(plan.PlanDigest));
	}

	[Fact]
	public async Task CompileAsync_CompatibleComponentWithActiveSrgBaseline_ProducesOneAcceptedItemWithNoBenchmark()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("srg", "8.0.3", withBenchmark: null);
		await ActivateBaselineAsync(executionProfileId, "srg", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-2001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Null(item.BenchmarkRevisionId);
		Assert.NotNull(item.BaselineId);
	}

	[Fact]
	public async Task CompileAsync_NoActiveBaseline_IsSkippedNotFailed()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("nobaseline", "8.0.3", withBenchmark: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-3001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.Empty(plan.Items);
		ScanPlanSkip skip = Assert.Single(plan.Skips);
		Assert.Equal(componentId, skip.ComponentId);
		Assert.Equal(ScanPlanSkipReasons.NoActiveBaseline, skip.Reason);

		// ADR-0023 "no run/job rows... zero survives, whole request rejected" is the
		// resolved-scope-level gate the CALLER (RunCreationService) applies; the plan
		// itself always reports IsRunnable accurately as a pure function of what
		// planned, independent of caller policy.
		Assert.False(plan.IsRunnable);
	}

	[Fact]
	public async Task CompileAsync_UnmappedStigBenchmark_IsSkippedNotFailed()
	{
		// A STIG execution profile (has a CatalogBenchmarkReference) whose ACTIVE
		// baseline was staged with no benchmark_revision_id -- a data-integrity gap
		// distinct from "no active baseline at all."
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("unmapped", "8.0.3", withBenchmark: "vmware-esxi-8-stig-unmapped");
		await ActivateBaselineAsync(executionProfileId, "unmapped", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-4001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.Empty(plan.Items);
		ScanPlanSkip skip = Assert.Single(plan.Skips);
		Assert.Equal(ScanPlanSkipReasons.UnmappedBenchmark, skip.Reason);
	}

	[Fact]
	public async Task CompileAsync_UnsupportedComponentWithNoCatalogLink_IsSkippedNotFailed()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid componentId = await SeedUnsupportedComponentAsync(targetId, "host-5001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.Empty(plan.Items);
		ScanPlanSkip skip = Assert.Single(plan.Skips);
		Assert.Equal(ScanPlanSkipReasons.Unsupported, skip.Reason);
	}

	[Fact]
	public async Task CompileAsync_OneUnplannableSiblingAmongMany_StillPlansTheOthers()
	{
		// ADR-0023/0024's core skip-vs-fail isolation rule: a per-component gap never
		// takes down its siblings.
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("mixed", "8.0.3", withBenchmark: null);
		await ActivateBaselineAsync(executionProfileId, "mixed", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid goodComponent = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-6001");
		Guid unsupportedComponent = await SeedUnsupportedComponentAsync(targetId, "host-6002");

		ScanPlan plan = await _planner.CompileAsync(null, [goodComponent, unsupportedComponent], CancellationToken.None);

		Assert.True(plan.IsRunnable);
		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Equal(goodComponent, item.ComponentId);
		ScanPlanSkip skip = Assert.Single(plan.Skips);
		Assert.Equal(unsupportedComponent, skip.ComponentId);
	}

	[Fact]
	public async Task CompileAsync_EmptyResolvedScope_IsAnHonestEmptyPlan()
	{
		ScanPlan plan = await _planner.CompileAsync(null, [], CancellationToken.None);

		Assert.Empty(plan.Items);
		Assert.Empty(plan.Skips);
		Assert.False(plan.IsRunnable);
		Assert.Contains("intentionally empty", plan.Explanation, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task CompileAsync_IsDeterministic_SameDigestAcrossRepeatedCompiles()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("determinism", "8.0.3", withBenchmark: null);
		await ActivateBaselineAsync(executionProfileId, "determinism", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentA = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-7001");
		Guid componentB = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-7002");

		// Deliberately reversed ordering on the second call -- the digest must not
		// depend on caller-supplied ordering (issue #734 AC-4).
		ScanPlan first = await _planner.CompileAsync(null, [componentA, componentB], CancellationToken.None);
		ScanPlan second = await _planner.CompileAsync(null, [componentB, componentA], CancellationToken.None);

		Assert.Equal(first.PlanDigest, second.PlanDigest);
		Assert.Equal(2, first.Items.Count);
	}

	[Fact]
	public async Task CompileAsync_SrgProfileWhoseActiveBaselineCarriesABenchmarkRevision_FailsPlanClosedAsIntegrityViolation()
	{
		// Round-2 review of PR #857 (finding 2): an SRG execution profile has no XCCDF
		// benchmark concept (ADR-0022), so an active baseline carrying a benchmark
		// revision is corrupt/inconsistent catalog state -- a data-integrity violation
		// epic #726 §3/§5 never sanction skipping. It must fail the WHOLE plan closed
		// (distinct plan_integrity_failure diagnostic), never a silent skip row that
		// narrows the run's coverage.
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("corrupt-srg", "8.0.3", withBenchmark: null);

		// Force the integrity violation: an SRG profile's active baseline is staged WITH
		// a benchmark revision id it should never carry. The benchmark_revisions row
		// itself is a real (invented-key) import so the baseline FK is satisfiable --
		// the corruption is purely the SRG/benchmark mismatch the planner guards.
		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkRevision strayBenchmark = await ImportBenchmarkAsync("invented-stray-srg-benchmark");
		await ActivateBaselineAsync(executionProfileId, "corrupt-srg", benchmarkRevisionId: strayBenchmark.Id);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-8001");

		ScanPlanIntegrityException failure = await Assert.ThrowsAsync<ScanPlanIntegrityException>(
			() => _planner.CompileAsync(null, [componentId], CancellationToken.None));

		// Fails closed: the planner threw before returning any ScanPlan at all -- there
		// is no plan object with a silent skip row, and RunCreationService (which
		// compiles before persisting) therefore never writes a run/plan/job. The
		// diagnostic is actionable (names the component + baseline, a distinct 500-class
		// integrity code, not one of the operator-fixable skip reasons).
		Assert.Equal(ScanPlanIntegrityException.ErrorCode, failure.Code);
		Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failure.StatusCode);
		Assert.Equal(componentId, failure.ComponentId);
		Assert.Contains(componentId.ToString(), failure.Detail!, StringComparison.Ordinal);
		Assert.DoesNotContain(failure.Code, ScanPlanSkipReasons.All);
	}

	[Fact]
	public async Task CompileAsync_EveryLegitimateSkipReason_SkipsAndContinuesWithoutFailingThePlan()
	{
		// The counterpart pin to the integrity-failure test above: the enumerated
		// architecturally-skippable reasons (unsupported, no_active_baseline,
		// unmapped_benchmark -- the closed ScanPlanSkipReasons.All set after #857 round 2
		// removed invalid_baseline) must ALL skip-and-continue, never fail the plan,
		// alongside a healthy sibling that still plans (ADR-0023/0024 per-component
		// isolation). One candidate per reason, plus one good component.
		Guid targetId = await SeedSiteAndTargetAsync();

		// Good sibling (SRG, active baseline) -> accepted item.
		Guid goodProfileId = await SeedExecutionProfileAsync("legit-good", "8.0.3", withBenchmark: null);
		await ActivateBaselineAsync(goodProfileId, "legit-good", benchmarkRevisionId: null);
		Guid goodCatalogComponentId = (await _catalog.GetExecutionProfileAsync(goodProfileId, CancellationToken.None))!.Component.Id;
		Guid goodComponent = await SeedComponentLinkedToAsync(targetId, goodCatalogComponentId, "8.0.3", "host-legit-good");

		// unsupported: no catalog link.
		Guid unsupportedComponent = await SeedUnsupportedComponentAsync(targetId, "host-legit-unsupported");

		// no_active_baseline: compatible profile, no baseline activated.
		Guid noBaselineProfileId = await SeedExecutionProfileAsync("legit-nobaseline", "8.0.3", withBenchmark: null);
		Guid noBaselineCatalogComponentId = (await _catalog.GetExecutionProfileAsync(noBaselineProfileId, CancellationToken.None))!.Component.Id;
		Guid noBaselineComponent = await SeedComponentLinkedToAsync(targetId, noBaselineCatalogComponentId, "8.0.3", "host-legit-nobaseline");

		// unmapped_benchmark: STIG profile, active baseline with no benchmark revision.
		Guid unmappedProfileId = await SeedExecutionProfileAsync("legit-unmapped", "8.0.3", withBenchmark: "invented-legit-unmapped-stig");
		await ActivateBaselineAsync(unmappedProfileId, "legit-unmapped", benchmarkRevisionId: null);
		Guid unmappedCatalogComponentId = (await _catalog.GetExecutionProfileAsync(unmappedProfileId, CancellationToken.None))!.Component.Id;
		Guid unmappedComponent = await SeedComponentLinkedToAsync(targetId, unmappedCatalogComponentId, "8.0.3", "host-legit-unmapped");

		ScanPlan plan = await _planner.CompileAsync(
			null, [goodComponent, unsupportedComponent, noBaselineComponent, unmappedComponent], CancellationToken.None);

		// The plan compiled (did not throw): the good sibling planned, every skippable
		// reason produced a skip row and its siblings still proceeded.
		Assert.True(plan.IsRunnable);
		ScanPlanItem accepted = Assert.Single(plan.Items);
		Assert.Equal(goodComponent, accepted.ComponentId);

		Assert.Equal(3, plan.Skips.Count);
		Assert.Contains(plan.Skips, s => s.ComponentId == unsupportedComponent && s.Reason == ScanPlanSkipReasons.Unsupported);
		Assert.Contains(plan.Skips, s => s.ComponentId == noBaselineComponent && s.Reason == ScanPlanSkipReasons.NoActiveBaseline);
		Assert.Contains(plan.Skips, s => s.ComponentId == unmappedComponent && s.Reason == ScanPlanSkipReasons.UnmappedBenchmark);

		// Every skip reason emitted here is a member of the closed skippable set; none is
		// the integrity code (that path throws, proven separately above).
		Assert.All(plan.Skips, s => Assert.Contains(s.Reason, ScanPlanSkipReasons.All));
	}

	private async Task<Waypoint.Core.ComplianceContent.Xccdf.BenchmarkRevision> ImportBenchmarkAsync(string benchmarkKey)
	{
		BenchmarkRepository benchmarks = new(_fixture.ConnectionString);
		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkImportCandidate candidate = new(
			benchmarkKey, "Test Benchmark", "V1", "R1", $"digest-{benchmarkKey}", []);
		return await benchmarks.ImportRevisionAsync(candidate, Waypoint.Core.ComplianceContent.Xccdf.BenchmarkSources.ManualUpload, CancellationToken.None);
	}
}
