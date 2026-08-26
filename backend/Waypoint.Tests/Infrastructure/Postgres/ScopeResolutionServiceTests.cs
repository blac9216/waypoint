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
using Waypoint.Core.Jobs;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #733 (epic #726 Wave 2, ADR-0023 §3), against a real PostgreSQL 16 container:
/// resolves a scan run's tri-state <see cref="TargetScopeRequest"/> into an explicit,
/// deterministic stable-component set, joined against the merged component model (PR
/// #839, migration 0054) rather than raw inventory. Fixtures are INVENTED
/// managed-object-reference-shaped identifiers only (CLAUDE.md sanitization policy).
///
/// Covers issue #733's own AC list directly: parent tri-state resolution (all vs.
/// explicit) is deterministic; stale/removed selections fail as explicit omissions
/// rather than silently widening; an empty explicit selection never falls back to the
/// whole site; ownership/lifecycle/catalog-compatibility validation.
/// </summary>
[Collection("Postgres")]
public sealed class ScopeResolutionServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ScopeResolutionService _resolution = null!;
	private ComponentRepository _components = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private CatalogRepository _catalog = null!;

	/// <summary>A catalog component id whose linked execution profile targets exactly <see cref="CompatibleExactVersion"/> -- see <see cref="SeedCompatibleCatalogComponentAsync"/>.</summary>
	private Guid _compatibleCatalogComponentId;

	private const string CompatibleExactVersion = "8.0.3";

	public ScopeResolutionServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_components = new ComponentRepository(_fixture.ConnectionString);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_resolution = new ScopeResolutionService(_targets, _components, _catalog);

		_compatibleCatalogComponentId = await SeedCompatibleCatalogComponentAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE run_scope_snapshots, component_observations, components, targets, sites, " +
			"catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components, " +
			"catalog_product_versions, catalog_products, catalog_source_revisions RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Seeds one complete catalog chain (source revision -> product -> product version
	/// "8.0.3" -> catalog component -> content release -> report group -> execution
	/// profile) so a component's <see cref="ComponentCapabilityMatcher"/> evaluation can
	/// actually succeed -- the positive-path tests below link a discovered component to
	/// this id and give it the matching exact version.
	/// </summary>
	private async Task<Guid> SeedCompatibleCatalogComponentAsync()
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, CompatibleExactVersion, CompatibleExactVersion, CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Stig, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		await _catalog.CreateExecutionProfileAsync(catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		return catalogComponent.Id;
	}

	private async Task<Guid> SeedSiteAsync() => (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;

	private async Task<Guid> SeedTargetAsync(Guid siteId, string name)
	{
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, name, "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return targetId!.Value;
	}

	/// <summary>Seeds a component with NO catalog link -- always resolves as <see cref="ScopeOmissionReasons.CatalogIncompatible"/>, the fail-closed default this repository's discovery pipeline produces before a real matcher ever runs.</summary>
	private async Task<Guid> SeedComponentAsync(Guid targetId, string vendorIdentity, string? exactVersion = "8.0.3")
	{
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"host-{vendorIdentity}", null, null, exactVersion)], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	/// <summary>
	/// Seeds one or more sibling components under the same target, all linked to
	/// <see cref="_compatibleCatalogComponentId"/> at the matching exact version --
	/// resolves as catalog-compatible (runnable). Deliberately a SINGLE
	/// <see cref="IComponentRepository.UpsertDiscoveredAsync"/> call for every sibling
	/// under one target: that method marks any not-reported existing component under
	/// the target absent (ADR-0023's own "successful boundary that didn't observe it"
	/// rule) -- calling it once per sibling would therefore make each call retroactively
	/// mark the PRIOR sibling absent, which is not what these tests are seeding.
	/// </summary>
	private async Task<IReadOnlyList<Guid>> SeedCompatibleComponentsAsync(Guid targetId, params string[] vendorIdentities)
	{
		DiscoveredComponent[] items = [.. vendorIdentities.Select(v =>
			new DiscoveredComponent("esxi", v, $"host-{v}", null, _compatibleCatalogComponentId, CompatibleExactVersion))];
		await _components.UpsertDiscoveredAsync(targetId, items, CancellationToken.None);

		IReadOnlyList<Component> all = await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		return [.. vendorIdentities.Select(v => all.Single(c => c.VendorIdentity == v).Id)];
	}

	// -- explicit mode -------------------------------------------------------------

	[Fact]
	public async Task ResolveAsync_ExplicitEmptyList_ResolvesToZeroComponentsWithNoOmissions()
	{
		// Issue #733 AC "No scan silently falls back from an empty explicit selection
		// to the whole site": an explicit empty list must resolve to an intentional
		// empty plan, never every component under the site.
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		await SeedComponentAsync(targetId, "host-1001");

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, []), CancellationToken.None);

		Assert.Equal(TargetScopeModes.Explicit, resolved.Mode);
		Assert.Empty(resolved.ResolvedComponentIds);
		Assert.Empty(resolved.Omissions);
		Assert.False(resolved.HasAnyResolvedComponent);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitOneOfTwoComponents_SelectsExactlyThatComponent()
	{
		// Issue #733 AC "Selecting or excluding one ESXi host/VM changes the persisted
		// run scope" -- an explicit subset must never widen to the sibling component.
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		IReadOnlyList<Guid> seeded = await SeedCompatibleComponentsAsync(targetId, "host-1001", "host-1002");
		Guid included = seeded[0];

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [included]), CancellationToken.None);

		Guid onlyResolved = Assert.Single(resolved.ResolvedComponentIds);
		Assert.Equal(included, onlyResolved);
		Assert.Empty(resolved.Omissions);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitUnknownComponentId_IsAnOmissionNotAnException()
	{
		Guid siteId = await SeedSiteAsync();
		Guid bogusId = Guid.NewGuid();

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [bogusId]), CancellationToken.None);

		Assert.Empty(resolved.ResolvedComponentIds);
		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(ScopeOmissionReasons.ComponentNotFound, omission.Reason);
		Assert.Equal(bogusId, omission.ComponentId);
		Assert.Contains("refresh", omission.Detail, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitComponentFromAnotherSite_IsOutOfScopeOmission()
	{
		// A cross-site component id must never silently resolve -- ownership check.
		Guid siteA = await SeedSiteAsync();
		Guid siteB = await SeedSiteAsync();
		Guid targetB = await SeedTargetAsync(siteB, "vcenter-b");
		Guid componentInSiteB = await SeedComponentAsync(targetB, "host-2001");

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteA, new TargetScopeRequest(TargetScopeModes.Explicit, null, [componentInSiteB]), CancellationToken.None);

		Assert.Empty(resolved.ResolvedComponentIds);
		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(ScopeOmissionReasons.ComponentNotInScope, omission.Reason);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitRetiredComponent_IsAnOmission()
	{
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		Guid componentId = await SeedComponentAsync(targetId, "host-1001");

		// Force retirement directly (RetireContinuouslyAbsentAsync requires a real
		// continuous_absence_since window; a direct SQL flip is the simplest way to
		// reach the terminal lifecycle state for this test).
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand update = new("UPDATE components SET lifecycle = 'retired', retired_at = now() WHERE id = $1", connection);
			update.Parameters.AddWithValue(componentId);
			await update.ExecuteNonQueryAsync();
		}

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [componentId]), CancellationToken.None);

		Assert.Empty(resolved.ResolvedComponentIds);
		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(ScopeOmissionReasons.ComponentRetired, omission.Reason);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitAbsentComponent_IsAnOmission()
	{
		// ADR-0023: "Stale or removed selections fail with actionable refresh
		// guidance rather than silently widening scope" -- a component a later
		// refresh no longer observed (lifecycle 'absent') must be an omission, not
		// silently included as if still current.
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		IReadOnlyList<Guid> seeded = await SeedCompatibleComponentsAsync(targetId, "host-1001", "host-1002");
		Guid staying = seeded[0];
		Guid goingAbsent = seeded[1];

		// A second discovery pass that only reports "staying" marks "goingAbsent" absent.
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", "host-1001", "host-host-1001", null, _compatibleCatalogComponentId, CompatibleExactVersion)], CancellationToken.None);

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [staying, goingAbsent]), CancellationToken.None);

		Assert.Equal([staying], resolved.ResolvedComponentIds);
		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(goingAbsent, omission.ComponentId);
		Assert.Equal(ScopeOmissionReasons.ComponentAbsent, omission.Reason);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitFactConflict_IsAnOmission()
	{
		// ADR-0023: a configured/discovered version conflict is never silently
		// resolved by this resolver -- only an interactive Cyber+ initiator may
		// choose, which is plan-preview integration, explicitly out of this slice.
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		Guid componentId = await SeedComponentAsync(targetId, "host-1001", exactVersion: "8.0.3");
		await _components.SetConfiguredFactAsync(componentId, "8.0.2", CancellationToken.None);

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [componentId]), CancellationToken.None);

		Assert.Empty(resolved.ResolvedComponentIds);
		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(ScopeOmissionReasons.FactConflict, omission.Reason);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitComponentWithNoCatalogLink_IsCatalogIncompatibleOmission()
	{
		// A component never linked to a catalog entry (the common case before #732's
		// discovery-job wiring lands) fails closed via ComponentCapabilityMatcher --
		// never silently treated as runnable.
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		Guid componentId = await SeedComponentAsync(targetId, "host-1001");

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [componentId]), CancellationToken.None);

		Assert.Empty(resolved.ResolvedComponentIds);
		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(ScopeOmissionReasons.CatalogIncompatible, omission.Reason);
	}

	[Fact]
	public async Task ResolveAsync_ExplicitDuplicateComponentId_IsResolvedOnlyOnce()
	{
		// Determinism: naming the same id twice must not produce two resolved rows
		// (a downstream persisted array/audit-history join expects distinct ids).
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		Guid componentId = await SeedComponentAsync(targetId, "host-1001");
		await _components.SetConfiguredFactAsync(componentId, "8.0.3", CancellationToken.None); // still no catalog link -> omission either way

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.Explicit, null, [componentId, componentId]), CancellationToken.None);

		Assert.Single(resolved.Omissions);
	}

	// -- all mode --------------------------------------------------------------------

	[Fact]
	public async Task ResolveAsync_AllModeWholeSite_IncludesEveryTargetsComponents_AsOmissionsGivenNoCatalogLink()
	{
		// "All" with no target_ids means every target under the site (ADR-0023) --
		// every component surfaces (as catalog-incompatible omissions here, since
		// none is catalog-linked), never silently dropped.
		Guid siteId = await SeedSiteAsync();
		Guid targetA = await SeedTargetAsync(siteId, "vcenter-a");
		Guid targetB = await SeedTargetAsync(siteId, "vcenter-b");
		await SeedComponentAsync(targetA, "host-1001");
		await SeedComponentAsync(targetB, "host-2001");

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.All, null, null), CancellationToken.None);

		Assert.Equal(TargetScopeModes.All, resolved.Mode);
		Assert.Empty(resolved.ResolvedComponentIds);
		Assert.Equal(2, resolved.Omissions.Count);
		Assert.All(resolved.Omissions, o => Assert.Equal(ScopeOmissionReasons.CatalogIncompatible, o.Reason));
	}

	[Fact]
	public async Task ResolveAsync_AllModeScopedToOneTarget_ExcludesSiblingTargetsComponents()
	{
		Guid siteId = await SeedSiteAsync();
		Guid targetA = await SeedTargetAsync(siteId, "vcenter-a");
		Guid targetB = await SeedTargetAsync(siteId, "vcenter-b");
		Guid componentA = await SeedComponentAsync(targetA, "host-1001");
		await SeedComponentAsync(targetB, "host-2001");

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.All, [targetA], null), CancellationToken.None);

		// Neither component is catalog-linked, so both are omissions -- but only
		// componentA's omission may appear; targetB's component must never surface
		// when targetB was not named.
		Assert.All(resolved.Omissions, o => Assert.Equal(componentA, o.ComponentId));
		Assert.Single(resolved.Omissions);
	}

	[Fact]
	public async Task ResolveAsync_AllModeUnknownTargetId_IsTargetNotFoundOmission()
	{
		Guid siteId = await SeedSiteAsync();
		Guid bogusTargetId = Guid.NewGuid();

		ResolvedTargetScope resolved = await _resolution.ResolveAsync(
			siteId, new TargetScopeRequest(TargetScopeModes.All, [bogusTargetId], null), CancellationToken.None);

		ScopeOmission omission = Assert.Single(resolved.Omissions);
		Assert.Equal(ScopeOmissionReasons.TargetNotFound, omission.Reason);
		Assert.Null(omission.ComponentId);
		Assert.Equal(bogusTargetId, omission.TargetId);
	}

	[Fact]
	public async Task ResolveAsync_IsDeterministic_AcrossRepeatedCalls()
	{
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId, "vcenter-a");
		await SeedComponentAsync(targetId, "host-1001");
		await SeedComponentAsync(targetId, "host-1002");
		await SeedComponentAsync(targetId, "host-1003");

		TargetScopeRequest request = new(TargetScopeModes.All, null, null);
		ResolvedTargetScope first = await _resolution.ResolveAsync(siteId, request, CancellationToken.None);
		ResolvedTargetScope second = await _resolution.ResolveAsync(siteId, request, CancellationToken.None);

		Assert.Equal(first.ResolvedComponentIds, second.ResolvedComponentIds);
		Assert.Equal(
			first.Omissions.Select(o => (o.ComponentId, o.Reason)).OrderBy(t => t.ComponentId),
			second.Omissions.Select(o => (o.ComponentId, o.Reason)).OrderBy(t => t.ComponentId));
	}
}
