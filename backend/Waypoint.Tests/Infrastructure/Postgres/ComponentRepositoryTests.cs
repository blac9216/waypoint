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
using Waypoint.Core.Components;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #732 (epic #726, Wave 2): stable compliance endpoint/component identity
/// beneath a top-level target, against a real PostgreSQL 16 container (migration
/// 0054). Fixtures below are INVENTED -- managed-object-reference-shaped identifiers
/// only, never exported from any real system (CLAUDE.md sanitization policy).
///
/// Covers issue #732's persistence ACs: linked vCenters (two targets each with their
/// own component tree), duplicate names under different parents (two named VCSA
/// services sharing a component key under different parent components), refresh
/// survival (identity persists across a second discovery pass with unchanged upstream
/// state), and retirement detection (continuous-absence threshold promotion).
/// </summary>
[Collection("Postgres")]
public sealed class ComponentRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ComponentRepository _repository = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;

	public ComponentRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_repository = new ComponentRepository(_fixture.ConnectionString, new CatalogRepository(_fixture.ConnectionString));
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE component_observations, components, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedTargetAsync(string name)
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, name, "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return targetId!.Value;
	}

	/// <summary>
	/// Issue #743: an ssh target has no discovery operation, so its ROOT component is
	/// Admin-declared. The created row is UNLINKED (no catalog_component_id, no facts) --
	/// declaration establishes IDENTITY only; catalog linkage happens later through the
	/// shared configured-fact path, never guessed at declaration time.
	/// </summary>
	[Fact]
	public async Task CreateDeclaredRootAsync_CreatesUnlinkedFactlessActiveRoot()
	{
		Guid targetId = await SeedTargetAsync("declared-root-target");

		Guid? componentId = await _repository.CreateDeclaredRootAsync(targetId, "photon", CancellationToken.None);

		Assert.NotNull(componentId);
		Component created = (await _repository.GetAsync(componentId!.Value, CancellationToken.None))!;
		Assert.Equal(targetId, created.ParentTargetId);
		Assert.Equal("photon", created.CatalogComponentKey);
		Assert.Equal("photon", created.DisplayName);
		Assert.Null(created.ParentComponentId);
		Assert.Null(created.VendorIdentity);
		Assert.Null(created.CatalogComponentId);
		Assert.Null(created.ConfiguredFact);
		Assert.Null(created.DiscoveredFact);
		Assert.Equal(ComponentLifecycleStates.Active, created.Lifecycle);
	}

	/// <summary>
	/// Issue #743: declaration is creation-only. A second declaration of the same key
	/// under the same target returns null (the caller surfaces a 409) and never mutates,
	/// relinks, or duplicates the existing row -- the multi-product case is served by
	/// DISTINCT keys under one target, not by re-declaring one.
	/// </summary>
	[Fact]
	public async Task CreateDeclaredRootAsync_DuplicateKey_ReturnsNullAndLeavesTheExistingRowIntact()
	{
		Guid targetId = await SeedTargetAsync("declared-root-duplicate");
		Guid first = (await _repository.CreateDeclaredRootAsync(targetId, "vidm", CancellationToken.None))!.Value;

		Guid? second = await _repository.CreateDeclaredRootAsync(targetId, "vidm", CancellationToken.None);

		Assert.Null(second);
		IReadOnlyList<Component> components = await _repository.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Component only = Assert.Single(components);
		Assert.Equal(first, only.Id);
		Assert.Equal("vidm", only.DisplayName);
	}

	/// <summary>
	/// Issue #743 AC "multiple components on one appliance are independently
	/// represented": two DIFFERENT catalog products declared on the same ssh target are
	/// two independent root rows, with no hard-coded product branching anywhere -- the
	/// keys are catalog data.
	/// </summary>
	[Fact]
	public async Task CreateDeclaredRootAsync_TwoDistinctProductsOnOneTarget_AreIndependentRoots()
	{
		Guid targetId = await SeedTargetAsync("declared-root-multi");

		Guid photon = (await _repository.CreateDeclaredRootAsync(targetId, "photon", CancellationToken.None))!.Value;
		Guid aria = (await _repository.CreateDeclaredRootAsync(targetId, "aria-operations", CancellationToken.None))!.Value;

		Assert.NotEqual(photon, aria);
		IReadOnlyList<Component> components = await _repository.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Assert.Equal(2, components.Count);
		Assert.Contains(components, c => c.CatalogComponentKey == "photon");
		Assert.Contains(components, c => c.CatalogComponentKey == "aria-operations");
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_TwoLinkedVCenters_KeepIndependentComponentTrees()
	{
		// "Linked vCenters" AC: two independently configured top-level targets, each
		// with its own vCenter + ESXi host components -- identity must never bleed
		// across targets even when the invented vendor identities collide by coincidence.
		Guid targetA = await SeedTargetAsync("vcenter-a");
		Guid targetB = await SeedTargetAsync("vcenter-b");

		DiscoveredComponent[] items =
		[
			new("vcenter", "vim.ServiceInstance:ServiceInstance", "vCenter Server", null, null, "8.0.3"),
			new("esxi", "host-1001", "esxi-01.example.internal", null, null, "8.0.3"),
		];

		await _repository.UpsertDiscoveredAsync(targetA, items, CancellationToken.None);
		await _repository.UpsertDiscoveredAsync(targetB, items, CancellationToken.None);

		IReadOnlyList<Component> componentsA = await _repository.ListForTargetAsync(targetA, includeRetired: true, CancellationToken.None);
		IReadOnlyList<Component> componentsB = await _repository.ListForTargetAsync(targetB, includeRetired: true, CancellationToken.None);

		Assert.Equal(2, componentsA.Count);
		Assert.Equal(2, componentsB.Count);
		Assert.DoesNotContain(componentsA, c => componentsB.Select(b => b.Id).Contains(c.Id));
		Assert.All(componentsA, c => Assert.Equal(targetA, c.ParentTargetId));
		Assert.All(componentsB, c => Assert.Equal(targetB, c.ParentTargetId));
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_DuplicateServiceNamesUnderDifferentParents_AreDistinctComponents()
	{
		// "Duplicate names under different parents" AC: two VCSA-shaped parent
		// components each declare a same-keyed "eam" sub-service. Identity is
		// (parent, catalog key), not name, so both persist independently.
		Guid target = await SeedTargetAsync("vcenter-duplicate-parents");

		DiscoveredComponent[] parents =
		[
			new("vcsa", "vim.VirtualMachine:vm-201", "vcsa-01.example.internal", null, null, "8.0.3"),
			new("vcsa", "vim.VirtualMachine:vm-202", "vcsa-02.example.internal", null, null, "8.0.3"),
		];
		await _repository.UpsertDiscoveredAsync(target, parents, CancellationToken.None);

		DiscoveredComponent[] children =
		[
			new("eam", null, "EAM Service", "vim.VirtualMachine:vm-201", null, null),
			new("eam", null, "EAM Service", "vim.VirtualMachine:vm-202", null, null),
		];
		await _repository.UpsertDiscoveredAsync(target, [.. parents, .. children], CancellationToken.None);

		IReadOnlyList<Component> all = await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None);
		List<Component> eams = [.. all.Where(c => c.CatalogComponentKey == "eam")];

		Assert.Equal(2, eams.Count);
		Assert.NotEqual(eams[0].ParentComponentId, eams[1].ParentComponentId);
		Assert.All(eams, c => Assert.NotNull(c.ParentComponentId));
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_SecondPassWithUnchangedUpstream_SurvivesRefreshWithSameIdentity()
	{
		// "Refresh survival" AC: re-running discovery with the exact same vendor
		// identity must reuse the same component row id -- never create a sibling.
		Guid target = await SeedTargetAsync("vcenter-refresh");
		DiscoveredComponent[] items = [new("esxi", "host-9001", "esxi-01.example.internal", null, null, "8.0.3")];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		IReadOnlyList<Component> firstPass = await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None);
		Guid firstId = Assert.Single(firstPass).Id;

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		IReadOnlyList<Component> secondPass = await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None);
		Component reDiscovered = Assert.Single(secondPass);

		Assert.Equal(firstId, reDiscovered.Id);
		Assert.Equal(ComponentLifecycleStates.Active, reDiscovered.Lifecycle);
	}

	/// <summary>
	/// Issue #1081: <see cref="ComponentFact.Build"/> round-trips through the real
	/// JSONB discovered_fact column -- both the write (<see cref="ComponentRepository.UpsertDiscoveredAsync"/>)
	/// and the read (<c>DeserializeFact</c>) sides.
	/// </summary>
	[Fact]
	public async Task UpsertDiscoveredAsync_WithBuild_RoundTripsBuildOnTheDiscoveredFact()
	{
		Guid target = await SeedTargetAsync("vcenter-build-roundtrip");
		DiscoveredComponent[] items = [new("esxi", "host-9101", "esxi-01.example.internal", null, null, "8.0.3", "99.0.12345678")];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component component = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));

		Assert.NotNull(component.DiscoveredFact);
		Assert.Equal("8.0.3", component.DiscoveredFact!.ExactVersion);
		Assert.Equal("99.0.12345678", component.DiscoveredFact.Build);
	}

	/// <summary>
	/// Issue #1081: a discovered fact with no build observed (e.g. a component whose
	/// discovery pass could not read it) stores/reads back honestly null -- never a
	/// parse failure, never a guessed value.
	/// </summary>
	[Fact]
	public async Task UpsertDiscoveredAsync_WithNoBuild_DiscoveredFactBuildStaysNull()
	{
		Guid target = await SeedTargetAsync("vcenter-build-absent");
		DiscoveredComponent[] items = [new("esxi", "host-9102", "esxi-02.example.internal", null, null, "8.0.3")];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component component = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));

		Assert.NotNull(component.DiscoveredFact);
		Assert.Null(component.DiscoveredFact!.Build);
	}

	/// <summary>
	/// Issue #1063: <see cref="ComponentFact.DerivedFromParent"/> round-trips through
	/// the real JSONB discovered_fact column, same as <see cref="ComponentFact.Build"/>
	/// above -- distinguishing a VM's parent-derived fact from a directly observed one
	/// (epic #726 section 3, "Provenance is visible and snapshotted") survives the
	/// write/read boundary, not just the in-memory DTO.
	/// </summary>
	[Fact]
	public async Task UpsertDiscoveredAsync_WithDerivedFromParent_RoundTripsOnTheDiscoveredFact()
	{
		Guid target = await SeedTargetAsync("vm-derived-roundtrip");
		DiscoveredComponent[] items = [new("vm", "vm-9201", "stub-vm-01", null, null, "8.0.3", "99.0.12345678", DerivedFromParent: true)];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component component = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));

		Assert.NotNull(component.DiscoveredFact);
		Assert.True(component.DiscoveredFact!.DerivedFromParent);
	}

	/// <summary>
	/// Issue #1063: a directly observed fact (a host's, or the vcenter root's own) is
	/// never marked derived -- both the explicit false this writer emits and the
	/// JSONB-field-OMITTED shape (a fact recorded before this field existed, which no
	/// migration rewrites) read back false. The omitted shape cannot be produced by the
	/// repository's own writer, so the second half rewrites the stored fact to the
	/// pre-#1063 form with raw SQL and reads it back through the repository -- proving
	/// the reader treats an absent key exactly like false rather than assuming it.
	/// </summary>
	[Fact]
	public async Task UpsertDiscoveredAsync_WithoutDerivedFromParent_DiscoveredFactStaysFalse()
	{
		Guid target = await SeedTargetAsync("host-not-derived-roundtrip");
		DiscoveredComponent[] items = [new("esxi", "host-9202", "esxi-03.example.internal", null, null, "8.0.3")];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component component = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));

		Assert.NotNull(component.DiscoveredFact);
		Assert.False(component.DiscoveredFact!.DerivedFromParent);

		// The legacy shape: exact_version/observed_at only, no derived_from_parent key.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync(CancellationToken.None);
			await using NpgsqlCommand rewrite = new(
				"""
				UPDATE components
				SET discovered_fact = jsonb_build_object('exact_version', '8.0.3', 'observed_at', now())
				WHERE id = $1
				""", connection);
			rewrite.Parameters.AddWithValue(component.Id);
			await rewrite.ExecuteNonQueryAsync(CancellationToken.None);
		}

		Component legacy = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));
		Assert.Equal("8.0.3", legacy.DiscoveredFact?.ExactVersion);
		Assert.False(legacy.DiscoveredFact!.DerivedFromParent);
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_ComponentNoLongerReported_BecomesAbsentThenRetiresPastThreshold()
	{
		// "Retirement detection" AC: a component a later successful boundary no longer
		// observes becomes absent (identity retained), and only crosses into retired
		// once continuous absence exceeds the given threshold.
		Guid target = await SeedTargetAsync("vcenter-retirement");
		DiscoveredComponent[] items = [new("esxi", "host-4001", "esxi-01.example.internal", null, null, "8.0.3")];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component beforeRemoval = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));
		Assert.Equal(ComponentLifecycleStates.Active, beforeRemoval.Lifecycle);

		ComponentUpsertOutcome outcome = await _repository.UpsertDiscoveredAsync(target, [], CancellationToken.None);
		Assert.Equal(1, outcome.MarkedAbsent);

		Component afterRemoval = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));
		Assert.Equal(ComponentLifecycleStates.Absent, afterRemoval.Lifecycle);
		Assert.NotNull(afterRemoval.ContinuousAbsenceSince);

		// Not yet past a generous threshold: still absent, not retired.
		int retiredNow = await _repository.RetireContinuouslyAbsentAsync(TimeSpan.FromDays(7), CancellationToken.None);
		Assert.Equal(0, retiredNow);

		// Past a zero threshold: promotes to retired.
		int retiredPastThreshold = await _repository.RetireContinuouslyAbsentAsync(TimeSpan.Zero, CancellationToken.None);
		Assert.Equal(1, retiredPastThreshold);

		Component afterRetirement = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));
		Assert.Equal(ComponentLifecycleStates.Retired, afterRetirement.Lifecycle);
		Assert.NotNull(afterRetirement.RetiredAt);

		// Rediscovery before purge reconnects rather than duplicating, even once retired.
		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component reconnected = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));
		Assert.Equal(afterRetirement.Id, reconnected.Id);
		Assert.Equal(ComponentLifecycleStates.Active, reconnected.Lifecycle);
		Assert.Null(reconnected.ContinuousAbsenceSince);
	}

	[Fact]
	public async Task SetConfiguredFactAsync_DisagreesWithDiscoveredFact_RaisesFactConflict()
	{
		Guid target = await SeedTargetAsync("vcenter-fact-conflict");
		DiscoveredComponent[] items = [new("esxi", "host-5001", "esxi-01.example.internal", null, null, "8.0.3")];
		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component discovered = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));

		ComponentWriteOutcome outcome = await _repository.SetConfiguredFactAsync(discovered.Id, "8.0.2", CancellationToken.None);
		Assert.Equal(ComponentWriteOutcome.Ok, outcome);

		Component conflicted = (await _repository.GetAsync(discovered.Id, CancellationToken.None))!;
		Assert.True(conflicted.FactConflict);
		Assert.Equal("8.0.2", conflicted.ConfiguredFact?.ExactVersion);
		Assert.Equal("8.0.3", conflicted.DiscoveredFact?.ExactVersion);

		IReadOnlyList<ComponentObservation> observations = await _repository.ListObservationsAsync(discovered.Id, CancellationToken.None);
		Assert.Contains(observations, o => o.Source == ComponentObservationSources.Configured && o.Outcome == ComponentObservationOutcomes.Conflict);
	}

	[Fact]
	public async Task SetConfiguredFactAsync_UnknownComponent_ReturnsNotFound()
	{
		ComponentWriteOutcome outcome = await _repository.SetConfiguredFactAsync(Guid.NewGuid(), "8.0.3", CancellationToken.None);
		Assert.Equal(ComponentWriteOutcome.NotFound, outcome);
	}

	[Fact]
	public async Task PurgeRetiredAsync_NotRetired_ReturnsNotFound_ThenSucceedsOnceRetired()
	{
		Guid target = await SeedTargetAsync("vcenter-purge");
		DiscoveredComponent[] items = [new("esxi", "host-6001", "esxi-01.example.internal", null, null, "8.0.3")];
		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Component active = Assert.Single(await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None));

		Assert.Equal(ComponentWriteOutcome.NotFound, await _repository.PurgeRetiredAsync(active.Id, CancellationToken.None));

		await _repository.UpsertDiscoveredAsync(target, [], CancellationToken.None);
		await _repository.RetireContinuouslyAbsentAsync(TimeSpan.Zero, CancellationToken.None);

		Assert.Equal(ComponentWriteOutcome.Ok, await _repository.PurgeRetiredAsync(active.Id, CancellationToken.None));
		Assert.Null(await _repository.GetAsync(active.Id, CancellationToken.None));
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_ConcurrentIdenticalVendorIdentityPasses_DedupesToOneRowNoDeadlockOrError()
	{
		// Issue #840: two "replicas" (here, two concurrent calls against the same
		// target/connection pool) discovering the exact same vendor-identity component
		// at once must dedupe to exactly one row via the atomic ON CONFLICT upsert --
		// never throw a duplicate-key violation and never race to two sibling rows,
		// which the prior check-then-insert implementation could not guarantee.
		Guid target = await SeedTargetAsync("vcenter-concurrent-vendor-identity");
		DiscoveredComponent[] items = [new("esxi", "host-7001", "esxi-01.example.internal", null, null, "8.0.3")];

		Task<ComponentUpsertOutcome> first = _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Task<ComponentUpsertOutcome> second = _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		await Task.WhenAll(first, second);

		IReadOnlyList<Component> all = await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None);
		Component only = Assert.Single(all);
		Assert.Equal("host-7001", only.VendorIdentity);
		Assert.Equal(ComponentLifecycleStates.Active, only.Lifecycle);
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_ConcurrentIdenticalNoVendorIdentityPasses_DedupesToOneRowNoDeadlockOrError()
	{
		// Same race, for the OTHER identity branch migration 0054 backs: the
		// no-vendor-identity partial-index case (a named service with no independent
		// upstream object) -- issue #840 explicitly calls out that this case could not
		// previously share one atomic conflict target with the vendor-identity case.
		Guid target = await SeedTargetAsync("vcenter-concurrent-no-vendor-identity");
		DiscoveredComponent[] parent = [new("vcsa", "vim.VirtualMachine:vm-301", "vcsa-03.example.internal", null, null, "8.0.3")];
		await _repository.UpsertDiscoveredAsync(target, parent, CancellationToken.None);

		DiscoveredComponent[] items =
		[
			.. parent,
			new("eam", null, "EAM Service", "vim.VirtualMachine:vm-301", null, null),
		];

		Task<ComponentUpsertOutcome> first = _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		Task<ComponentUpsertOutcome> second = _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		await Task.WhenAll(first, second);

		IReadOnlyList<Component> all = await _repository.ListForTargetAsync(target, includeRetired: true, CancellationToken.None);
		Component eam = Assert.Single(all, c => c.CatalogComponentKey == "eam");
		Assert.Null(eam.VendorIdentity);
		Assert.Equal(ComponentLifecycleStates.Active, eam.Lifecycle);
	}

	[Fact]
	public async Task UpsertDiscoveredAsync_ReconnectsRetiredVendorIdentityComponent_ReportsReconnectedNotUpserted()
	{
		// Outcome-counter correctness after the atomic rewrite: a retired component that
		// reappears must be counted as Reconnected (not Upserted), matching the
		// pre-rewrite behavior this rewrite must preserve.
		Guid target = await SeedTargetAsync("vcenter-reconnect-counter");
		DiscoveredComponent[] items = [new("esxi", "host-8001", "esxi-01.example.internal", null, null, "8.0.3")];

		await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);
		await _repository.UpsertDiscoveredAsync(target, [], CancellationToken.None);
		await _repository.RetireContinuouslyAbsentAsync(TimeSpan.Zero, CancellationToken.None);

		ComponentUpsertOutcome reconnectOutcome = await _repository.UpsertDiscoveredAsync(target, items, CancellationToken.None);

		Assert.Equal(1, reconnectOutcome.Reconnected);
		Assert.Equal(0, reconnectOutcome.Upserted);
	}
}
