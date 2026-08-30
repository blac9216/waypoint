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

using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Discovery;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #21 (epic #13) full-loop acceptance, through the REAL loop: a
/// <c>discover</c> job is fanned out, the dispatcher claims it, the real
/// <see cref="DiscoverJobHandler"/> resolves the target's vCenter credential,
/// decrypts it under job attribution (the #8 store), invokes the stub
/// <c>Invoke-WaypointDiscovery</c> module in-process, and the parsed cluster/host/vm
/// rows land in <c>inventory_items</c> via <see cref="InventoryRepository"/> --
/// idempotent on re-discovery (upsert, no duplicate hosts) and detecting removals --
/// while the password canary never reaches <c>job_events</c> or <c>jobs.note</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class DiscoverJobHandlerEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDiscoveryStubModule", "WaypointDiscoveryStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-discover-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private InventoryRepository _inventory = null!;
	private ComponentRepository _components = null!;
	private Waypoint.Infrastructure.ComplianceContent.CatalogRepository _catalog = null!;
	private DiscoverJobHandler _handler = null!;

	public DiscoverJobHandlerEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetDiscoveryDataAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 2 };
		powerShellOptions.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedPsOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedPsOptions, NullLogger<PowerShellExecutor>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_inventory = new InventoryRepository(_fixture.ConnectionString);
		_components = new ComponentRepository(_fixture.ConnectionString, new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString));
		_catalog = new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString);

		_handler = new DiscoverJobHandler(
			executor, _secretStore, _credentials, _targets, _inventory, _components, _catalog, _repository, _redactor, wrappedPsOptions);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	private JobDispatcherHostedService CreateDispatcher()
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 };
		return new JobDispatcherHostedService(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	/// <summary>
	/// The full loop: fan out a <c>discover</c> job for a seeded vsphere target ->
	/// dispatcher claims it -> the real handler decrypts the credential, invokes the
	/// stub module (pass 1: cluster + 2 hosts + 1 vm) -> inventory_items has the rows,
	/// discover.progress was emitted, and target.discovery_status/last_refreshed
	/// updated -> re-running (pass 2: one host dropped) upserts the survivors
	/// in place (no duplicates) AND marks the dropped host removed.
	/// </summary>
	[Fact]
	public async Task SyncToDispatchToHandler_PopulatesInventory_UpsertsIdempotently_AndDetectsRemovals()
	{
		const string canary = "invented-discover-e2e-canary-f4a1";
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync(canary);

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		Guid firstRunId = await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 5); // vcenter + cluster + 2 hosts + 1 vm
		Assert.True(await EventTypeExistsAsync(JobEventTypes.DiscoverProgress, firstRunId));
		Assert.Equal(TargetDiscoveryStatuses.Discovered, (await _targets.GetAsync(targetId, CancellationToken.None))!.DiscoveryStatus);
		Assert.NotNull((await _targets.GetAsync(targetId, CancellationToken.None))!.LastRefreshed);

		// Issue #974: the raw build number is retained in inventory_items exactly as
		// before, alongside (never replaced by) the new semantic Version -- host-11
		// reports a Version, host-12 does not, but BOTH still carry their Build.
		InventoryItem host11 = (await GetItemByMorefAsync(targetId, "host-11"))!;
		Assert.Equal("99.0.12345678", host11.Build);
		Assert.Equal("8.0.3", host11.Version);
		InventoryItem host12 = (await GetItemByMorefAsync(targetId, "host-12"))!;
		Assert.Equal("99.0.12345678", host12.Build);
		Assert.Null(host12.Version);

		// Issue #732 (discovery-wiring remainder): the same successful pass must also
		// materialize `components` rows -- the synthetic vcenter root plus one component
		// per esxi/vm, never one for the vSphere-grouping-only `cluster` type (no
		// catalog component analogue). Pass 1's stub inventory is cluster/host-11/vm-101/
		// host-12 -- 4 components expected: root + host-11 + host-12 + vm-101 (cluster
		// dropped; see this handler's own MapToComponents doc comment for the mapping
		// rule).
		await AssertComponentCountAsync(targetId, expectedActive: 4);

		// Issue #974: host-11's component-level ExactVersion is the semantic Version
		// ("8.0.3"), never the raw Build ("99.0.12345678") -- ADR-0022's byte-for-byte
		// contract means using Build here could never match any real catalog row.
		Waypoint.Core.Components.Component host11Component = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-11");
		Assert.Equal("8.0.3", host11Component.DiscoveredFact?.ExactVersion);
		Assert.False(host11Component.DiscoveredFact!.DerivedFromParent); // A host's fact is always directly observed.

		// Issue #1063: vm-101's own inventory_items row records its authoritative
		// instance UUID, and its component-level fact is DERIVED from this pass's own
		// (deliberately unseeded) vcenter root fact -- see the stub module's own
		// comment on vm-101 for why root and vm share the same unseeded version.
		InventoryItem vm101Item = (await GetItemByMorefAsync(targetId, "vm-101"))!;
		Assert.Equal("vm-instance-uuid-101", vm101Item.InstanceUuid);
		Waypoint.Core.Components.Component vm101Component = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "vm-101");
		Assert.Equal("0.0.0-invented-unseeded", vm101Component.DiscoveredFact?.ExactVersion);
		Assert.True(vm101Component.DiscoveredFact!.DerivedFromParent);

		// Issue #1082: this pass's fixture is a natural mix -- host-11 links (real
		// seeded 8.0.3), but the root vcenter and its derived vm-101 both carry a
		// deliberately-unseeded exact version (out_of_declared_scope), and host-12
		// reports no version at all (no_exact_version_fact). Before this issue, the
		// discover.progress payload had no way to say so: 3 of the 4 components this
		// pass upserted are catalog-unlinked and unscannable, and the payload/completion
		// note must say exactly that rather than reading like a clean success.
		JsonElement firstPassProgress = await GetDiscoverProgressPayloadAsync(firstRunId);
		Assert.Equal(3, firstPassProgress.GetProperty("components_unlinked").GetInt32());
		JsonElement unlinkedByReason = firstPassProgress.GetProperty("components_unlinked_by_reason");
		Assert.Equal(2, unlinkedByReason.GetProperty("out_of_declared_scope").GetInt32());
		Assert.Equal(1, unlinkedByReason.GetProperty("no_exact_version_fact").GetInt32());

		Guid firstPassJobId = await GetJobIdForRunAsync(firstRunId);
		string firstPassJobLog = await GetJobLogTextAsync(firstPassJobId);
		Assert.Contains("no_exact_version_fact", firstPassJobLog, StringComparison.Ordinal);
		Assert.Contains("out_of_declared_scope", firstPassJobLog, StringComparison.Ordinal);
		Assert.Contains("host-12", firstPassJobLog, StringComparison.Ordinal);

		string firstPassNote = await GetJobNoteAsync(firstPassJobId);
		Assert.Contains("3 catalog-unlinked", firstPassNote, StringComparison.Ordinal);

		// Re-discover with the SAME pass: same identities upsert in place, not duplicated.
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 5);
		await AssertComponentCountAsync(targetId, expectedActive: 4);

		// Pass 2: host-12 is no longer reported -- removal detection marks it (and its
		// child, cascaded by ON DELETE CASCADE semantics N/A here since this is a soft
		// delete -- host-12 itself carries no children in this fixture) removed_at, and
		// EXCLUDES it from the active listing, without deleting the surviving 3 rows.
		// The parallel components pass must mark the host-12 COMPONENT absent too --
		// the same fail-closed absence rule as inventory, proven independently.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "2");
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 4);
		await AssertItemRemovedAsync(targetId, "host-12");
		Guid removedHostId = (await GetItemByMorefAsync(targetId, "host-12"))!.Id;
		await AssertComponentCountAsync(targetId, expectedActive: 3); // root + host-11 + vm-101
		await AssertComponentAbsentAsync(targetId, "host-12");

		// Reappear-after-removal: host-12 is reported again in a later discovery. The
		// moref-keyed upsert must UN-delete the existing row (removed_at -> NULL) rather
		// than insert a duplicate -- same row id, active again, count back to 4.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 5);
		InventoryItem revived = (await GetItemByMorefAsync(targetId, "host-12"))!;
		Assert.Equal(removedHostId, revived.Id); // no duplicate -- same row un-deleted
		Assert.Null(revived.RemovedAt);
		await AssertComponentCountAsync(targetId, expectedActive: 4);
		await AssertComponentReconnectedAsync(targetId, "host-12");

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	/// <summary>
	/// Issue #1081's own end-to-end proof, through the REAL loop (stub PowerShell pass
	/// '1081-linked', which reports the appliance's `content.about`-style identity
	/// exactly as the real module now does): the discovered vCenter component carries
	/// an authoritative stable vendor identifier and a version fact with provenance
	/// (never absent, never guessed from FQDN/IP/display name), the appliance's own
	/// `inventory_items` row exists, AND -- because the version fact matches migration
	/// 0064's real seeded 'vsphere'/'8.0.3' row -- the root LINKS against the catalog
	/// and issue #741's catalog-declared VCSA service expansion actually fires. Epic
	/// #726's round-11 live validation found this whole chain broken
	/// (`declared_services_upserted: 0`, "never exercised against a real appliance");
	/// this test is the first to exercise it end to end.
	/// </summary>
	[Fact]
	public async Task VCenterRoot_GetsVendorIdentityAndVersionFact_LinksToCatalog_AndDeclaredServicesExpand()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-1081-vcenter-canary");

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1081-linked");
		Guid runId = await RunDiscoverOnceAsync(targetId);

		Guid jobId = await GetJobIdForRunAsync(runId);
		Assert.Equal("done", await GetJobFieldAsync(jobId, "state"));

		// The appliance's own inventory_items row exists -- deliverable #2: previously
		// there was no inventory_items row of any vCenter/appliance type at all.
		InventoryItem? vcenterItem = await GetItemByMorefAsync(targetId, "vcenter-instance-1081-linked");
		Assert.NotNull(vcenterItem);
		Assert.Equal(InventoryItemTypes.VCenter, vcenterItem!.Type);
		Assert.Equal("8.0.3", vcenterItem.Version);
		Assert.Equal("99.0.87654321", vcenterItem.Build);
		Assert.Null(vcenterItem.ParentId); // top-level, same as the root component below.

		// The root component carries a real vendor identity (never null/absent) and a
		// discovered fact with both exact_version and build -- deliverable #1 and #4.
		Waypoint.Core.Components.Component root = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter);
		Assert.Equal("vcenter-instance-1081-linked", root.VendorIdentity);
		Assert.NotNull(root.DiscoveredFact);
		Assert.Equal("8.0.3", root.DiscoveredFact!.ExactVersion);
		Assert.Equal("99.0.87654321", root.DiscoveredFact.Build);

		// The version fact matches a real seeded catalog row -- the root LINKS, unlike
		// before this issue where it could never carry a version at all.
		Assert.NotNull(root.CatalogComponentId);

		// #1065's catalog-declared VCSA service expansion (eam/lookup/postgresql/vami
		// under migration 0064's seeded vsphere/8.0.3 product version) now fires off
		// the linked root -- proven both via the components it materialized and via
		// the discover.progress event's own declared_services_upserted counter, the
		// exact field round-11 observed stuck at zero.
		IReadOnlyList<Waypoint.Core.Components.Component> declaredChildren = (await _components
			.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Where(c => c.ParentComponentId == root.Id)
			.ToList();
		Assert.NotEmpty(declaredChildren);
		Assert.All(declaredChildren, c => Assert.Null(c.VendorIdentity));

		JsonElement progress = await GetDiscoverProgressPayloadAsync(runId);
		Assert.True(progress.GetProperty("declared_services_upserted").GetInt32() > 0);

		// Issue #1082's other half: a fully-successful, fully-linked pass (this
		// fixture's only discovered component -- the root -- links) must report zero
		// unlinked, distinguishing it from the mixed/unlinked pass covered by
		// SyncToDispatchToHandler_PopulatesInventory_UpsertsIdempotently_AndDetectsRemovals
		// above -- both are "done" jobs, but only one is honestly a clean success.
		Assert.Equal(0, progress.GetProperty("components_unlinked").GetInt32());
		string note = await GetJobNoteAsync(jobId);
		Assert.DoesNotContain("catalog-unlinked", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1063's core acceptance, through the real loop: two identically NAMED
	/// VMs under a linked vCenter both derive the SAME exact_version/build from the
	/// root's own discovered fact -- bulk stamping, zero per-VM configuration -- and
	/// remain two DISTINCT components because their identity is keyed on MoRef, with
	/// their vSphere instance UUID (deconfliction fact, issue #1063's other AC)
	/// recorded on the `inventory_items` row. The derived fact is marked
	/// <see cref="Waypoint.Core.Components.ComponentFact.DerivedFromParent"/> = true,
	/// distinguishing it from the root's own directly-observed fact (epic #726
	/// section 3, "Provenance is visible and snapshotted") -- proven end-to-end
	/// through the real DB round trip, not just the pure MapToComponents unit tests.
	/// </summary>
	[Fact]
	public async Task Vm_WithLinkedParentVersion_DerivesFactInBulk_AndRecordsDistinctUuidsForDuplicateNames()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-1063-bulk-canary");

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1063-bulk-derivation");
		await RunDiscoverOnceAsync(targetId);

		InventoryItem vmA = (await GetItemByMorefAsync(targetId, "vm-1063-bulk-a"))!;
		InventoryItem vmB = (await GetItemByMorefAsync(targetId, "vm-1063-bulk-b"))!;
		Assert.Equal("vm-instance-uuid-1063-bulk-a", vmA.InstanceUuid);
		Assert.Equal("vm-instance-uuid-1063-bulk-b", vmB.InstanceUuid);
		Assert.NotEqual(vmA.InstanceUuid, vmB.InstanceUuid);
		Assert.Equal(vmA.Name, vmB.Name); // Identically named, per this test's own name.

		IReadOnlyList<Waypoint.Core.Components.Component> components =
			await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Waypoint.Core.Components.Component root = components.Single(c => c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter);
		Waypoint.Core.Components.Component vmComponentA = components.Single(c => c.VendorIdentity == "vm-1063-bulk-a");
		Waypoint.Core.Components.Component vmComponentB = components.Single(c => c.VendorIdentity == "vm-1063-bulk-b");

		// Two distinct components -- never collapsed by the shared display name.
		Assert.NotEqual(vmComponentA.Id, vmComponentB.Id);

		foreach (Waypoint.Core.Components.Component vmComponent in new[] { vmComponentA, vmComponentB })
		{
			Assert.Equal(root.DiscoveredFact!.ExactVersion, vmComponent.DiscoveredFact?.ExactVersion);
			Assert.Equal(root.DiscoveredFact.Build, vmComponent.DiscoveredFact?.Build);
			Assert.True(vmComponent.DiscoveredFact!.DerivedFromParent, "a VM's fact must be recorded as derived-from-parent, never indistinguishable from a directly observed one.");
		}

		// The root's OWN fact is directly observed, never derived.
		Assert.False(root.DiscoveredFact!.DerivedFromParent);
	}

	/// <summary>
	/// Issue #1063 (round-1 review, blocker 1) -- the two-pass case, and the only
	/// sequence in which this issue's honest-degradation rule can actually be wrong.
	/// Pass 1 derives and links two VMs from a linked vCenter root fact; pass 2
	/// re-discovers the SAME target with the same VM MoRefs and no `vcenter` row at
	/// all (issue #1115's shape). The derived fact must be CLEARED, not retained:
	/// <c>UpsertDiscoveredAsync</c>'s COALESCE keeps a directly OBSERVED fact across a
	/// pass that could not observe it (issue #1000, deliberately unchanged), but a
	/// derived fact has no independent observation to fall back on -- retaining it
	/// would leave the VM stamped `derived_from_parent: true` and catalog-linked (so
	/// still plannable, against a possibly-wrong baseline) off a parent fact this pass
	/// could not see. Epic #726 section 3: absent facts stay honestly absent.
	/// </summary>
	[Fact]
	public async Task Vm_WhenParentFactDisappearsOnALaterPass_ClearsTheDerivedFactAndItsLink()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-1063-two-pass-canary");

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1063-bulk-derivation");
		await RunDiscoverOnceAsync(targetId);

		IReadOnlyList<Waypoint.Core.Components.Component> afterPass1 =
			await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		foreach (string moref in new[] { "vm-1063-bulk-a", "vm-1063-bulk-b" })
		{
			Waypoint.Core.Components.Component derived = afterPass1.Single(c => c.VendorIdentity == moref);
			Assert.Equal("8.0.3", derived.DiscoveredFact?.ExactVersion);
			Assert.True(derived.DiscoveredFact!.DerivedFromParent);
			Assert.NotNull(derived.CatalogComponentId);
		}

		// Pass 2 on the SAME target: same VM MoRefs, no `vcenter` row this time.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1063-parent-fact-lost");
		await RunDiscoverOnceAsync(targetId);

		IReadOnlyList<Waypoint.Core.Components.Component> afterPass2 =
			await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		foreach (string moref in new[] { "vm-1063-bulk-a", "vm-1063-bulk-b" })
		{
			Waypoint.Core.Components.Component cleared = afterPass2.Single(c => c.VendorIdentity == moref);
			Assert.Null(cleared.DiscoveredFact); // Version-absent: no stale derived version survives.
			Assert.Null(cleared.CatalogComponentId); // Unlinked: no longer plannable off a fact this pass could not see.
			Assert.False(cleared.FactConflict);
		}

		// The host's OWN directly-observed fact is untouched by the derived-fact clear
		// -- issue #1000's retain-last-known behaviour for observed facts is unchanged.
		Waypoint.Core.Components.Component host = afterPass2.Single(c => c.VendorIdentity == "host-1063-bulk");
		Assert.Equal("8.0.3", host.DiscoveredFact?.ExactVersion);
		Assert.False(host.DiscoveredFact!.DerivedFromParent);
	}

	/// <summary>
	/// Issue #1063's honest-degradation AC, and issue #1115's exact concrete case
	/// (no session matched the target by name, so the pass never emitted a `vcenter`
	/// row at all): a VM discovered under a root with no version fact this pass must
	/// stay honestly version-absent -- no discovered_fact recorded at all, never a
	/// guess and never a stale/default value.
	/// </summary>
	[Fact]
	public async Task Vm_WithNoParentVersionFact_StaysHonestlyVersionAbsent()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-1063-no-parent-canary");

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1063-no-parent-version");
		await RunDiscoverOnceAsync(targetId);

		Waypoint.Core.Components.Component vmComponent = (await _components
			.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "vm-1063-no-parent");
		Assert.Null(vmComponent.DiscoveredFact);
		Assert.Null(vmComponent.CatalogComponentId); // Never links with no exact version.

		InventoryItem vmItem = (await GetItemByMorefAsync(targetId, "vm-1063-no-parent"))!;
		Assert.Equal("vm-instance-uuid-1063-no-parent", vmItem.InstanceUuid); // Still recorded even though version is absent.
	}

	/// <summary>
	/// Issue #1081 (round-1 review, blocker 1) -- the MIGRATION case, and the one that
	/// decides whether this issue is actually fixed on the deployment it was filed
	/// from. Every target discovered before #1081 already carries a `vcenter` root with
	/// `vendor_identity IS NULL`, because the pre-#1081 module reported no appliance
	/// identity at all. Giving the root a real identity also moves its upsert from the
	/// null-identity partial index to `components_vendor_identity_unique` -- a
	/// DIFFERENT ON CONFLICT target, which does not see the existing row. Both
	/// constraints permit the result, so the pass inserted a SECOND root; the
	/// declared-service lookup then resolved the tie by `created_at` and picked the
	/// older, unlinked, identity-less one, leaving `declared_services_upserted: 0`
	/// forever -- the single metric this issue exists to move -- while any children
	/// #741 had already materialized were marked absent.
	///
	/// This test seeds the pre-#1081 shape FIRST and then runs the real pass, so it
	/// starts from the real deployment's state rather than a fresh target (which is
	/// why the sibling test above cannot catch this: every stub pass there emits an
	/// identity, so only one root is ever created). The bar is one root, adopted in
	/// place -- same row id, its history intact -- identified, linked, expanding.
	/// </summary>
	[Fact]
	public async Task PreExistingNullIdentityVCenterRoot_IsAdoptedInPlace_NotDuplicated_AndDeclaredServicesExpand()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-1081-adoption-canary");

		// The pre-#1081 shape, exactly as the old module produced it: a vcenter root
		// with no vendor identity and no version opinion, hence no catalog linkage.
		await _components.UpsertDiscoveredAsync(
			targetId,
			[new Waypoint.Core.Components.DiscoveredComponent(
				Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter,
				VendorIdentity: null,
				DisplayName: "vCenter Server",
				ParentVendorIdentity: null,
				CatalogComponentId: null,
				ExactVersion: null)],
			CancellationToken.None);

		Waypoint.Core.Components.Component legacyRoot = Assert.Single(
			(await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
				.Where(c => c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter));
		Assert.Null(legacyRoot.VendorIdentity);
		Assert.Null(legacyRoot.CatalogComponentId);

		// Now the post-#1081 pass runs against that pre-existing state.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1081-linked");
		Guid runId = await RunDiscoverOnceAsync(targetId);
		Assert.Equal("done", await GetJobFieldAsync(await GetJobIdForRunAsync(runId), "state"));

		// ONE root, not two -- including retired rows, so a "dedupe by retiring the
		// loser" implementation could not pass this by hiding the duplicate.
		IReadOnlyList<Waypoint.Core.Components.Component> all = await _components
			.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Waypoint.Core.Components.Component root = Assert.Single(
			all.Where(c => c.ParentComponentId is null
				&& c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter));

		// ADOPTED, not replaced: the same row the legacy pass created now carries the
		// identity. This is what preserves first_seen_at, any Admin configured_fact,
		// and the parent_component_id FKs of children already materialized under it.
		Assert.Equal(legacyRoot.Id, root.Id);
		Assert.Equal("vcenter-instance-1081-linked", root.VendorIdentity);
		Assert.Equal(Waypoint.Core.Components.ComponentLifecycleStates.Active, root.Lifecycle);
		Assert.Equal("8.0.3", root.DiscoveredFact?.ExactVersion);
		Assert.NotNull(root.CatalogComponentId);

		// And the consequence the issue was actually filed for: the expansion fires.
		Assert.NotEmpty(all.Where(c => c.ParentComponentId == root.Id));
		JsonElement progress = await GetDiscoverProgressPayloadAsync(runId);
		Assert.True(
			progress.GetProperty("declared_services_upserted").GetInt32() > 0,
			"declared_services_upserted must move on a target discovered BEFORE this shipped -- that is the whole point of #1081.");
	}

	/// <summary>
	/// Issue #732's fail-closed requirement (ADR-0023: "a failed or partial boundary...
	/// neither claims completeness nor advances absence"): a malformed discovery result
	/// must fail the job BEFORE either inventory or component materialization runs --
	/// proven here by asserting zero components exist afterward, not merely that the
	/// job failed (which <see cref="MalformedDiscoveryRows_FailTheJob_InsteadOfSilentlySucceedingWithZeroItems"/>
	/// already covers for inventory).
	/// </summary>
	[Fact]
	public async Task MalformedDiscoveryRows_NeverMaterializeOrAbsentAnyComponent()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-malformed-component-canary");

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		await RunDiscoverOnceAsync(targetId);
		await AssertComponentCountAsync(targetId, expectedActive: 4);

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "malformed");
		Guid failedRunId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			failedRunId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));

		// The prior successful pass's 4 active components must be entirely unchanged --
		// no new absence, no reconnect, no mutation of any kind from the failed pass.
		await AssertComponentCountAsync(targetId, expectedActive: 4);
	}

	/// <summary>
	/// Issue #865 (ADR-0023 completeness gap): a partial vSphere enumeration -- the
	/// stub's 'partial' pass, standing in for a non-terminating per-subtree PowerCLI
	/// error (unreachable ESXi/permission-denied cluster) -- still reports
	/// <c>succeeded == true</c> with only the objects it COULD enumerate (host-12's
	/// subtree dropped) but must NOT mass-mark host-12 removed/absent on either the
	/// inventory or component path. The job itself still reaches 'done' (a partial
	/// boundary is not a hard failure per ADR-0023 -- it retains earlier rows as
	/// unverified cache and raises a diagnostic, it does not fail the job), and the
	/// diagnostic must land in job.log.
	/// </summary>
	[Fact]
	public async Task PartialEnumeration_RefreshesSeenObjects_ButNeverAdvancesAbsence_OnEitherPath()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-partial-canary");

		// Pass 1: full inventory (cluster + host-11 + vm-101 + host-12) -- establishes
		// host-12 as a previously-seen, currently-active row/component.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 5);
		await AssertComponentCountAsync(targetId, expectedActive: 4);

		// Pass 2: 'partial' -- host-12 is not reported (its subtree "failed"), and the
		// module's discovery-meta marker says Complete = $false.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "partial");
		Guid partialRunId = await RunDiscoverOnceAsync(targetId);
		Guid partialJobId = await GetJobIdForRunAsync(partialRunId);

		// The job still reaches 'done' -- a partial boundary is not a hard failure.
		Assert.Equal("done", await GetJobFieldAsync(partialJobId, "state"));

		// host-12 must still be active/not-removed on BOTH paths -- absence was not
		// advanced despite this pass not reporting it.
		await AssertInventoryCountAsync(targetId, expectedActive: 5);
		InventoryItem host12AfterPartial = (await GetItemByMorefAsync(targetId, "host-12"))!;
		Assert.Null(host12AfterPartial.RemovedAt);
		await AssertComponentCountAsync(targetId, expectedActive: 4);
		IReadOnlyList<Waypoint.Core.Components.Component> componentsAfterPartial =
			await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Waypoint.Core.Components.Component host12Component = componentsAfterPartial.Single(c => c.VendorIdentity == "host-12");
		Assert.Equal(Waypoint.Core.Components.ComponentLifecycleStates.Active, host12Component.Lifecycle);
		Assert.Null(host12Component.ContinuousAbsenceSince);

		// The cluster/vm-101/host-11 objects the partial pass DID see are still
		// refreshed (upserted), not merely left alone -- last_seen_at moved forward.
		InventoryItem host11AfterPartial = (await GetItemByMorefAsync(targetId, "host-11"))!;
		Assert.True(host11AfterPartial.LastSeenAt > host11AfterPartial.CreatedAt || host11AfterPartial.LastSeenAt == host11AfterPartial.UpdatedAt);

		// The partial-failure diagnostic reached job.log (issue #865's alert path).
		string jobLogText = await GetJobLogTextAsync(partialJobId);
		Assert.Contains("partial vSphere enumeration", jobLogText, StringComparison.Ordinal);
		Assert.Contains("invented-unreachable-esxi-02-error", jobLogText, StringComparison.Ordinal);

		// The job's own note also names the partial pass (visible in job history without
		// digging into job_events).
		string note = await GetJobNoteAsync(partialJobId);
		Assert.Contains("PARTIAL enumeration", note, StringComparison.Ordinal);

		// Immediately after: a genuinely COMPLETE pass (back to '2') must still be able
		// to advance absence normally -- the gate is per-pass, not sticky.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "2");
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 4);
		await AssertItemRemovedAsync(targetId, "host-12");
		await AssertComponentCountAsync(targetId, expectedActive: 3);
		await AssertComponentAbsentAsync(targetId, "host-12");
	}

	/// <summary>
	/// Issue #995's failing-test-first proof: a real vSphere discovery batch containing
	/// ONE host reporting Version as an empty string (powered-off/disconnected) alongside
	/// one normal linkable host must not crash the whole job. Before the fix, the
	/// empty-version host's "" slipped past <see cref="DiscoverJobHandler.ResolveCatalogLinkageAsync"/>'s
	/// `is null` guard and reached <c>CatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync</c>'s
	/// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> unhandled, aborting the entire
	/// discovery job (job state 'failed', components = 0, evidence in issue #995). This
	/// test fails on pre-fix main with exactly that ArgumentException; after the fix the
	/// job succeeds, the linkable host links against the real migration 0064 seed, and
	/// the empty-version host persists unlinked (its own component row untouched,
	/// discovered_fact absent) rather than blocking the other host or the job.
	/// </summary>
	[Fact]
	public async Task DiscoveryBatch_WithOneEmptyVersionHost_Succeeds_LinkableHostLinks_EmptyVersionHostStaysUnlinked()
	{
		await using (NpgsqlConnection seedConnection = new(_fixture.ConnectionString))
		{
			await seedConnection.OpenAsync();
			await ReapplySeedMigrationAsync(seedConnection);
		}

		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-995-empty-version-canary");

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "995-empty-version");
		Guid runId = await RunDiscoverOnceAsync(targetId);

		Guid jobId = await GetJobIdForRunAsync(runId);
		Assert.Equal("done", await GetJobFieldAsync(jobId, "state"));
		string note = await GetJobNoteAsync(jobId);
		Assert.DoesNotContain("ArgumentException", note, StringComparison.Ordinal);
		Assert.DoesNotContain("exactVersion", note, StringComparison.Ordinal);

		// root vcenter + host-995-linkable + host-995-empty-version -- neither host is
		// dropped from materialization; only catalog linkage differs between them.
		await AssertComponentCountAsync(targetId, expectedActive: 3);

		Waypoint.Core.Components.Component linkable = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-995-linkable");
		Assert.Equal("8.0.3", linkable.DiscoveredFact?.ExactVersion);
		Assert.NotNull(linkable.CatalogComponentId);

		Waypoint.Core.Components.Component emptyVersionHost = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == "host-995-empty-version");
		Assert.Null(emptyVersionHost.DiscoveredFact); // ExactVersion normalized to null -- no discovered_fact recorded, same as a null-Version host.
		Assert.Null(emptyVersionHost.CatalogComponentId); // Fail-closed: never linked, never guessed.
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

	[Fact]
	public async Task TargetWithNoCredential_FailsCleanly_WithoutThrowing()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		string connectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", connectionJson, credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId!.Value });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("no credential", note, StringComparison.Ordinal);
		Assert.Equal(TargetDiscoveryStatuses.NeverDiscovered, (await _targets.GetAsync(targetId.Value, CancellationToken.None))!.DiscoveryStatus);
	}

	/// <summary>
	/// Issue #262: a vCenter credential with no dedicated username set fails the job
	/// cleanly rather than silently falling back to the credential's display name as
	/// the SSO login.
	/// </summary>
	[Fact]
	public async Task TargetWithCredentialMissingUsername_FailsCleanly_WithoutFallingBackToName()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-no-username-canary", username: null);

		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("no username set", note, StringComparison.Ordinal);
		// Same as TargetWithNoCredential_FailsCleanly_WithoutThrowing above: this check
		// runs before discovery_status is stamped 'discovering', so a target that never
		// got that far stays at its initial value rather than moving to 'failed'.
		Assert.Equal(TargetDiscoveryStatuses.NeverDiscovered, (await _targets.GetAsync(targetId, CancellationToken.None))!.DiscoveryStatus);
	}

	/// <summary>
	/// Issue #618's "genuinely empty vCenter" acceptance criterion: the module
	/// connecting successfully and reporting zero rows is a legitimate, clean success
	/// (0 upserted, 0 removed) -- the malformed-row fail-closed check below must not
	/// punish a target that really has no inventory.
	/// </summary>
	[Fact]
	public async Task TargetWithNoInventory_StillSucceeds_WithZeroItems()
	{
		Guid targetId = (await SeedVsphereTargetAsync("invented-empty-vcenter-canary")).TargetId;

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "empty");
		Guid runId = await RunDiscoverOnceAsync(targetId);

		Guid jobId = await GetJobIdForRunAsync(runId);
		Assert.Equal("done", await GetJobFieldAsync(jobId, "state"));
		await AssertInventoryCountAsync(targetId, expectedActive: 0);
		Assert.Equal(TargetDiscoveryStatuses.Discovered, (await _targets.GetAsync(targetId, CancellationToken.None))!.DiscoveryStatus);
		string note = await GetJobNoteAsync(jobId);
		Assert.Contains("Discovered 0 item(s)", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #618's core acceptance criterion: rows the module DID return but this
	/// handler cannot parse (the executor-output-capture-loses-NoteProperties shape
	/// the live vCenter reproduction hit) must fail the job with an actionable note --
	/// never read as a clean "Discovered 0 item(s)" success, and never leave inventory
	/// silently unpopulated without a visible error.
	/// </summary>
	[Fact]
	public async Task MalformedDiscoveryRows_FailTheJob_InsteadOfSilentlySucceedingWithZeroItems()
	{
		Guid targetId = (await SeedVsphereTargetAsync("invented-malformed-canary")).TargetId;

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "malformed");
		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("could not be parsed", note, StringComparison.Ordinal);
		Assert.DoesNotContain("Discovered 0 item(s)", note, StringComparison.Ordinal);
		await AssertInventoryCountAsync(targetId, expectedActive: 0);
		Assert.Equal(TargetDiscoveryStatuses.Failed, (await _targets.GetAsync(targetId, CancellationToken.None))!.DiscoveryStatus);
	}

	private async Task<Guid> GetJobIdForRunAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT id FROM jobs WHERE run_id = $1 LIMIT 1", connection);
		query.Parameters.AddWithValue(runId);
		return (Guid)(await query.ExecuteScalarAsync())!;
	}

	private async Task<Guid> RunDiscoverOnceAsync(Guid targetId)
	{
		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("done", await GetJobFieldAsync(jobIds[0], "state"));
		return runId;
	}

	private async Task<(Guid TargetId, Guid CredentialId)> SeedVsphereTargetAsync(string secretValue, string? username = "administrator@example.internal")
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-discovery-{Guid.NewGuid():N}@example.internal", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, username))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		return (targetId!.Value, credentialId);
	}

	private async Task AssertInventoryCountAsync(Guid targetId, int expectedActive)
	{
		IReadOnlyList<InventoryItem> active = await _inventory.ListAsync(targetId, includeRemoved: false, CancellationToken.None);
		Assert.Equal(expectedActive, active.Count);
	}

	private async Task AssertItemRemovedAsync(Guid targetId, string moref)
	{
		IReadOnlyList<InventoryItem> all = await _inventory.ListAsync(targetId, includeRemoved: true, CancellationToken.None);
		InventoryItem? item = all.FirstOrDefault(i => i.Moref == moref);
		Assert.NotNull(item);
		Assert.NotNull(item!.RemovedAt);
	}

	private async Task<InventoryItem?> GetItemByMorefAsync(Guid targetId, string moref)
	{
		IReadOnlyList<InventoryItem> all = await _inventory.ListAsync(targetId, includeRemoved: true, CancellationToken.None);
		return all.FirstOrDefault(i => i.Moref == moref);
	}

	/// <summary>
	/// Counts components whose lifecycle is <c>active</c> specifically (not merely
	/// "not retired" -- <see cref="Waypoint.Core.Components.IComponentRepository.ListForTargetAsync"/>'s
	/// <c>includeRetired: false</c> still returns <c>absent</c> rows, since absence
	/// retains identity per ADR-0023).
	/// </summary>
	private async Task AssertComponentCountAsync(Guid targetId, int expectedActive)
	{
		IReadOnlyList<Waypoint.Core.Components.Component> all =
			await _components.ListForTargetAsync(targetId, includeRetired: false, CancellationToken.None);
		int active = all.Count(c => c.Lifecycle == Waypoint.Core.Components.ComponentLifecycleStates.Active);
		Assert.Equal(expectedActive, active);
	}

	private async Task AssertComponentAbsentAsync(Guid targetId, string vendorIdentity)
	{
		IReadOnlyList<Waypoint.Core.Components.Component> all =
			await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Waypoint.Core.Components.Component? component = all.FirstOrDefault(c => c.VendorIdentity == vendorIdentity);
		Assert.NotNull(component);
		Assert.Equal(Waypoint.Core.Components.ComponentLifecycleStates.Absent, component!.Lifecycle);
	}

	private async Task AssertComponentReconnectedAsync(Guid targetId, string vendorIdentity)
	{
		IReadOnlyList<Waypoint.Core.Components.Component> all =
			await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None);
		Waypoint.Core.Components.Component? component = all.FirstOrDefault(c => c.VendorIdentity == vendorIdentity);
		Assert.NotNull(component);
		Assert.Equal(Waypoint.Core.Components.ComponentLifecycleStates.Active, component!.Lifecycle);
		Assert.Null(component.ContinuousAbsenceSince);
	}

	/// <summary>
	/// security.md control 1/4 + the epic #6/#8 canary machinery, proven through THIS
	/// handler: the credential value never reaches job_events payloads, jobs.note, or
	/// inventory_items, and the #8 decrypt audit row carries this job's attribution.
	/// </summary>
	private async Task AssertCanaryNeverLeakedAsync(string canary, Guid credentialId)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand leaked = new(
			"SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", connection))
		{
			leaked.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand notes = new(
			"SELECT count(*) FROM jobs WHERE note LIKE '%' || $1 || '%'", connection))
		{
			notes.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await notes.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand items = new(
			"SELECT count(*) FROM inventory_items WHERE name LIKE '%' || $1 || '%' OR build LIKE '%' || $1 || '%'", connection))
		{
			items.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await items.ExecuteScalarAsync())!);
		}

		// The #8 audit trail: at least one secret.decrypted row for this credential,
		// attributed to a job (job_id NOT NULL) -- proven through this handler, not
		// just at the secrets-store unit level.
		await using (NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id IS NOT NULL", connection))
		{
			audited.Parameters.AddWithValue(credentialId);
			Assert.True((long)(await audited.ExecuteScalarAsync())! >= 1);
		}
	}

	private async Task<bool> EventTypeExistsAsync(string eventType, Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events e JOIN jobs j ON j.id = e.job_id WHERE e.event_type = $1 AND j.run_id = $2", connection);
		query.Parameters.AddWithValue(eventType);
		query.Parameters.AddWithValue(runId);
		return (long)(await query.ExecuteScalarAsync())! > 0;
	}

	/// <summary>The single discover.progress event payload for a run, parsed (issue #1081: asserting declared_services_upserted).</summary>
	private async Task<JsonElement> GetDiscoverProgressPayloadAsync(Guid runId)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(300)); // BufferedJobEventWriter flush window.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT e.payload::text FROM job_events e JOIN jobs j ON j.id = e.job_id WHERE j.run_id = $1 AND e.event_type = $2", connection);
		query.Parameters.AddWithValue(runId);
		query.Parameters.AddWithValue(JobEventTypes.DiscoverProgress);
		string payload = (string)(await query.ExecuteScalarAsync())!;
		return JsonDocument.Parse(payload).RootElement;
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	/// <summary>Concatenates every job.log event's payload for a job (issue #865: asserting the partial-enumeration warning line landed there).</summary>
	private async Task<string> GetJobLogTextAsync(Guid jobId)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(300)); // BufferedJobEventWriter flush window.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT string_agg(payload::text, ' | ') FROM job_events WHERE job_id = $1 AND event_type = $2", connection);
		query.Parameters.AddWithValue(jobId);
		query.Parameters.AddWithValue(JobEventTypes.JobLog);
		object? result = await query.ExecuteScalarAsync();
		return result as string ?? string.Empty;
	}

	private async Task<string> GetJobNoteAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT COALESCE(note, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}

	private async Task ResetDiscoveryDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE inventory_items, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}
}
