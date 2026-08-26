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
/// Issue #735 (epic #726 Wave 2, ADR-0024 "Control-granular settings and snapshots"),
/// against a real PostgreSQL 16 container: <see cref="ScanPlannerService"/>'s
/// planning-time Input/Attestation resolution, keyed to the plan item's stable
/// <c>CatalogExecutionProfileId</c> (migration 0060) rather than a fixed profile name.
/// Fixtures are INVENTED managed-object-reference-shaped identifiers only
/// (CLAUDE.md sanitization policy).
///
/// Covers the layering matrix (global-only, site-override, target-override),
/// missing-vs-resolved state, expired-attestation provenance (falls through, reports
/// the lapsed layer), and that a resolved-config change alone moves the plan digest
/// (issue #734 AC-4's determinism contract extended to the config layer by this issue).
/// </summary>
[Collection("Postgres")]
public sealed class PlanConfigResolutionServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ScanPlannerService _planner = null!;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private TargetRepository _targets = null!;
	private SiteRepository _sites = null!;
	private ConfigDocRepository _configDocs = null!;

	public PlanConfigResolutionServiceTests(PostgresFixture fixture)
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

	private async Task<(Guid TargetId, Guid SiteId)> SeedSiteAndTargetAsync()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return (targetId!.Value, siteId);
	}

	private async Task<Guid> SeedComponentLinkedToAsync(Guid targetId, Guid catalogComponentId, string exactVersion, string vendorIdentity)
	{
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"host-{vendorIdentity}", null, catalogComponentId, exactVersion)], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	private async Task<Guid> SeedExecutionProfileAsync(string suffix, string exactVersion)
	{
		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", $"vsphere-{suffix}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, exactVersion, exactVersion, CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"esxi-{suffix}", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null),
			CancellationToken.None);
		CatalogContentRelease contentRelease = await _catalog.UpsertContentReleaseAsync(
			sourceRevision.Id, CatalogKinds.Srg, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 2, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _catalog.AddCredentialRequirementAsync(executionProfile.Id, "vsphere-api", isRequired: true, CancellationToken.None);
		await _catalog.UpsertDeclaredInputAsync(executionProfile.Id, "target_ip", "string", isRequired: true, CancellationToken.None);
		return executionProfile.Id;
	}

	private async Task ActivateBaselineAsync(Guid executionProfileId, string suffix)
	{
		Waypoint.Core.ComplianceContent.ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", $"digest-{suffix}", $"revisions/{suffix}", CancellationToken.None);
		Waypoint.Core.ComplianceContent.Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);
		Waypoint.Core.ComplianceContent.BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "admin", CancellationToken.None);
		Assert.Equal(Waypoint.Core.ComplianceContent.BaselineActivationOutcome.Activated, outcome);
	}

	private async Task SaveConfigDocAsync(string kind, Guid catalogExecutionProfileId, string layerType, Guid? layerRef, string bodyYaml)
	{
		(ConfigDocSaveOutcome outcome, ConfigDoc? doc, _) = await _configDocs.SaveAsync(
			Guid.NewGuid(), kind, $"unused-profile-name-{Guid.NewGuid():N}", layerType, layerRef, "admin", bodyYaml, CancellationToken.None,
			catalogExecutionProfileId);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome);
		Assert.NotNull(doc);
	}

	[Fact]
	public async Task CompileAsync_GlobalInputOnly_ResolvesFromGlobalForEveryDeclaredInput()
	{
		(Guid targetId, Guid siteId) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("global-only", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "global-only");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-1001");

		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Global, null, "target_ip: 192.0.2.10\n");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		ScanPlanItem item = Assert.Single(plan.Items);
		PlanInputResolution input = Assert.Single(item.InputResolutionsOrEmpty);
		Assert.Equal("target_ip", input.InputName);
		Assert.Equal(ConfigResolutionStates.Resolved, input.State);
		Assert.Equal(ConfigDocLayers.Global, input.Layer);
		Assert.NotNull(input.DocId);
		Assert.Equal(1, input.DocVersion);
	}

	[Fact]
	public async Task CompileAsync_SiteOverride_WinsOverGlobalForInput()
	{
		(Guid targetId, Guid siteId) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("site-override", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "site-override");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-2001");

		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Global, null, "target_ip: 192.0.2.10\n");
		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Site, siteId, "target_ip: 192.0.2.20\n");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		PlanInputResolution input = Assert.Single(Assert.Single(plan.Items).InputResolutionsOrEmpty);
		Assert.Equal($"{ConfigDocLayers.Site}:{siteId}", input.Layer);
	}

	[Fact]
	public async Task CompileAsync_TargetOverride_WinsOverSiteAndGlobalForInput()
	{
		(Guid targetId, Guid siteId) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("target-override", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "target-override");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-3001");

		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Global, null, "target_ip: 192.0.2.10\n");
		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Site, siteId, "target_ip: 192.0.2.20\n");
		// The doc is keyed to the CONTROL/COMPONENT (componentId), which is what
		// ScanPlannerService actually resolves against for the Target layer -- see
		// PlanConfigResolutionService.ResolveAsync's targetId parameter.
		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Target, componentId, "target_ip: 192.0.2.30\n");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		PlanInputResolution input = Assert.Single(Assert.Single(plan.Items).InputResolutionsOrEmpty);
		Assert.Equal($"{ConfigDocLayers.Target}:{componentId}", input.Layer);
	}

	[Fact]
	public async Task CompileAsync_NoInputDocAnywhere_ReportsMissingNotResolved()
	{
		(Guid targetId, _) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("missing-input", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "missing-input");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-4001");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		PlanInputResolution input = Assert.Single(Assert.Single(plan.Items).InputResolutionsOrEmpty);
		Assert.Equal(ConfigResolutionStates.Missing, input.State);
		Assert.Null(input.Layer);
		Assert.Null(input.DocId);
	}

	[Fact]
	public async Task CompileAsync_AttestationKeyedToProfile_ResolvesIndependentlyPerProfile()
	{
		// The AC this test pins: attestations resolve by the SELECTED profile/component,
		// not one fixed application-wide name -- two different execution profiles each
		// get their own attestation doc, and each plan item resolves only its own.
		(Guid targetId, _) = await SeedSiteAndTargetAsync();
		Guid profileA = await SeedExecutionProfileAsync("attest-a", "8.0.3");
		await ActivateBaselineAsync(profileA, "attest-a");
		Guid catalogComponentA = (await _catalog.GetExecutionProfileAsync(profileA, CancellationToken.None))!.Component.Id;
		Guid componentA = await SeedComponentLinkedToAsync(targetId, catalogComponentA, "8.0.3", "host-cfg-5001");

		Guid profileB = await SeedExecutionProfileAsync("attest-b", "8.0.3");
		await ActivateBaselineAsync(profileB, "attest-b");
		Guid catalogComponentB = (await _catalog.GetExecutionProfileAsync(profileB, CancellationToken.None))!.Component.Id;
		Guid componentB = await SeedComponentLinkedToAsync(targetId, catalogComponentB, "8.0.3", "host-cfg-5002");

		await SaveConfigDocAsync(ConfigDocKinds.Attestation, profileA, ConfigDocLayers.Global, null, "status: Not_A_Finding\njustification: waived for A\n");

		ScanPlan plan = await _planner.CompileAsync(null, [componentA, componentB], CancellationToken.None);

		ScanPlanItem itemA = plan.Items.Single(i => i.ComponentId == componentA);
		ScanPlanItem itemB = plan.Items.Single(i => i.ComponentId == componentB);

		Assert.NotNull(itemA.AttestationResolution);
		Assert.Equal(ConfigResolutionStates.Resolved, itemA.AttestationResolution!.State);
		Assert.True(itemA.AttestationResolution.Applied);

		Assert.NotNull(itemB.AttestationResolution);
		Assert.Equal(ConfigResolutionStates.Missing, itemB.AttestationResolution!.State);
		Assert.False(itemB.AttestationResolution.Applied);
	}

	[Fact]
	public async Task CompileAsync_ExpiredAttestation_ReportsExpiredStateAndExpiryProvenance_NeverApplied()
	{
		(Guid targetId, _) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("attest-expired", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "attest-expired");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-6001");

		await SaveConfigDocAsync(
			ConfigDocKinds.Attestation, executionProfileId, ConfigDocLayers.Global, null,
			"status: Not_A_Finding\njustification: waived but lapsed\nexpires: 2000-01-01\n");

		ScanPlan plan = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		PlanAttestationResolution attestation = Assert.Single(plan.Items).AttestationResolution!;
		Assert.Equal(ConfigResolutionStates.Expired, attestation.State);
		Assert.False(attestation.Applied);
		Assert.True(attestation.Expired);
		Assert.NotNull(attestation.ExpiresAt);
	}

	[Fact]
	public async Task CompileAsync_ResolvedConfigChangeAlone_ChangesThePlanDigest()
	{
		// Issue #734 AC-4's determinism contract extended to the config-resolution layer
		// by this issue: identical catalog/baseline state, but a DIFFERENT resolved
		// Input document, must produce a DIFFERENT digest -- otherwise two materially
		// different runs would be indistinguishable by their recorded digest.
		(Guid targetId, _) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("digest-shift", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "digest-shift");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-7001");

		ScanPlan before = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Global, null, "target_ip: 192.0.2.40\n");

		ScanPlan after = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.NotEqual(before.PlanDigest, after.PlanDigest);
	}

	[Fact]
	public async Task CompileAsync_SamePlanRecompiledTwice_IsDeterministic_IncludingConfigResolution()
	{
		(Guid targetId, Guid siteId) = await SeedSiteAndTargetAsync();
		Guid executionProfileId = await SeedExecutionProfileAsync("digest-stable", "8.0.3");
		await ActivateBaselineAsync(executionProfileId, "digest-stable");
		Guid catalogComponentId = (await _catalog.GetExecutionProfileAsync(executionProfileId, CancellationToken.None))!.Component.Id;
		Guid componentId = await SeedComponentLinkedToAsync(targetId, catalogComponentId, "8.0.3", "host-cfg-8001");
		await SaveConfigDocAsync(ConfigDocKinds.Input, executionProfileId, ConfigDocLayers.Site, siteId, "target_ip: 192.0.2.50\n");

		ScanPlan first = await _planner.CompileAsync(null, [componentId], CancellationToken.None);
		ScanPlan second = await _planner.CompileAsync(null, [componentId], CancellationToken.None);

		Assert.Equal(first.PlanDigest, second.PlanDigest);
	}
}
