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
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Components;
using Waypoint.Core.Discovery;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #974's end-to-end proof, against a real PostgreSQL 16 container with the REAL
/// migration 0064 execution-catalog seed applied (not an invented catalog chain like
/// <see cref="ScopeResolutionServiceTests"/> builds for its own unrelated scenarios):
/// a discovered ESXi host reporting an invented semantic version that happens to equal
/// the seed's real 'vsphere'/'8.0.3' row must resolve <c>is_compatible = true</c>
/// through the unmodified <see cref="ComponentCapabilityMatcher"/>, and a host with no
/// reported version must fail closed.
///
/// <see cref="DiscoverJobHandler.MapToComponents"/> itself never resolves
/// <c>CatalogComponentId</c> (that linkage is a separate, pre-existing mechanism this
/// issue does not touch -- see <see cref="Waypoint.Core.Components.DiscoveredComponent.CatalogComponentId"/>
/// and how <see cref="ScopeResolutionServiceTests"/> seeds it directly), so this test
/// resolves the real seeded catalog_component id by its documented key/version (exactly
/// as <c>ExecutionCatalogSeedTests</c> does) and supplies it the same way a future
/// catalog-linking step would, isolating THIS issue's fix (ExactVersion sourced from
/// Version, not Build) as the only variable under test.
///
/// Same shared-database hazard <see cref="ExecutionCatalogSeedTests"/> documents: this
/// class shares <see cref="PostgresFixture"/>'s ONE database with every other
/// <c>[Collection("Postgres")]</c> class, and a sibling (e.g. <c>ScopeResolutionServiceTests</c>)
/// <c>TRUNCATE</c>s the catalog identity tree in its own <c>InitializeAsync</c> --
/// <c>NpgsqlSchemaMigrator.ApplyAsync()</c> alone would then be a no-op forever once
/// migration 0064 is recorded applied. Each <c>[Fact]</c> below therefore re-applies
/// 0064's raw SQL directly (bypassing the tracking table) immediately before resolving
/// the seeded id, exactly as <see cref="ExecutionCatalogSeedTests"/> does.
/// </summary>
[Collection("Postgres")]
public sealed class DiscoverJobHandlerEsxiVersionLinkageTests : IAsyncLifetime
{
	private const string InventedSemanticVersion = "8.0.3"; // Matches migration 0064's real seeded 'vsphere'/'8.0.3' row -- invented input, not lab-observed.
	private const string InventedBuildNumber = "99.0.13572468"; // Invented -- never a real lab build number; must NOT participate in the match.

	private readonly PostgresFixture _fixture;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;

