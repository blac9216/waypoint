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
/// The closed set of component lifecycle states (migration 0054's
/// <c>components_lifecycle_check</c>; ADR-0023 "Refresh and lifecycle"). A component
/// starts and stays <see cref="Active"/> while a successful discovery boundary keeps
/// observing it. When a successful boundary no longer observes a previously-active
/// component it becomes <see cref="Absent"/> (identity and configuration retained,
/// never deleted); continuous absence past a global Admin threshold (default seven
/// days) moves it to <see cref="Retired"/>, which leaves normal active selection
/// (excluded from <c>all</c>-scope expansion) but remains listed until an explicit
/// Admin purge. Rediscovery of the same vendor identity at any point before purge
/// reconnects the row back to <see cref="Active"/> rather than creating a sibling.
/// </summary>
public static class ComponentLifecycleStates
{
	public const string Active = "active";
	public const string Absent = "absent";
	public const string Retired = "retired";

	public static readonly IReadOnlyCollection<string> All = [Active, Absent, Retired];

	public static bool IsValid(string? lifecycle) => lifecycle is not null && All.Contains(lifecycle);
}

/// <summary>
/// The closed set of <see cref="ComponentObservation"/> sources (migration 0054's
/// <c>component_observations_source_check</c>): a <see cref="Discovered"/> fact comes
/// from a discovery refresh boundary; a <see cref="Configured"/> fact comes from an
/// explicit Admin <c>PUT /components/{id}</c> (docs/api-contract.md). ADR-0023: "Exact
/// product version is mandatory... [Waypoint] never guesses a winner" -- the two
/// sources are independent and both retained rather than one overwriting the other.
/// </summary>
public static class ComponentObservationSources
{
	public const string Configured = "configured";
	public const string Discovered = "discovered";

	public static readonly IReadOnlyCollection<string> All = [Configured, Discovered];

	public static bool IsValid(string? source) => source is not null && All.Contains(source);
}

/// <summary>
/// The closed set of <see cref="ComponentObservation"/> outcomes (migration 0054's
/// <c>component_observations_outcome_check</c>). <see cref="Recorded"/> is a plain
/// fact update; <see cref="Conflict"/> records that this observation disagreed with
/// the other source's already-recorded fact (mirrors <see cref="Component.FactConflict"/>
/// at the moment it was raised); <see cref="Absent"/> records a successful discovery
/// boundary that did not observe the component at all (the event that starts or
/// continues <see cref="Component.ContinuousAbsenceSince"/>).
/// </summary>
public static class ComponentObservationOutcomes
{
	public const string Recorded = "recorded";
	public const string Conflict = "conflict";
	public const string Absent = "absent";

	public static readonly IReadOnlyCollection<string> All = [Recorded, Conflict, Absent];

	public static bool IsValid(string? outcome) => outcome is not null && All.Contains(outcome);
}

/// <summary>
/// One independent, timestamped product/version (or other catalog-declared capability)
/// observation (migration 0054's <c>configured_fact</c>/<c>discovered_fact</c> JSONB
/// columns). <see cref="ExactVersion"/> is the mandatory exact value ADR-0023 requires
/// ("Exact product version is mandatory") -- never a range or family key.
/// <see cref="RawEvidenceReference"/> is an opaque pointer to the raw normalized
/// evidence this fact was derived from (e.g. a discovery-refresh observation id),
/// never the raw evidence itself, matching the rest of this codebase's "reference, not
/// embed" convention for anything that could grow unbounded or carry sensitive detail.
/// <see cref="Build"/> (issue #1081) is the observed raw build number alongside the
/// mandatory <see cref="ExactVersion"/> -- docs/compliance-parity.md: "hosts store
/// exactly two facts about their own version: the full observed product version and
/// the build number." Optional/nullable: a discovery pass that could not observe a
/// build (or an Admin-configured fact, which never carries one) leaves it honestly
/// absent rather than guessed.
/// </summary>
public sealed record ComponentFact(string ExactVersion, DateTimeOffset ObservedAt, string? RawEvidenceReference, string? Build = null);

