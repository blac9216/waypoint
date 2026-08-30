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

namespace Waypoint.Core.Components;

/// <summary>
/// Storage for <c>components</c>/<c>component_observations</c> (migration 0054, issue
/// #732, ADR-0023). Distinct from <see cref="Waypoint.Core.Discovery.InventoryItem"/>'s
/// flat cluster/host/VM cache -- this is the stable-identity layer #732 introduces
/// beneath a top-level <see cref="Waypoint.Core.Sites.Target"/>. Issue #732's
/// discovery-wiring remainder now calls <see cref="UpsertDiscoveredAsync"/> from
/// <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/>'s real vSphere
/// enumeration pass (esxi/vm inventory rows -> components, per that handler's own
/// <c>MapToComponents</c> doc comment); NSX and other non-vSphere discovery sources
/// remain future work.
/// </summary>
public interface IComponentRepository
{
	/// <summary>
	/// All non-retired-by-default components for a target; pass <paramref name="includeRetired"/>
	/// to include retired rows too (Configuration-screen visibility, docs/api-contract.md
	/// <c>/targets/{id}/components</c>: "every known component ... regardless of
	/// lifecycle"). Issue #1202: for the closed <c>ssh</c>/<c>target</c> declared-root
	/// shape (a <see cref="CreateDeclaredRootAsync"/> row that has since linked to an
	/// exact catalog product version), <see cref="Component.DisplayName"/> is RE-DERIVED
	/// at read time from that linked catalog component's own display name -- never the
	/// stored <c>display_name</c> column, which stays the version-neutral catalog
	/// component key from declaration. Every other component's <see cref="Component.DisplayName"/>
	/// is the stored vendor-observed (or catalog-declared-child) value, unchanged.
	/// </summary>
	Task<IReadOnlyList<Component>> ListForTargetAsync(Guid targetId, bool includeRetired, CancellationToken cancellationToken);

	/// <summary>
	/// Single component by id, or null when unknown. Same read-time <see cref="Component.DisplayName"/>
	/// re-derivation as <see cref="ListForTargetAsync"/>: re-derived from the linked
	/// catalog component for the closed <c>ssh</c>/<c>target</c> declared-root shape,
	/// the stored value otherwise.
	/// </summary>
	Task<Component?> GetAsync(Guid componentId, CancellationToken cancellationToken);

	/// <summary>
	/// Applies one successful discovery boundary's results for a target: upserts every
	/// reported component by vendor identity (or by parent+catalog-key when
	/// <see cref="DiscoveredComponent.VendorIdentity"/> is null), reconnecting an
	/// absent/retired row rather than creating a sibling, and marks any previously
	/// active/absent component under this target that this pass did NOT report as
	/// <see cref="ComponentLifecycleStates.Absent"/> (starting/preserving
	/// <see cref="Component.ContinuousAbsenceSince"/>). A component already
	/// <see cref="ComponentLifecycleStates.Retired"/> is left retired even if still
	/// unobserved -- retirement is a one-way state until an explicit purge, not
	/// re-derived every pass. Records one <see cref="ComponentObservation"/> per
	/// discovered fact and one per newly-marked absence. Runs as a single transaction,
	/// same durability contract as
	/// <see cref="Waypoint.Core.Discovery.InventoryRepository.UpsertDiscoveryResultsAsync"/>.
	///
	/// <paramref name="advanceAbsence"/> (issue #865, ADR-0023) gates ONLY the
	/// mark-absent block: the caller passes <c>false</c> for a discovery pass whose
	/// PowerShell boundary reported incomplete enumeration (a subtree failed), so
	/// <paramref name="items"/> this pass DID see are still upserted/reconnected as
	/// unverified-cache refreshes, but nothing this pass didn't see is marked absent --
	/// a partial boundary must never "neither claim completeness nor advance absence."
	/// </summary>
	Task<ComponentUpsertOutcome> UpsertDiscoveredAsync(
		Guid targetId, IReadOnlyList<DiscoveredComponent> items, CancellationToken cancellationToken, bool advanceAbsence = true);

