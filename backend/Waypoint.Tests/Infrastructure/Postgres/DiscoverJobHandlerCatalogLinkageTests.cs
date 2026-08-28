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
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #985's own end-to-end proof: the discovered-component-to-catalog-component
/// LINKAGE mechanism itself (<see cref="DiscoverJobHandler.ResolveCatalogLinkageAsync"/>),
/// as distinct from <see cref="DiscoverJobHandlerEsxiVersionLinkageTests"/>, which proves
/// the version FACT (issue #974) by resolving the catalog id directly in its own fixture
/// and simulating linkage with a hand-applied <c>with</c> expression -- that test's own
/// doc comment says so explicitly ("that linkage is a separate, pre-existing mechanism
/// this issue does not touch"). Before this issue, no such mechanism existed at all
/// (<see cref="DiscoverJobHandler.MapToComponents"/> always passed
/// <c>CatalogComponentId: null</c>) -- this suite is the first to exercise the real
/// resolver end to end, against the REAL migration 0064 seed, through the UNMODIFIED
/// <see cref="ComponentCapabilityMatcher"/>.
/// </summary>
[Collection("Postgres")]
public sealed class DiscoverJobHandlerCatalogLinkageTests : IAsyncLifetime
{
	private const string SeededExactVersion = "8.0.3"; // Matches migration 0064's real seeded 'vsphere'/'8.0.3' row -- invented input, not lab-observed.
	private const string UnseededExactVersion = "9.9.9"; // Invented -- deliberately not seeded anywhere, proves the honest zero-match path.

	private readonly PostgresFixture _fixture;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;

	public DiscoverJobHandlerCatalogLinkageTests(PostgresFixture fixture)
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
	/// Same shared-database hazard <see cref="DiscoverJobHandlerEsxiVersionLinkageTests"/>
	/// documents: deletes only the runtime rows this suite seeds itself, never truncates
	/// any catalog_* table, since the whole point is to exercise migration 0064's real,
	/// already-applied seed.
	/// </summary>
	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE component_observations, components, inventory_items, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
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
	/// Core acceptance: a component discovered with an exact version matching a REAL
	/// seeded catalog row resolves CatalogComponentId through
	/// <see cref="DiscoverJobHandler.ResolveCatalogLinkageAsync"/> -- no direct id
	/// resolution in the test fixture, unlike #978's test -- and once persisted,
	/// <see cref="ComponentCapabilityMatcher"/> (entirely unmodified) reports
	/// is_compatible=true.
	/// </summary>
	[Fact]
	public async Task DiscoveredHost_WithVersionMatchingSeededCatalogRow_ResolvesLinkage_AndMatcherReportsCompatible()
	{
		await using (NpgsqlConnection seedConnection = new(_fixture.ConnectionString))
		{
			await seedConnection.OpenAsync();
			await ReapplySeedMigrationAsync(seedConnection);
		}

		Guid targetId = await SeedVsphereTargetAsync();

		DiscoveredComponent hostMapping = new(
			CatalogComponentKey: "esxi", VendorIdentity: "host-985-compatible", DisplayName: "esxi-01.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: SeededExactVersion);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> ambiguities) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [hostMapping], CancellationToken.None);
		Assert.Empty(ambiguities);
		DiscoveredComponent resolvedMapping = linked.Single();
		Assert.NotNull(resolvedMapping.CatalogComponentId);

		await _components.UpsertDiscoveredAsync(targetId, linked, CancellationToken.None);

		Component persisted = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-985-compatible");
		Assert.Equal(resolvedMapping.CatalogComponentId, persisted.CatalogComponentId);

		IReadOnlyList<CatalogExecutionProfileDetail> profiles =
			await _catalog.ListExecutionProfilesByComponentAsync(persisted.CatalogComponentId!.Value, CancellationToken.None);
		Assert.NotEmpty(profiles);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(
			persisted, profiles[0].ProductVersion.Id, profiles[0].ProductVersion.VersionKey, profiles);

		Assert.True(match.IsCompatible, string.Join("; ", match.IncompatibilityReasons));
		Assert.NotEmpty(match.CompatibleProfiles);
	}

	/// <summary>
	/// Negative: an exact version with no seeded catalog coverage at all resolves to
	/// null (honest "not yet covered"), never a guessed nearest match (ADR-0022).
	/// </summary>
	[Fact]
	public async Task DiscoveredHost_WithNoMatchingCatalogVersion_StaysUnlinked_NoAmbiguity()
	{
		await using (NpgsqlConnection seedConnection = new(_fixture.ConnectionString))
		{
			await seedConnection.OpenAsync();
			await ReapplySeedMigrationAsync(seedConnection);
		}

		DiscoveredComponent hostMapping = new(
			CatalogComponentKey: "esxi", VendorIdentity: "host-985-nomatch", DisplayName: "esxi-02.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: UnseededExactVersion);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> ambiguities) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [hostMapping], CancellationToken.None);

		Assert.Empty(ambiguities);
		Assert.Null(linked.Single().CatalogComponentId);
	}

	/// <summary>
	/// Negative: a component with no exact version this pass (unavailable) is never
	/// even looked up -- stays unlinked, no ambiguity, matching
	/// <see cref="ComponentCapabilityMatcher"/>'s own fail-closed "no configured or
	/// discovered exact product version" gate one layer up.
	/// </summary>
	[Fact]
	public async Task DiscoveredComponent_WithNoExactVersion_NeverLooksUp_StaysUnlinked()
	{
		DiscoveredComponent vmMapping = new(
			CatalogComponentKey: "vm", VendorIdentity: "vm-985-no-version", DisplayName: "vm-01",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: null);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> ambiguities) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [vmMapping], CancellationToken.None);

		Assert.Empty(ambiguities);
		Assert.Null(linked.Single().CatalogComponentId);
	}