/// <summary>
/// A stable compliance endpoint/component (migration 0054's <c>components</c> table;
/// ADR-0023). Distinguishes a top-level connection <c>Target</c> (a vCenter/NSX
/// Manager/SSH connection boundary) from the concrete executable subjects beneath it:
/// a discovered ESXi host, a discovered VM, a named VCSA sub-service, or a
/// whole-appliance SSH component. Identity is
/// <c>(ParentTargetId, CatalogComponentKey, VendorIdentity)</c> when
/// <see cref="VendorIdentity"/> is non-null (an independently discoverable upstream
/// object), or <c>(ParentTargetId, ParentComponentId, CatalogComponentKey)</c> when it
/// is null (a catalog-declared component with no independent upstream object, e.g. a
/// named VCSA service) -- never <see cref="DisplayName"/>, address, or tree position
/// (ADR-0023 "Risks": "Do not use display name as identity").
/// </summary>
public sealed record Component(
	Guid Id,
	Guid ParentTargetId,
	Guid? ParentComponentId,
	Guid? CatalogComponentId,
	string CatalogComponentKey,
	string? VendorIdentity,
	string DisplayName,
	string Lifecycle,
	ComponentFact? ConfiguredFact,
	ComponentFact? DiscoveredFact,
	bool FactConflict,
	DateTimeOffset FirstSeenAt,
	DateTimeOffset LastSeenAt,
	DateTimeOffset? ContinuousAbsenceSince,
	DateTimeOffset? RetiredAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>
/// One discovered or configured component fact, as reported by a discovery pass or an
/// Admin configuration write, before it is reconciled/persisted -- the upsert unit
/// <see cref="Waypoint.Core.Components.IComponentRepository.UpsertDiscoveredAsync"/>
/// consumes. Mirrors <see cref="Waypoint.Core.Discovery.DiscoveredInventoryItem"/>'s
/// shape/intent one layer up (component identity instead of a flat inventory row).
/// </summary>
public sealed record DiscoveredComponent(
	string CatalogComponentKey,
	string? VendorIdentity,
	string DisplayName,
	string? ParentVendorIdentity,
	Guid? CatalogComponentId,
	string? ExactVersion,
	string? Build = null);

/// <summary>Outcome of one discovery-boundary reconciliation pass (mirrors <see cref="Waypoint.Core.Discovery.InventoryUpsertOutcome"/>).</summary>
public sealed record ComponentUpsertOutcome(int Upserted, int MarkedAbsent, int Reconnected);

/// <summary>
/// One immutable provenance row (migration 0054's <c>component_observations</c> table;
/// docs/api-contract.md <c>/components/{id}/observations</c>).
/// </summary>
public sealed record ComponentObservation(
	Guid Id,
	Guid ComponentId,
	string Source,
	ComponentFact ObservedFact,
	string Outcome,
	DateTimeOffset ObservedAt);

/// <summary>Admin configured-fact update request (docs/api-contract.md <c>PUT /components/{id}</c>: "configured_fact only ... never lifecycle or identity").</summary>
public sealed record ComponentConfiguredFactUpdateRequest(string ExactVersion);

public enum ComponentWriteOutcome
{
	Ok,
	NotFound,
}

/// <summary>
/// The result of matching one <see cref="Component"/> against the catalog's execution
/// profiles for its resolved product version (issue #732 AC "capability matching
/// against catalog selectors and product/build/version facts ... exact reasons for
/// unsupported product/build/component/transport combinations"). Fails closed: a
/// component with no exact catalog product-version entry, no matching catalog
/// component, or no active-compatible execution profile is
/// <see cref="IsCompatible"/> == false with an explicit <see cref="IncompatibilityReasons"/>
/// list rather than silently empty results.
/// </summary>
public sealed record ComponentCapabilityMatch(
	Guid ComponentId,
	bool IsCompatible,
	IReadOnlyList<Waypoint.Core.ComplianceContent.CatalogExecutionProfileDetail> CompatibleProfiles,
	IReadOnlyList<string> IncompatibilityReasons);