	public DiscoverJobHandlerEsxiVersionLinkageTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_components = new ComponentRepository(_fixture.ConnectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString));
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// Deletes only the runtime rows this test seeds itself (components/targets/sites)
	/// -- deliberately does NOT truncate any catalog_* table, since the whole point is
	/// to exercise the migration's real, already-applied seed rather than a
	/// per-test-invented one.
	/// </summary>
	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE component_observations, components, inventory_items, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Resolves the real migration-0064-seeded 'vsphere'/'8.0.3'/'esxi' catalog
	/// component id. Re-applies 0064's raw SQL directly against the given connection
	/// first (bypassing the schema_migrations tracking table) -- see this class's own
	/// doc comment: a sibling Postgres-collection test may have TRUNCATEd the catalog
	/// identity tree since some earlier test's migrator run already marked 0064
	/// applied, so relying on <see cref="NpgsqlSchemaMigrator.ApplyAsync"/> alone would
	/// silently resolve nothing here.
	/// </summary>
	private static async Task<Guid> GetSeededEsxiCatalogComponentIdAsync(NpgsqlConnection connection)
	{
		await ReapplySeedMigrationAsync(connection);

		await using NpgsqlCommand command = new(
			"""
			SELECT cc.id
			FROM catalog_components cc
			JOIN catalog_product_versions pv ON pv.id = cc.product_version_id
			JOIN catalog_products p ON p.id = pv.product_id
			WHERE p.product_key = 'vsphere' AND pv.version_key = '8.0.3' AND cc.component_key = 'esxi'
			""", connection);
		object? result = await command.ExecuteScalarAsync();
		Assert.NotNull(result); // Migration 0064 must have seeded exactly this row on main.
		return (Guid)result!;
	}

	private static async Task ReapplySeedMigrationAsync(NpgsqlConnection connection)
	{
		System.Reflection.Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0064_execution_catalog_seed.sql", StringComparison.Ordinal));
		await using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		string sql = await reader.ReadToEndAsync();
		await using NpgsqlCommand reapply = new(sql, connection);
		await reapply.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Issue #974's core acceptance: MapToComponents resolves ExactVersion from
	/// item.Version (never item.Build), and once linked to the real seeded catalog
	/// component that fact matches byte-for-byte -- ComponentCapabilityMatcher (entirely
	/// unmodified by this issue) reports is_compatible = true.
	/// </summary>
	[Fact]
	public async Task DiscoveredHost_WithSemanticVersion_LinksCompatible_AgainstTheRealSeededCatalogRow()
	{
		Guid targetId = await SeedVsphereTargetAsync();

		DiscoveredInventoryItem hostItem = new(
			InventoryItemTypes.Host, "host-974-compatible", "esxi-01.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: InventedSemanticVersion);

		IReadOnlyList<DiscoveredComponent> mapped = DiscoverJobHandler.MapToComponents([hostItem]);
		DiscoveredComponent hostMapping = mapped.Single(c => c.VendorIdentity == "host-974-compatible");
		Assert.Equal(InventedSemanticVersion, hostMapping.ExactVersion);

		Guid esxiCatalogComponentId;
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			esxiCatalogComponentId = await GetSeededEsxiCatalogComponentIdAsync(connection);
		}

		// Simulates the (separate, pre-existing) catalog-linking step: the mapped
		// component carries the resolved catalog_component_id alongside the version
		// MapToComponents already resolved.
		DiscoveredComponent linked = hostMapping with { CatalogComponentId = esxiCatalogComponentId };
		await _components.UpsertDiscoveredAsync(targetId, [linked], CancellationToken.None);

		Component persisted = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-974-compatible");
		Assert.Equal(InventedSemanticVersion, persisted.DiscoveredFact?.ExactVersion);
		Assert.NotEqual(InventedBuildNumber, persisted.DiscoveredFact?.ExactVersion);

		IReadOnlyList<CatalogExecutionProfileDetail> profiles =
			await _catalog.ListExecutionProfilesByComponentAsync(esxiCatalogComponentId, CancellationToken.None);
		Assert.NotEmpty(profiles);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(
			persisted, profiles[0].ProductVersion.Id, profiles[0].ProductVersion.VersionKey, profiles);

		Assert.True(match.IsCompatible, string.Join("; ", match.IncompatibilityReasons));
		Assert.NotEmpty(match.CompatibleProfiles);
	}

	/// <summary>
	/// Issue #974's fail-closed acceptance: a host discovery could not report a semantic
	/// Version for gets ExactVersion=null -- never a value derived or inferred from
	/// Build -- so it reports is_compatible=false with an honest reason even though a
	/// linked catalog component exists.
	/// </summary>
	[Fact]
	public async Task DiscoveredHost_WithNoSemanticVersion_NeverFallsBackToBuild_FailsClosed()
	{
		Guid targetId = await SeedVsphereTargetAsync();

		DiscoveredInventoryItem hostItem = new(
			InventoryItemTypes.Host, "host-974-unavailable", "esxi-02.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: null);

		IReadOnlyList<DiscoveredComponent> mapped = DiscoverJobHandler.MapToComponents([hostItem]);
		DiscoveredComponent hostMapping = mapped.Single(c => c.VendorIdentity == "host-974-unavailable");
		Assert.Null(hostMapping.ExactVersion);

		Guid esxiCatalogComponentId;
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			esxiCatalogComponentId = await GetSeededEsxiCatalogComponentIdAsync(connection);
		}

		DiscoveredComponent linked = hostMapping with { CatalogComponentId = esxiCatalogComponentId };
		await _components.UpsertDiscoveredAsync(targetId, [linked], CancellationToken.None);

		Component persisted = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-974-unavailable");
		Assert.Null(persisted.DiscoveredFact); // No fact recorded at all -- ExactVersion was null, so UpsertDiscoveredAsync stores no discovered_fact.

		IReadOnlyList<CatalogExecutionProfileDetail> profiles =
			await _catalog.ListExecutionProfilesByComponentAsync(esxiCatalogComponentId, CancellationToken.None);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(
			persisted, profiles[0].ProductVersion.Id, profiles[0].ProductVersion.VersionKey, profiles);

		Assert.False(match.IsCompatible);
		Assert.Contains(match.IncompatibilityReasons, r => r.Contains("no configured or discovered exact product version", StringComparison.Ordinal));
	}

	private async Task<Guid> SeedVsphereTargetAsync()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", "{}", credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return targetId!.Value;
	}
}
