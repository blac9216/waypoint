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

using Waypoint.Core.Components;

namespace Waypoint.Api.Contracts;

/// <summary>One independent, timestamped product-version observation on the wire.</summary>
public sealed record ComponentFactResponse(string ExactVersion, DateTimeOffset ObservedAt, string? RawEvidenceReference)
{
	public static ComponentFactResponse FromDomain(ComponentFact fact)
	{
		ArgumentNullException.ThrowIfNull(fact);
		return new ComponentFactResponse(fact.ExactVersion, fact.ObservedAt, fact.RawEvidenceReference);
	}
}

/// <summary>
/// docs/api-contract.md's planned <c>/targets/{id}/components</c> row shape: stable
/// identity, lifecycle, independent configured/discovered facts, and the explicit
/// <see cref="FactConflict"/> readiness signal -- never a silently resolved value.
/// </summary>
public sealed record ComponentResponse(
	string Id,
	string ParentTargetId,
	string? ParentComponentId,
	string? CatalogComponentId,
	string CatalogComponentKey,
	string? VendorIdentity,
	string DisplayName,
	string Lifecycle,
	ComponentFactResponse? ConfiguredFact,
	ComponentFactResponse? DiscoveredFact,
	bool FactConflict,
	DateTimeOffset FirstSeenAt,
	DateTimeOffset LastSeenAt,
	DateTimeOffset? ContinuousAbsenceSince,
	DateTimeOffset? RetiredAt)
{
	public static ComponentResponse FromDomain(Component component)
	{
		ArgumentNullException.ThrowIfNull(component);
		return new ComponentResponse(
			component.Id.ToString(),
			component.ParentTargetId.ToString(),
			component.ParentComponentId?.ToString(),
			component.CatalogComponentId?.ToString(),
			component.CatalogComponentKey,
			component.VendorIdentity,
			component.DisplayName,
			component.Lifecycle,
			component.ConfiguredFact is null ? null : ComponentFactResponse.FromDomain(component.ConfiguredFact),
			component.DiscoveredFact is null ? null : ComponentFactResponse.FromDomain(component.DiscoveredFact),
			component.FactConflict,
			component.FirstSeenAt,
			component.LastSeenAt,
			component.ContinuousAbsenceSince,
			component.RetiredAt);
	}
}

/// <summary>Admin configured-fact write body (docs/api-contract.md: "configured_fact only").</summary>
public sealed record ComponentConfiguredFactBody(string? ExactVersion);

/// <summary>
/// Issue #743: Admin declared-root creation body (<c>POST /targets/{id}/components</c>)
/// for target kinds with no discovery operation (today: <c>ssh</c> whole-appliance SRG
/// products). <see cref="CatalogComponentKey"/> is the EXPLICIT product selection --
/// generic SSH never guesses a product; it must name a top-level catalog component in
/// the closed <c>ssh</c>/<c>target</c> shape. <see cref="ExactVersion"/> optionally
/// configures the exact product version in the same write, flowing through the shared
/// configured-fact/linkage path (<c>PUT /components/{id}</c> semantics, issue #1000).
/// </summary>
public sealed record ComponentDeclareRootBody(string? CatalogComponentKey, string? ExactVersion);

/// <summary>One immutable observation-history row (docs/api-contract.md <c>/components/{id}/observations</c>).</summary>
public sealed record ComponentObservationResponse(string Id, string ComponentId, string Source, ComponentFactResponse ObservedFact, string Outcome, DateTimeOffset ObservedAt)
{
	public static ComponentObservationResponse FromDomain(ComponentObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);
		return new ComponentObservationResponse(
			observation.Id.ToString(),
			observation.ComponentId.ToString(),
			observation.Source,
			ComponentFactResponse.FromDomain(observation.ObservedFact),
			observation.Outcome,
			observation.ObservedAt);
	}
}

/// <summary>
/// Catalog compatibility for one component (issue #732 AC: "compatible profiles/
/// components and incompatibility reasons through API queries"). Fails closed:
/// <see cref="IsCompatible"/> false always carries at least one reason.
/// </summary>
public sealed record ComponentCapabilityResponse(string ComponentId, bool IsCompatible, IReadOnlyList<string> CompatibleExecutionProfileIds, IReadOnlyList<string> IncompatibilityReasons)
{
	public static ComponentCapabilityResponse FromDomain(ComponentCapabilityMatch match)
	{
		ArgumentNullException.ThrowIfNull(match);
		return new ComponentCapabilityResponse(
			match.ComponentId.ToString(),
			match.IsCompatible,
			match.CompatibleProfiles.Select(p => p.ExecutionProfile.Id.ToString()).ToArray(),
			match.IncompatibilityReasons);
	}
}