	/// <summary>
	/// Issue #995 belt-and-braces: even though <see cref="DiscoverJobHandler.MapToComponents"/>
	/// now normalizes an empty/whitespace Version to null before this method ever runs,
	/// this guard is hardened to <c>string.IsNullOrWhiteSpace</c> (not <c>is null</c>) so
	/// a <see cref="DiscoveredComponent"/> constructed directly with ExactVersion = ""
	/// (bypassing MapToComponents entirely, as this suite's other tests already do) never
	/// reaches <see cref="ICatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync"/>
	/// -- which throws ArgumentException on an empty/whitespace value -- and instead stays
	/// unlinked, exactly like the already-covered null case.
	/// </summary>
	[Fact]
	public async Task DiscoveredComponent_WithEmptyStringExactVersion_NeverLooksUp_StaysUnlinked_NeverThrows()
	{
		DiscoveredComponent hostMapping = new(
			CatalogComponentKey: "esxi", VendorIdentity: "host-995-empty-version", DisplayName: "esxi-05.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: string.Empty);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> warnings) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [hostMapping], CancellationToken.None);

		Assert.Empty(warnings);
		Assert.Null(linked.Single().CatalogComponentId);
	}

	/// <summary>Same as above, for a whitespace-only ExactVersion.</summary>
	[Fact]
	public async Task DiscoveredComponent_WithWhitespaceOnlyExactVersion_NeverLooksUp_StaysUnlinked_NeverThrows()
	{
		DiscoveredComponent hostMapping = new(
			CatalogComponentKey: "esxi", VendorIdentity: "host-995-whitespace-version", DisplayName: "esxi-06.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: "   ");

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> warnings) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [hostMapping], CancellationToken.None);

		Assert.Empty(warnings);
		Assert.Null(linked.Single().CatalogComponentId);
	}

