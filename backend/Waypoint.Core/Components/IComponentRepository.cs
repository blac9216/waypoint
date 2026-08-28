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
	/// <summary>All non-retired-by-default components for a target; pass <paramref name="includeRetired"/> to include retired rows too (Configuration-screen visibility, docs/api-contract.md <c>/targets/{id}/components</c>: "every known component ... regardless of lifecycle").</summary>
	Task<IReadOnlyList<Component>> ListForTargetAsync(Guid targetId, bool includeRetired, CancellationToken cancellationToken);

	/// <summary>Single component by id, or null when unknown.</summary>
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