	/// <summary>
	/// Issue #741 (ADR-0023 "For a catalog-declared service with no independent
	/// upstream object, parent identity plus catalog component key is authoritative"):
	/// reconciles the catalog-declared child component set beneath one parent component
	/// against <paramref name="declared"/> (the linked catalog release's own declared
	/// service list, selected by <see cref="CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren"/>
	/// -- never a hard-coded list). Each declared child upserts by
	/// (parent target, parent component, catalog component key) with a NULL vendor
	/// identity -- reconnecting an absent/retired row rather than creating a sibling --
	/// and always re-asserts <see cref="Component.CatalogComponentId"/> from the
	/// declared entry (the expansion IS the linkage authority for these rows; there is
	/// no independent fact to resolve against). A previously-declared child no longer
	/// in <paramref name="declared"/> (catalog change, or the parent losing its link --
	/// callers pass an empty list for an unlinked parent) is marked
	/// <see cref="ComponentLifecycleStates.Absent"/>, never deleted and never left
	/// silently active. No version fact is ever written on a child row -- facts are
	/// inherited live from the parent at match time (<see cref="ComponentFactInheritance"/>).
	/// Runs as a single transaction, same durability contract as
	/// <see cref="UpsertDiscoveredAsync"/>.
	/// </summary>
	Task<CatalogDeclaredChildSyncOutcome> SyncCatalogDeclaredChildrenAsync(
		Guid targetId, Guid parentComponentId, IReadOnlyList<CatalogDeclaredChild> declared, CancellationToken cancellationToken);

	/// <summary>
	/// Admin configured-fact write (docs/api-contract.md <c>PUT /components/{id}</c>:
	/// "configured_fact only ... never lifecycle or identity"). Recomputes
	/// <see cref="Component.FactConflict"/> against any existing discovered fact and
	/// records a <see cref="ComponentObservation"/> with outcome
	/// <see cref="ComponentObservationOutcomes.Conflict"/> when they now disagree,
	/// <see cref="ComponentObservationOutcomes.Recorded"/> otherwise. A null/whitespace
	/// <paramref name="exactVersion"/> clears the configured fact.
	///
	/// Issue #1000: also (re-)resolves <see cref="Component.CatalogComponentId"/> from
	/// the effective exact version -- configured when present, discovered when
	/// configured is absent/cleared, unlinked on a configured/discovered conflict --
	/// the same precedence <see cref="ComponentCapabilityMatcher"/> already applies to
	/// the FACT, reused here for the LINK via the shared
	/// <see cref="CatalogLinkageResolver"/> (never a forked copy of the exact-
	/// match/ambiguity rule), so an Admin-configured version participates in catalog
	/// linkage exactly like a discovered one. Clearing the configured fact honestly
	/// re-resolves the link from whatever remains (a discovered fact, or nothing).
	/// </summary>
	Task<ComponentWriteOutcome> SetConfiguredFactAsync(Guid componentId, string? exactVersion, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #743: Admin-declared ROOT component creation for a target kind that has no
	/// discovery operation (the <c>ssh</c> whole-appliance SRG products -- Photon, the
	/// Aria family, Workspace ONE Access). Discovery materializes the root for
	/// <c>vsphere</c> targets; an <c>ssh</c> target's product is not derivable from its
	/// connection ("generic SSH does not guess a product" -- #743 AC), so the Admin
	/// declares it explicitly by catalog component key and the row is created UNLINKED
	/// (<see cref="Component.CatalogComponentId"/> null, no facts) -- catalog linkage
	/// happens only through the shared configured-fact path
	/// (<see cref="SetConfiguredFactAsync"/>/<see cref="CatalogLinkageResolver"/>) once
	/// an exact version is configured, identical semantics to every other provenance.
	/// Identity binds to migration 0054's no-vendor-identity partial unique index (the
	/// same identity case as a catalog-declared service child, at the root tier):
	/// returns null when a root with this key already exists under the target (the
	/// caller surfaces a 409; the existing row is never mutated).
	///
	/// Issue #1202/#1270: the stored <c>display_name</c> is always <paramref name="catalogComponentKey"/>
	/// itself (version-neutral -- never one arbitrary product version's name) until
	/// linkage supplies a real one; there is no independent display name to accept at
	/// declaration time, so this method takes none.
	/// </summary>
	Task<Guid?> CreateDeclaredRootAsync(Guid targetId, string catalogComponentKey, CancellationToken cancellationToken);

	/// <summary>
	/// Moves every component under any target whose <see cref="Component.ContinuousAbsenceSince"/>
	/// is at least <paramref name="threshold"/> old and is not already retired to
	/// <see cref="ComponentLifecycleStates.Retired"/>. Returns the count retired. Global
	/// Admin-configurable threshold policy (ADR-0023 "initially seven days") is the
	/// caller's concern; this method only applies whatever threshold it is given.
	/// </summary>
	Task<int> RetireContinuouslyAbsentAsync(TimeSpan threshold, CancellationToken cancellationToken);

	/// <summary>Admin-only audited purge (docs/api-contract.md: 409 unless already retired). Historical references outside this table are untouched -- this slice has none yet (plan/run integration is #733/#734).</summary>
	Task<ComponentWriteOutcome> PurgeRetiredAsync(Guid componentId, CancellationToken cancellationToken);

	/// <summary>Immutable observation history for one component, newest first.</summary>
	Task<IReadOnlyList<ComponentObservation>> ListObservationsAsync(Guid componentId, CancellationToken cancellationToken);
}