	/// <summary>
	/// Ambiguous match: two distinct products both seed a top-level component sharing
	/// the same component_key + exact version -- structurally possible since discovery
	/// supplies no product context. Must stay unlinked with an honest reason, never an
	/// arbitrary "first match wins" (ADR-0022 "never guesses a winner").
	/// </summary>
	[Fact]
	public async Task DiscoveredHost_WithAmbiguousCatalogMatch_StaysUnlinked_ReportsAmbiguity()
	{
		string sharedComponentKey = $"ambiguous-{Guid.NewGuid():N}";
		string sharedVersion = "1.2.3";
		await SeedTopLevelCatalogComponentAsync("vendor-a", $"product-a-{Guid.NewGuid():N}", sharedVersion, sharedComponentKey);
		await SeedTopLevelCatalogComponentAsync("vendor-b", $"product-b-{Guid.NewGuid():N}", sharedVersion, sharedComponentKey);

		DiscoveredComponent mapping = new(
			CatalogComponentKey: sharedComponentKey, VendorIdentity: "component-985-ambiguous", DisplayName: "ambiguous component",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: sharedVersion);

		(IReadOnlyList<DiscoveredComponent> linked, IReadOnlyList<string> ambiguities) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [mapping], CancellationToken.None);

		Assert.Null(linked.Single().CatalogComponentId);
		Assert.Single(ambiguities);
		Assert.Contains(sharedComponentKey, ambiguities[0], StringComparison.Ordinal);
		Assert.Contains(sharedVersion, ambiguities[0], StringComparison.Ordinal);
	}

	/// <summary>
	/// Re-discovery/stale-link acceptance (issue #985's own AC: "a version change must
	/// re-link or honestly unlink -- never keep a stale link"): a component first links
	/// against the seeded 8.0.3 row, then a later discovery pass reports a version with
	/// no catalog coverage -- the persisted link must be cleared, not preserved by a
	/// COALESCE that only ever adds a link and never removes one.
	/// </summary>
	[Fact]
	public async Task Rediscovery_WithChangedVersion_UnlinksStaleCatalogComponentId()
	{
		await using (NpgsqlConnection seedConnection = new(_fixture.ConnectionString))
		{
			await seedConnection.OpenAsync();
			await ReapplySeedMigrationAsync(seedConnection);
		}

		Guid targetId = await SeedVsphereTargetAsync();

		DiscoveredComponent firstPass = new(
			CatalogComponentKey: "esxi", VendorIdentity: "host-985-rediscovery", DisplayName: "esxi-03.example.internal",
			ParentVendorIdentity: null, CatalogComponentId: null, ExactVersion: SeededExactVersion);

		(IReadOnlyList<DiscoveredComponent> firstLinked, _) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [firstPass], CancellationToken.None);
		await _components.UpsertDiscoveredAsync(targetId, firstLinked, CancellationToken.None);

		Component afterFirstPass = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-985-rediscovery");
		Assert.NotNull(afterFirstPass.CatalogComponentId);

		DiscoveredComponent secondPass = firstPass with { ExactVersion = UnseededExactVersion };
		(IReadOnlyList<DiscoveredComponent> secondLinked, _) =
			await DiscoverJobHandler.ResolveCatalogLinkageAsync(_catalog, [secondPass], CancellationToken.None);
		await _components.UpsertDiscoveredAsync(targetId, secondLinked, CancellationToken.None);

		Component afterSecondPass = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-985-rediscovery");
		Assert.Null(afterSecondPass.CatalogComponentId); // The stale link from pass 1 must NOT survive pass 2's honest re-evaluation.
		Assert.Equal(UnseededExactVersion, afterSecondPass.DiscoveredFact?.ExactVersion);
	}

	private async Task<Guid> SeedVsphereTargetAsync()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", "{}", credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return targetId!.Value;
	}

	private async Task SeedTopLevelCatalogComponentAsync(string vendor, string productKey, string versionKey, string componentKey)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, vendor, productKey, productKey, CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, versionKey, versionKey, CancellationToken.None);
		await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition(componentKey, componentKey, CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null),
			CancellationToken.None);
	}
}
