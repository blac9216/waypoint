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
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Components;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
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
/// Issue #741 (epic #726, ADR-0023 "For a catalog-declared service with no independent
/// upstream object, parent identity plus catalog component key is authoritative"),
/// against a real PostgreSQL 16 container: the catalog release's declared VCSA service
/// set materializes as inventory child components beneath a linked root connection
/// component, survives discovery's absence sweep (a boundary structurally never
/// enumerates them), inherits the appliance's version facts at scope-resolution time,
/// and plans as ssh/service items carrying the derived <c>vcsa-ssh</c> +
/// <c>vsphere-api</c> requirements. These tests pin the exact gap epic #726's round-10
/// live validation named for this issue ("VCSA service components ... are not surfaced
/// as discoverable scan targets"): every positive assertion here fails on pre-#741 main
/// because nothing materialized the children at all. Fixtures are INVENTED catalog
/// shapes and RFC-5737-style names only (CLAUDE.md sanitization policy).
/// </summary>
[Collection("Postgres")]
public sealed class CatalogDeclaredServiceExpansionTests : IAsyncLifetime
{
	private const string ApplianceExactVersion = "8.0.3";
	private const string DeclaredScopeVersionKey = "8.0";

	private readonly PostgresFixture _fixture;
	private ComponentRepository _components = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private ScopeResolutionService _resolution = null!;
	private ScanPlannerService _planner = null!;

	private Guid _vcenterCatalogComponentId;
	private Guid _eamCatalogComponentId;
	private Guid _postgresqlCatalogComponentId;

	public CatalogDeclaredServiceExpansionTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_components = new ComponentRepository(_fixture.ConnectionString, _catalog);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);
		_resolution = new ScopeResolutionService(_targets, _components, _catalog);
		_planner = new ScanPlannerService(
			_components, _catalog, _baselines, _targets,
			new PlanConfigResolutionService(new ConfigDocRepository(_fixture.ConnectionString)));

		await SeedCatalogAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE run_scope_snapshots, component_observations, components, targets, sites, " +
			"baselines, content_revisions, config_versions, config_docs, " +
			"catalog_credential_requirements, catalog_execution_profiles, catalog_report_groups, catalog_content_releases, " +
			"catalog_components, catalog_product_versions, catalog_products, catalog_source_revisions RESTART IDENTITY CASCADE",
			connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// One invented vSphere catalog product version (declared scope "8.0") shaped like
	/// docs/compliance-parity.md's vSphere 8.0 rows: the vmware object-kind components
	/// (vcenter, esxi) AND the ssh/service VCSA named services (eam, postgresql), plus
	/// one nsx-api/service and one ssh/target row that must NEVER be selected as VCSA
	/// service children. The two services get active SRG baselines and the doc-derived
	/// vsphere-api + vcsa-ssh requirements so plan compilation can succeed end to end.
	/// </summary>
	private async Task SeedCatalogAsync()
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _catalog.UpsertProductVersionAsync(product.Id, DeclaredScopeVersionKey, DeclaredScopeVersionKey, CancellationToken.None);

		CatalogComponent vcenter = await _catalog.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		await _catalog.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("esxi", "ESXi", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogComponent eam = await _catalog.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("eam", "VCSA EAM", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "eam", null), CancellationToken.None);
		CatalogComponent postgresql = await _catalog.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("postgresql", "VCSA PostgreSQL", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "postgresql", null), CancellationToken.None);
		await _catalog.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("manager", "NSX Manager", CatalogTransports.NsxApi, CatalogSelectorKinds.Service, "manager", null), CancellationToken.None);
		await _catalog.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("photon", "Photon OS", CatalogTransports.Ssh, CatalogSelectorKinds.Target, null, null), CancellationToken.None);

		_vcenterCatalogComponentId = vcenter.Id;
		_eamCatalogComponentId = eam.Id;
		_postgresqlCatalogComponentId = postgresql.Id;

		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "VCSA SRG", 6, CancellationToken.None);

		foreach (CatalogComponent service in new[] { eam, postgresql })
		{
			CatalogExecutionProfile profile = await _catalog.CreateExecutionProfileAsync(
				service.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.Hdf, CancellationToken.None);
			await _catalog.AddCredentialRequirementAsync(profile.Id, CredentialPurposes.VSphereApi, isRequired: true, CancellationToken.None);
			await _catalog.AddCredentialRequirementAsync(profile.Id, CredentialPurposes.VcsaSsh, isRequired: true, CancellationToken.None);

			ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
				$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);
			Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, profile.Id, benchmarkRevisionId: null, CancellationToken.None);
			await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);
		}
	}

	private async Task<(Guid TargetId, Guid RootComponentId)> SeedTargetWithRootAsync()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"vcsa-{Guid.NewGuid():N}.example.internal", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		// The synthetic root connection component exactly as DiscoverJobHandler.MapToComponents
		// produces it: no vendor identity, no parent, no discovered version.
		await _components.UpsertDiscoveredAsync(
			targetId!.Value,
			[new DiscoveredComponent("vcenter", null, "vCenter Server", null, null, null)],
			CancellationToken.None);
		Component root = Assert.Single(await _components.ListForTargetAsync(targetId.Value, includeRetired: true, CancellationToken.None));
		return (targetId.Value, root.Id);
	}

	private async Task<IReadOnlyList<Component>> ListChildrenAsync(Guid targetId, Guid rootComponentId) =>
		[.. (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Where(c => c.ParentComponentId == rootComponentId)
			.OrderBy(c => c.CatalogComponentKey, StringComparer.Ordinal)];

	// -- materialization via the configured-fact write (the live-lab linkage path) ---

	[Fact]
	public async Task SetConfiguredFact_OnRoot_MaterializesDeclaredServiceChildren()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();

		ComponentWriteOutcome outcome = await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		Assert.Equal(ComponentWriteOutcome.Ok, outcome);

		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);
		Assert.Equal(2, children.Count);

		Component eamChild = children[0];
		Assert.Equal("eam", eamChild.CatalogComponentKey);
		Assert.Equal(_eamCatalogComponentId, eamChild.CatalogComponentId);
		Assert.Null(eamChild.VendorIdentity);
		Assert.Equal(ComponentLifecycleStates.Active, eamChild.Lifecycle);
		Assert.Equal("VCSA EAM", eamChild.DisplayName);

		// No fact of its own -- version identity is inherited from the appliance root
		// at evaluation time, never persisted as a third copy.
		Assert.Null(eamChild.ConfiguredFact);
		Assert.Null(eamChild.DiscoveredFact);

		Component postgresChild = children[1];
		Assert.Equal("postgresql", postgresChild.CatalogComponentKey);
		Assert.Equal(_postgresqlCatalogComponentId, postgresChild.CatalogComponentId);

		// The nsx-api service and the ssh/target whole-appliance rows were in the same
		// product version but must never materialize as VCSA service children.
		Assert.DoesNotContain(children, c => c.CatalogComponentKey is "manager" or "photon" or "esxi");
	}

	/// <summary>
	/// Issue #1081 regression pin: before this issue, the vcenter root's vendor
	/// identity was ALWAYS null, so <see cref="ComponentRepository.SetConfiguredFactAsync"/>
	/// could identify "this is the root" purely from a null vendor_identity. Once
	/// discovery gives the root a real vendor identity (the appliance's instance
	/// UUID), that test alone would wrongly say "not the root" and silently stop
	/// triggering declared-service sync on an Admin's configured-fact PUT to an
	/// already-discovered vCenter. This seeds the root WITH a vendor identity (as a
	/// post-#1081 discovery pass would leave it) and proves the PUT path still
	/// materializes the declared children.
	/// </summary>
	[Fact]
	public async Task SetConfiguredFact_OnDiscoveredRootWithVendorIdentity_StillMaterializesDeclaredServiceChildren()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"vcsa-{Guid.NewGuid():N}.example.internal", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		await _components.UpsertDiscoveredAsync(
			targetId!.Value,
			[new DiscoveredComponent("vcenter", "vcenter-instance-discovered-abc", "vCenter Server", null, null, null)],
			CancellationToken.None);
		Component root = Assert.Single(await _components.ListForTargetAsync(targetId.Value, includeRetired: true, CancellationToken.None));
		Assert.Equal("vcenter-instance-discovered-abc", root.VendorIdentity); // sanity: this fixture models a real discovered identity, not the old null-identity shape.

		ComponentWriteOutcome writeOutcome = await _components.SetConfiguredFactAsync(root.Id, ApplianceExactVersion, CancellationToken.None);
		Assert.Equal(ComponentWriteOutcome.Ok, writeOutcome);

		IReadOnlyList<Component> children = await ListChildrenAsync(targetId.Value, root.Id);
		Assert.Equal(2, children.Count);
		Assert.Contains(children, c => c.CatalogComponentKey == "eam");
		Assert.Contains(children, c => c.CatalogComponentKey == "postgresql");
	}

	[Fact]
	public async Task SetConfiguredFact_Cleared_MarksDeclaredChildrenAbsent()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		Assert.Equal(2, (await ListChildrenAsync(targetId, rootId)).Count);

		// Clearing the configured version unlinks the root (#1000) -- the declared set
		// becomes empty and every derived child goes honestly absent, never deleted.
		await _components.SetConfiguredFactAsync(rootId, null, CancellationToken.None);

		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);
		Assert.Equal(2, children.Count);
		Assert.All(children, c => Assert.Equal(ComponentLifecycleStates.Absent, c.Lifecycle));
		Assert.All(children, c => Assert.NotNull(c.ContinuousAbsenceSince));

		// Re-setting the version reconnects the same rows rather than creating siblings.
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		IReadOnlyList<Component> reconnected = await ListChildrenAsync(targetId, rootId);
		Assert.Equal(2, reconnected.Count);
		Assert.All(reconnected, c => Assert.Equal(ComponentLifecycleStates.Active, c.Lifecycle));
		Assert.Equal(children.Select(c => c.Id).Order(), reconnected.Select(c => c.Id).Order());
	}

	[Fact]
	public async Task SetConfiguredFact_OnNonRootComponent_NeverExpandsChildren()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();

		// An esxi host (vendor identity present) is never an expansion anchor even
		// when its own configured version links it to the same product version.
		await _components.UpsertDiscoveredAsync(
			targetId,
			[
				new DiscoveredComponent("vcenter", null, "vCenter Server", null, null, null),
				new DiscoveredComponent("esxi", "host-2001", "esxi-01.example.internal", null, null, null),
			],
			CancellationToken.None);
		Component host = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-2001");

		await _components.SetConfiguredFactAsync(host.Id, ApplianceExactVersion, CancellationToken.None);

		Assert.Empty(await ListChildrenAsync(targetId, rootId));
		Assert.Empty((await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Where(c => c.ParentComponentId == host.Id));
	}

	// -- discovery interaction ------------------------------------------------------

	[Fact]
	public async Task UpsertDiscovered_AbsenceSweep_NeverMarksDeclaredChildrenAbsent()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		Assert.Equal(2, (await ListChildrenAsync(targetId, rootId)).Count);

		// A later complete discovery pass reports only what it enumerated (the root and
		// one host) -- catalog-declared children are structurally never enumerated, so
		// the sweep must not read "not reported" as absence for them.
		ComponentUpsertOutcome outcome = await _components.UpsertDiscoveredAsync(
			targetId,
			[
				new DiscoveredComponent("vcenter", null, "vCenter Server", null, null, null),
				new DiscoveredComponent("esxi", "host-3001", "esxi-02.example.internal", null, null, ApplianceExactVersion),
			],
			CancellationToken.None);
		Assert.Equal(0, outcome.MarkedAbsent);

		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);
		Assert.Equal(2, children.Count);
		Assert.All(children, c => Assert.Equal(ComponentLifecycleStates.Active, c.Lifecycle));
	}

	// -- direct reconciliation protocol ---------------------------------------------

	[Fact]
	public async Task SyncCatalogDeclaredChildren_NarrowedDeclaredSet_MarksOnlyMissingChildAbsent()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);

		// The catalog stops declaring postgresql for this release: only that child
		// goes absent; its sibling stays active (one component's gap never halts
		// siblings, epic #726 invariant).
		CatalogDeclaredChildSyncOutcome outcome = await _components.SyncCatalogDeclaredChildrenAsync(
			targetId, rootId,
			[new CatalogDeclaredChild(_eamCatalogComponentId, "eam", "VCSA EAM")],
			CancellationToken.None);
		Assert.Equal(1, outcome.MarkedAbsent);

		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);
		Assert.Equal(ComponentLifecycleStates.Active, children.Single(c => c.CatalogComponentKey == "eam").Lifecycle);
		Assert.Equal(ComponentLifecycleStates.Absent, children.Single(c => c.CatalogComponentKey == "postgresql").Lifecycle);

		// Re-declaring reconnects the absent row (same identity, never a sibling).
		CatalogDeclaredChildSyncOutcome redeclared = await _components.SyncCatalogDeclaredChildrenAsync(
			targetId, rootId,
			[
				new CatalogDeclaredChild(_eamCatalogComponentId, "eam", "VCSA EAM"),
				new CatalogDeclaredChild(_postgresqlCatalogComponentId, "postgresql", "VCSA PostgreSQL"),
			],
			CancellationToken.None);
		Assert.Equal(1, redeclared.Reconnected);
		Assert.Equal(0, redeclared.MarkedAbsent);
	}

	// -- scope resolution + planning (the "discoverable, plannable" surface) --------

	[Fact]
	public async Task ScopeResolution_AllMode_ResolvesDeclaredChildrenWithInheritedFacts()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		Target root = (await _targets.GetAsync(targetId, CancellationToken.None))!;
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			root.SiteId, new TargetScopeRequest(TargetScopeModes.All, [targetId], null), CancellationToken.None);

		// Both declared children resolve as catalog-compatible even though they carry
		// no version fact of their own -- the appliance root's configured fact is
		// inherited at match time. (The root itself has no execution profile in this
		// fixture, so it is an honest omission, not a resolved component.)
		Assert.All(children, c => Assert.Contains(c.Id, resolved.ResolvedComponentIds));
		Assert.DoesNotContain(resolved.Omissions, o => children.Any(c => c.Id == o.ComponentId));
	}

	[Fact]
	public async Task Planner_CompilesDeclaredChildren_AsSshServiceItemsRequiringVcsaSsh()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);

		ScanPlan plan = await _planner.CompileAsync(null, [.. children.Select(c => c.Id)], CancellationToken.None);

		Assert.Empty(plan.Skips);
		Assert.Equal(2, plan.Items.Count);
		Assert.All(plan.Items, item =>
		{
			Assert.Equal(CatalogTransports.Ssh, item.Transport);
			Assert.Equal(CatalogSelectorKinds.Service, item.SelectorKind);
			Assert.Equal(CatalogOutputKinds.Hdf, item.OutputKind);
			Assert.Contains(CredentialPurposes.VcsaSsh, item.RequiredPurposes);
			Assert.Contains(CredentialPurposes.VSphereApi, item.RequiredPurposes);
			Assert.NotNull(item.SelectorName);
		});

		// Each service is its own leaf item with its own frozen profile/baseline
		// identity -- never one collapsed appliance scan (issue #741 AC).
		Assert.Equal(2, plan.Items.Select(i => i.CatalogExecutionProfileId).Distinct().Count());
		Assert.Equal(2, plan.Items.Select(i => i.BaselineId).Distinct().Count());
		Assert.Equal(["eam", "postgresql"], plan.Items.Select(i => i.SelectorName).Order().ToArray());
	}

	[Fact]
	public async Task ScopeResolution_ConflictedApplianceFact_OmitsDeclaredChildrenHonestly()
	{
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		Target root = (await _targets.GetAsync(targetId, CancellationToken.None))!;
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);
		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);

		// A discovered fact now disagrees with the configured one: the appliance root
		// conflicts, and every service derived from it must conflict too -- inherited,
		// never silently runnable against an ambiguous appliance version.
		await _components.UpsertDiscoveredAsync(
			targetId,
			[new DiscoveredComponent("vcenter", null, "vCenter Server", null, null, "9.0.1")],
			CancellationToken.None, advanceAbsence: false);

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			root.SiteId, new TargetScopeRequest(TargetScopeModes.All, [targetId], null), CancellationToken.None);

		Assert.All(children, c => Assert.DoesNotContain(c.Id, resolved.ResolvedComponentIds));
		foreach (Component child in children)
		{
			ScopeOmission omission = Assert.Single(resolved.Omissions, o => o.ComponentId == child.Id);
			Assert.Equal(ScopeOmissionReasons.FactConflict, omission.Reason);
		}
	}

	/// <summary>
	/// Issue #1064 end to end: content imported from the top-level `vcsa/` vendor tree
	/// promotes INTO the seeded vSphere product version (owner decision: VCSA services
	/// are implied subcomponents of the vCenter appliance, never a separate `vcsa`
	/// product), carries the doc-derived vsphere-api + vcsa-ssh requirements at
	/// promotion time (the issue's empty-requirement half), and -- because it now lives
	/// on the linked product version -- materializes as a declared service child under
	/// a vSphere-linked root exactly like a seeded service (#741's expansion, the
	/// issue's invisible-to-expansion half). Every path and manifest is INVENTED.
	/// </summary>
	[Fact]
	public async Task ImporterPromotedVcsaService_JoinsVsphereProduct_AndMaterializesAsDeclaredChild()
	{
		VendorContentEntry lookup = new(
			$"vcsa/{DeclaredScopeVersionKey}/v2r3-stig/inspec/invented-vcsa-stig-baseline/lookup",
			"name: lookup\ntitle: VCSA Lookup Service STIG\nversion: 2.3.0\n",
			HasControlsDirectory: true,
			HasFilesDirectory: false,
			ControlFileNames: ["lookup-000001.rb"]);

		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret([lookup]);
		Assert.Empty(interpretation.Rejections);
		SemanticCandidate candidate = Assert.Single(interpretation.Candidates);
		Assert.Equal("vsphere", candidate.VendorFamily);

		SemanticImportReport report = SemanticImportReconciler.Reconcile("invented-commit-1064", interpretation, [lookup]);
		Assert.Empty(report.Rejected);
		SemanticImportAccepted accepted = Assert.Single(report.Accepted);

		CatalogPromotionOutcome outcome = await _catalog.PromoteCandidateAsync(
			accepted.Candidate,
			new CatalogPromotionRequest(
				SourceRevisionKey: "compliance-content",
				Vendor: CatalogVendors.VMware,
				ProductDisplayName: "VMware vSphere",
				ProductVersionDisplayName: DeclaredScopeVersionKey,
				ContentReleaseDisplayName: $"stig {DeclaredScopeVersionKey}",
				ReportGroupKey: "vcsa-stig",
				ReportGroupDisplayName: "VCSA STIG",
				ReportGroupPriority: 2,
				OutputKind: CatalogOutputKinds.HdfAndCkl),
			CancellationToken.None);
		Assert.Null(outcome.RejectionReason);
		Assert.NotNull(outcome.ExecutionProfileId);

		// Promotion-time credential derivation (the issue's first half): the imported
		// service carries the vSphere ssh/service row's documented purposes, not an
		// empty fail-closed set.
		CatalogExecutionProfileDetail? detail = await _catalog.GetExecutionProfileAsync(outcome.ExecutionProfileId!.Value, CancellationToken.None);
		Assert.NotNull(detail);
		Assert.Equal("vsphere", detail!.Product.ProductKey);
		Assert.Equal(DeclaredScopeVersionKey, detail.ProductVersion.VersionKey);
		Assert.Equal(2, detail.CredentialRequirements.Count);
		Assert.Contains(detail.CredentialRequirements, r => r.Purpose == CredentialPurposes.VSphereApi);
		Assert.Contains(detail.CredentialRequirements, r => r.Purpose == CredentialPurposes.VcsaSsh);

		// Expansion visibility (the issue's second half): the imported service now
		// materializes beneath a linked appliance root alongside the seeded ones.
		(Guid targetId, Guid rootId) = await SeedTargetWithRootAsync();
		await _components.SetConfiguredFactAsync(rootId, ApplianceExactVersion, CancellationToken.None);

		IReadOnlyList<Component> children = await ListChildrenAsync(targetId, rootId);
		Assert.Equal(["eam", "lookup", "postgresql"], children.Select(c => c.CatalogComponentKey).ToArray());
	}
}
