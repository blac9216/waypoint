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
/// SRG), no-active-baseline skip, unsupported (no catalog link) skip, the "some skip,
/// siblings still plan" isolation ADR-0023/0024 require, the all-skip =>
/// unrunnable-plan case, and determinism/digest-parity across repeated compiles of the
/// same resolved set. Also covers the skip-vs-integrity-failure split (round-2 review
/// of PR #857): the enumerated architecturally-skippable reasons skip-and-continue, but
/// a data-integrity violation (an SRG profile whose active baseline carries a benchmark
/// revision) throws <see cref="ScanPlanIntegrityException"/> and fails the whole plan
/// closed rather than silently dropping the component. Issue #1021: an unmapped STIG
/// benchmark is no longer in the skippable set at all -- it plans profile-only with
/// <see cref="ScanPlanItem.IsBenchmarkMissing"/> set (see the coexisting-SRG repro test,
/// the issue's own live-lab scenario).
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

		_components = new ComponentRepository(_fixture.ConnectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString));
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

	/// <summary>
	/// Issue #738 AC "linked-vCenter identity: no duplicate execution for linked
	/// environments" -- pinned at the layer that actually enforces it.
	/// <see cref="ScanPlannerService.CompileAsync"/> dedupes its
	/// <c>resolvedComponentIds</c> input via <c>.Distinct()</c> before compiling, so a
	/// caller that (e.g. through an overlapping "all" scope resolution, or a caller bug)
	/// requests the SAME component id more than once still produces exactly ONE accepted
	/// plan item -- never two sibling items/jobs racing to scan the identical vCenter
	/// object. This is a vcenter-selector component specifically (the transport this
	/// issue's execution path targets), proving the structural dedupe holds for it too,
	/// not only for the esxi/vm shapes the pre-existing planner tests exercise.
	/// </summary>
	[Fact]
	public async Task CompileAsync_SameComponentIdRequestedTwice_ProducesExactlyOneAcceptedItem()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedVCenterExecutionProfileAsync("dedupe", "8.0.3");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		await ActivateBaselineAsync(executionProfileId, "dedupe", benchmarkRevisionId: null);
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "vcenter-dedupe-6001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId, componentId], CancellationToken.None);

		Assert.True(plan.IsRunnable);
		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Equal(componentId, item.ComponentId);
		Assert.Empty(plan.Skips);
	}

	/// <summary>Same shape as <see cref="SeedExecutionProfileAsync"/> but the catalog component's selector is <c>vcenter</c> (this issue's execution path) instead of <c>esxi</c>.</summary>
	private async Task<Guid> SeedVCenterExecutionProfileAsync(string suffix, string exactVersion)
	{
		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", $"vsphere-{suffix}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, exactVersion, exactVersion, CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"vcenter-{suffix}", "vCenter", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null),
			CancellationToken.None);
		CatalogContentRelease contentRelease = await _catalog.UpsertContentReleaseAsync(
			sourceRevision.Id, CatalogKinds.Srg, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 3, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(executionProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		return executionProfile.Id;
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

	/// <summary>
	/// Issue #743: an ssh-transport item FREEZES the catalog component's declared sudo
	/// policy (migration 0074) into the plan item -- sudo is CONTENT knowledge (vIDM:
	/// sudo with password; Photon: passwordless sudo; the Aria family: no sudo), never
	/// inferred from the target kind or the credential row. A non-ssh transport carries
	/// no sudo concept and freezes null, so every pre-#743 item shape is unchanged.
	/// </summary>
	[Theory]
	[InlineData(true, false)]   // Photon shape: sudo, passwordless.
	[InlineData(true, true)]    // vIDM shape: sudo, password required.
	[InlineData(false, true)]   // Aria shape: no sudo (the password flag is then moot).
	public async Task CompileAsync_SshTransportItem_FreezesCatalogDeclaredSudoPolicy(bool requiresSudo, bool sudoRequiresPassword)
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		string suffix = $"sudo-{requiresSudo}-{sudoRequiresPassword}";
		Guid executionProfileId = await SeedSshTargetExecutionProfileAsync(suffix, "8.0.3", requiresSudo, sudoRequiresPassword);
		await ActivateBaselineAsync(executionProfileId, suffix, benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", $"appliance-{suffix}");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Equal(requiresSudo, item.RequiresSudo);
		Assert.Equal(sudoRequiresPassword, item.SudoRequiresPassword);
	}

	/// <summary>
	/// Issue #1094 (AC3): the IMPORTER's shape end to end -- import (a
	/// <see cref="CatalogComponentDefinition"/> that declares NO sudo policy, which is
	/// every component the vendor-content importer creates, since that content carries no
	/// sudo signal to derive one from) through the catalog write path, into the frozen
	/// plan item. Before #1094 the write path never named the two columns, so the row took
	/// the table DEFAULT by accident; now the record's explicit, documented default (no
	/// sudo) is what reaches the plan boundary. Pinning it HERE and not only at the
	/// catalog read is what protects the live SSH/SRG lab leg: the invocation reads the
	/// frozen item, not the catalog.
	/// </summary>
	[Fact]
	public async Task CompileAsync_ImportedSshComponentWithNoDeclaredSudoPolicy_FreezesExplicitDefault()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedSshTargetExecutionProfileAsync("imported-nosudo", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "imported-nosudo", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "appliance-imported-nosudo");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.False(item.RequiresSudo);
		Assert.True(item.SudoRequiresPassword);
	}

	/// <summary>Issue #743: a vmware-transport item has no sudo concept -- both fields stay null (pre-#743 shape preserved).</summary>
	[Fact]
	public async Task CompileAsync_NonSshTransportItem_FreezesNoSudoPolicy()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("nosudo", "8.0.3", withBenchmark: null);
		await ActivateBaselineAsync(executionProfileId, "nosudo", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-nosudo-9101");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Null(item.RequiresSudo);
		Assert.Null(item.SudoRequiresPassword);
	}

	/// <summary>
	/// Issue #743: two plans identical except for the catalog's declared sudo policy
	/// must NOT collide on the plan digest -- sudo changes the InSpec invocation itself,
	/// so it is part of what the accepted item commits to executing.
	/// </summary>
	[Fact]
	public async Task CompileAsync_SudoPolicyChange_ProducesADifferentPlanDigest()
	{
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedSshTargetExecutionProfileAsync("digest-sudo", "8.0.3", requiresSudo: false, sudoRequiresPassword: true);
		await ActivateBaselineAsync(executionProfileId, "digest-sudo", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "appliance-digest-sudo");

		ScanPlan before = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand update = new(
				"UPDATE catalog_components SET requires_sudo = true, sudo_requires_password = false WHERE id = $1", connection);
			update.Parameters.AddWithValue(catalogComponentId);
			await update.ExecuteNonQueryAsync();
		}

		ScanPlan after = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.NotEqual(before.PlanDigest, after.PlanDigest);
	}

	/// <summary>
	/// Whole-appliance ssh/target execution profile (issue #743, migration 0074).
	/// The sudo policy is declared on <see cref="CatalogComponentDefinition"/> and written
	/// by <c>UpsertComponentAsync</c> -- the catalog write path that owns those columns
	/// since issue #1094 -- so every plan-freeze assertion below depends on that write
	/// path rather than on a raw <c>UPDATE</c> the production importer never performs.
	/// Passing no policy (both nulls) reproduces the IMPORTER's shape: a definition that
	/// declares nothing and therefore takes the record's explicit no-sudo default.
	/// </summary>
	private async Task<Guid> SeedSshTargetExecutionProfileAsync(string suffix, string exactVersion, bool? requiresSudo = null, bool? sudoRequiresPassword = null)
	{
		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", $"appliance-{suffix}", "Invented SSH Appliance", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, exactVersion, exactVersion, CancellationToken.None);
		CatalogComponentDefinition definition = requiresSudo is null && sudoRequiresPassword is null
			? new CatalogComponentDefinition($"esxi-{suffix}", "Invented SSH Appliance", CatalogTransports.Ssh, CatalogSelectorKinds.Target, null, null)
			: new CatalogComponentDefinition(
				$"esxi-{suffix}", "Invented SSH Appliance", CatalogTransports.Ssh, CatalogSelectorKinds.Target, null, null,
				RequiresSudo: requiresSudo!.Value, SudoRequiresPassword: sudoRequiresPassword!.Value);
		CatalogComponent component = await _catalog.UpsertComponentAsync(productVersion.Id, definition, CancellationToken.None);

		CatalogContentRelease contentRelease = await _catalog.UpsertContentReleaseAsync(
			sourceRevision.Id, CatalogKinds.Srg, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 6, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.Hdf, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(executionProfile.Id, "srg-ssh", isRequired: true, CancellationToken.None);
		return executionProfile.Id;
	}

	/// <summary>
	/// Issue #1012 (round-8 report): before this fix, two co-existing execution
	/// profiles for the SAME catalog component -- an imported, credential-less SRG
	/// profile and a seeded, credential-bearing STIG profile -- were tiebroken purely
	/// by the lowest <c>ExecutionProfile.Id</c>, which is insertion order, not a
	/// documented preference. This is the exact shape the round-8 live evidence
	/// describes: the imported profile happened to have the lower id and won, so every
	/// scan job fanned out with no credential. The fix orders by
	/// docs/compliance-parity.md's documented report-group Priority column first (STIG
	/// priorities 1-5 all sort below every SRG's priority 6), so the STIG profile wins
	/// regardless of which one was inserted first -- this seeds the STIG profile with
	/// the HIGHER id specifically to prove the ordering is priority-driven, not an
	/// accidental id coincidence.
	/// </summary>
	[Fact]
	public async Task CompileAsync_TwoProfilesForSameComponent_PrefersLowerDocumentedPriorityOverInsertionOrder()
	{
		Guid targetId = await SeedSiteAndTargetAsync();

		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync("source-tiebreak", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", "vsphere-tiebreak", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi-tiebreak", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);

		// Imported (SRG, priority 6, no benchmark) profile created FIRST -- lower id --
		// with ONLY vsphere-api required (matches what CredentialRequirementDerivation
		// now derives for a vmware-transport component).
		CatalogContentRelease srgRelease = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Srg, "release-srg-tiebreak", "Test SRG Release", CancellationToken.None);
		CatalogReportGroup srgGroup = await _catalog.UpsertReportGroupAsync("srg-tiebreak", "SRG", 6, CancellationToken.None);
		CatalogExecutionProfile srgProfile = await _catalog.CreateExecutionProfileAsync(component.Id, srgRelease.Id, srgGroup.Id, "1.0.0", CatalogOutputKinds.Hdf, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(srgProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		await ActivateBaselineAsync(srgProfile.Id, "tiebreak-srg", benchmarkRevisionId: null);

		// Seeded (STIG, priority 4 -- ESXi STIG) profile created SECOND -- higher id --
		// with vsphere-api required, mirroring a real seed row.
		CatalogContentRelease stigRelease = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Stig, "release-stig-tiebreak", "Test STIG Release", CancellationToken.None);
		CatalogReportGroup stigGroup = await _catalog.UpsertReportGroupAsync("esxi-stig-tiebreak", "ESXi STIG", 4, CancellationToken.None);
		CatalogExecutionProfile stigProfile = await _catalog.CreateExecutionProfileAsync(component.Id, stigRelease.Id, stigGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(stigProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkRevision benchmarkRevision = await ImportBenchmarkAsync("tiebreak-stig-benchmark");
		await _catalog.SetBenchmarkReferenceAsync(stigProfile.Id, "tiebreak-stig-benchmark", "V1R1", CancellationToken.None);
		await ActivateBaselineAsync(stigProfile.Id, "tiebreak-stig", benchmarkRevision.Id);

		Assert.True(srgProfile.Id.CompareTo(stigProfile.Id) < 0 || srgProfile.CreatedAt <= stigProfile.CreatedAt,
			"Test setup invariant: the SRG profile must be created (and therefore ordered) before the STIG profile for this to actually exercise the tiebreak.");

		Guid componentId = await SeedComponentLinkedToAsync(targetId, component.Id, "8.0.3", "host-tiebreak-9001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Equal(stigProfile.Id, item.CatalogExecutionProfileId);
		Assert.NotNull(item.BenchmarkRevisionId);
		Assert.Empty(plan.Skips);
	}

	/// <summary>
	/// Issue #1012 defense-in-depth: a resolved execution profile on a documented
	/// credentialed transport (vmware) whose <c>catalog_credential_requirements</c>
	/// resolved to an EMPTY set -- the exact round-8 shape an importer-promoted profile
	/// could reach before <see cref="Waypoint.Core.ComplianceContent.CredentialRequirementDerivation"/>
	/// existed -- is skipped at PLAN-COMPILE time with an honest, previewable reason,
	/// never silently accepted with a zero-length RequiredPurposes that would let
	/// RunCreationService find no credential gap and fan out a credential-less job.
	/// This test builds the execution profile directly through
	/// <see cref="ICatalogRepository.CreateExecutionProfileAsync"/> WITHOUT calling
	/// AddCredentialRequirementAsync at all -- reproducing exactly the pre-#1012
	/// catalog state (zero requirement rows) regardless of how it got there.
	/// </summary>
	[Fact]
	public async Task CompileAsync_VmwareTransportProfileWithZeroCredentialRequirements_IsSkippedAtPreviewTime_NeverSilentlyAccepted()
	{
		Guid targetId = await SeedSiteAndTargetAsync();

		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync("source-nocred", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", "vsphere-nocred", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi-nocred", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Srg, "release-nocred", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync("group-nocred", "Test Group", 6, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(component.Id, release.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.Hdf, CancellationToken.None);
		// Deliberately NO AddCredentialRequirementAsync call -- reproduces the pre-#1012
		// importer-promoted state directly, independent of the promotion-path fix.
		await ActivateBaselineAsync(executionProfile.Id, "nocred", benchmarkRevisionId: null);
		Guid componentId = await SeedComponentLinkedToAsync(targetId, component.Id, "8.0.3", "host-nocred-9002");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.Empty(plan.Items);
		ScanPlanSkip skip = Assert.Single(plan.Skips);
		Assert.Equal(componentId, skip.ComponentId);
		Assert.Equal(ScanPlanSkipReasons.CredentialedTransportWithNoRequirement, skip.Reason);
		Assert.Contains(executionProfile.Id.ToString(), skip.Detail, StringComparison.Ordinal);
		Assert.False(plan.IsRunnable);
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
	public async Task CompileAsync_UnmappedStigBenchmark_PlansProfileOnlyWithBenchmarkMissingAnnotation()
	{
		// Issue #1021: a STIG execution profile (has a CatalogBenchmarkReference) whose
		// ACTIVE baseline was staged with no benchmark_revision_id used to be an
		// unmapped_benchmark SKIP -- a permanent planning dead-end per the issue. Per the
		// owner correction on #730 (2026-08-28) and the decided lifecycle in #1002,
		// execution requires only the approved profile baseline; the XCCDF is optional
		// CKL-metadata enrichment, never an execution gate. This now plans normally
		// (profile-only semantics: BenchmarkRevisionId stays null, matching an SRG item's
		// shape) with IsBenchmarkMissing surfacing the standing #1002 alert as a
		// non-blocking annotation instead of a skip.
		Guid targetId = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("unmapped", "8.0.3", withBenchmark: "vmware-esxi-8-stig-unmapped");
		await ActivateBaselineAsync(executionProfileId, "unmapped", benchmarkRevisionId: null);
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-4001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.True(plan.IsRunnable);
		Assert.Empty(plan.Skips);
		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Equal(executionProfileId, item.CatalogExecutionProfileId);
		Assert.Null(item.BenchmarkRevisionId);
		Assert.True(item.IsBenchmarkMissing);
	}

	/// <summary>
	/// Issue #1021's exact live-lab repro: an operator activates BOTH a runnable SRG
	/// baseline (priority 6) and a STIG baseline whose XCCDF is not yet mapped (lower/
	/// higher-precedence priority, so the pre-#1021 tiebreak picked it first and then
	/// skipped `unmapped_benchmark` immediately, never falling through to the coexisting
	/// SRG baseline). On main (pre-fix) this produced `is_runnable=false` with a single
	/// `unmapped_benchmark` skip and the SRG baseline never considered at all -- captured
	/// as the failing-test-first proof for this issue. Post-fix, the STIG profile is
	/// preferred by the SAME documented-priority tiebreak (issue #1012) but now RUNS
	/// profile-only instead of dead-ending: one accepted item, zero skips,
	/// IsBenchmarkMissing true, the coexisting SRG baseline correctly left unused (the
	/// tiebreak is harmless now that the higher-priority choice always executes).
	/// </summary>
	[Fact]
	public async Task CompileAsync_CoexistingSrgAndUnmappedStigBaselines_PlansStigProfileOnlyInsteadOfDeadEnding()
	{
		Guid targetId = await SeedSiteAndTargetAsync();

		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync("source-1021", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", "vsphere-1021", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi-1021", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);

		// Coexisting, perfectly runnable SRG baseline (priority 6, per
		// docs/compliance-parity.md -- every SRG report group sorts below every STIG one).
		CatalogContentRelease srgRelease = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Srg, "release-srg-1021", "Test SRG Release", CancellationToken.None);
		CatalogReportGroup srgGroup = await _catalog.UpsertReportGroupAsync("srg-1021", "SRG", 6, CancellationToken.None);
		CatalogExecutionProfile srgProfile = await _catalog.CreateExecutionProfileAsync(component.Id, srgRelease.Id, srgGroup.Id, "1.0.0", CatalogOutputKinds.Hdf, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(srgProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		await ActivateBaselineAsync(srgProfile.Id, "1021-srg", benchmarkRevisionId: null);

		// STIG baseline, XCCDF NOT yet mapped (the normal pre-#730 state) -- higher
		// precedence priority than SRG, so the tiebreak picks this one first.
		CatalogContentRelease stigRelease = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Stig, "release-stig-1021", "Test STIG Release", CancellationToken.None);
		CatalogReportGroup stigGroup = await _catalog.UpsertReportGroupAsync("esxi-stig-1021", "ESXi STIG", 4, CancellationToken.None);
		CatalogExecutionProfile stigProfile = await _catalog.CreateExecutionProfileAsync(component.Id, stigRelease.Id, stigGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(stigProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		await _catalog.SetBenchmarkReferenceAsync(stigProfile.Id, "vmware-esxi-8-1021-unmapped", "V1R1", CancellationToken.None);
		await ActivateBaselineAsync(stigProfile.Id, "1021-stig", benchmarkRevisionId: null);

		Guid componentId = await SeedComponentLinkedToAsync(targetId, component.Id, "8.0.3", "host-1021-9001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		// Failing-test-first proof (pre-fix, this asserted the opposite): the component
		// is RUNNABLE via the STIG profile, profile-only, with the gap surfaced as a
		// non-blocking annotation -- never a dead end, never falling back to the SRG
		// baseline (the tiebreak result is unchanged, only its consequence is).
		Assert.True(plan.IsRunnable);
		Assert.Empty(plan.Skips);
		ScanPlanItem item = Assert.Single(plan.Items);
		Assert.Equal(stigProfile.Id, item.CatalogExecutionProfileId);
		Assert.Equal(CatalogOutputKinds.HdfAndCkl, item.OutputKind);
		Assert.Null(item.BenchmarkRevisionId);
		Assert.True(item.IsBenchmarkMissing);
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
		// architecturally-skippable reasons (unsupported, no_active_baseline -- the closed
		// ScanPlanSkipReasons.All set after issue #1021 also retired unmapped_benchmark as
		// a producible reason) must ALL skip-and-continue, never fail the plan, alongside
		// healthy siblings that still plan (ADR-0023/0024 per-component isolation). One
		// candidate per skip reason, plus one good (SRG) component and one
		// unmapped-benchmark STIG component that now plans profile-only instead of
		// skipping (issue #1021/#1002).
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

		// Issue #1021: STIG profile, active baseline with no benchmark revision -- now an
		// ACCEPTED, profile-only item (IsBenchmarkMissing), never a skip.
		Guid unmappedProfileId = await SeedExecutionProfileAsync("legit-unmapped", "8.0.3", withBenchmark: "invented-legit-unmapped-stig");
		await ActivateBaselineAsync(unmappedProfileId, "legit-unmapped", benchmarkRevisionId: null);
		Guid unmappedCatalogComponentId = (await _catalog.GetExecutionProfileAsync(unmappedProfileId, CancellationToken.None))!.Component.Id;
		Guid unmappedComponent = await SeedComponentLinkedToAsync(targetId, unmappedCatalogComponentId, "8.0.3", "host-legit-unmapped");

		ScanPlan plan = await _planner.CompileAsync(
			null, [goodComponent, unsupportedComponent, noBaselineComponent, unmappedComponent], CancellationToken.None);

		// The plan compiled (did not throw): both healthy siblings planned (the SRG good
		// component and the profile-only unmapped-STIG component), every remaining
		// skippable reason produced a skip row and its siblings still proceeded.
		Assert.True(plan.IsRunnable);
		Assert.Equal(2, plan.Items.Count);
		Assert.Contains(plan.Items, i => i.ComponentId == goodComponent);
		ScanPlanItem unmappedItem = Assert.Single(plan.Items, i => i.ComponentId == unmappedComponent);
		Assert.True(unmappedItem.IsBenchmarkMissing);
		Assert.Null(unmappedItem.BenchmarkRevisionId);

		Assert.Equal(2, plan.Skips.Count);
		Assert.Contains(plan.Skips, s => s.ComponentId == unsupportedComponent && s.Reason == ScanPlanSkipReasons.Unsupported);
		Assert.Contains(plan.Skips, s => s.ComponentId == noBaselineComponent && s.Reason == ScanPlanSkipReasons.NoActiveBaseline);

		// Every skip reason emitted here is a member of the closed PRODUCIBLE set; none is
		// the integrity code (that path throws, proven separately above), and
		// unmapped_benchmark never appears (issue #1021 retired it as producible).
		Assert.All(plan.Skips, s => Assert.Contains(s.Reason, ScanPlanSkipReasons.All));
		Assert.DoesNotContain(plan.Skips, s => s.Reason == ScanPlanSkipReasons.UnmappedBenchmark);
	}

	private async Task<Waypoint.Core.ComplianceContent.Xccdf.BenchmarkRevision> ImportBenchmarkAsync(string benchmarkKey)
	{
		BenchmarkRepository benchmarks = new(_fixture.ConnectionString);
		Waypoint.Core.ComplianceContent.Xccdf.BenchmarkImportCandidate candidate = new(
			benchmarkKey, "Test Benchmark", "V1", "R1", $"digest-{benchmarkKey}", []);
		return await benchmarks.ImportRevisionAsync(candidate, Waypoint.Core.ComplianceContent.Xccdf.BenchmarkSources.ManualUpload, CancellationToken.None);
	}
}
