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

using Waypoint.Core.ComplianceContent;

namespace Waypoint.Core.Components;

/// <summary>
/// One catalog-declared service component to materialize as an inventory child row
/// beneath a linked root connection component (issue #741, ADR-0023 "For a
/// catalog-declared service with no independent upstream object, parent identity plus
/// catalog component key is authoritative"). Identity-bearing fields only -- the child
/// row's version facts are never copied here; they are inherited live from the parent
/// at capability-match time (<see cref="ComponentFactInheritance"/>).
/// </summary>
public sealed record CatalogDeclaredChild(Guid CatalogComponentId, string CatalogComponentKey, string DisplayName);

/// <summary>Counts from one <see cref="IComponentRepository.SyncCatalogDeclaredChildrenAsync"/> reconciliation.</summary>
public sealed record CatalogDeclaredChildSyncOutcome(int Upserted, int Reconnected, int MarkedAbsent);

/// <summary>
/// Issue #741 (epic #726, ADR-0023): the pure selection rule for which of a linked
/// catalog product version's components are CATALOG-DECLARED SERVICE components of the
/// owning appliance -- the named VCSA sub-services (EAM, Lookup, PostgreSQL, VAMI,
/// Envoy, ...) a vSphere release maps to separate leaf profiles. These components have
/// no independent upstream object a discovery boundary could ever enumerate (no MoRef,
/// no address), so their inventory existence is derived from the catalog release the
/// appliance's own root component resolved to -- "the catalog release determines the
/// exact VCSA component list" (issue #741 AC), never a hard-coded service list.
///
/// Selection is by the CLOSED transport/selector vocabulary alone
/// (docs/compliance-parity.md: the "`ssh` / named VCSA service" rows are exactly the
/// <see cref="CatalogTransports.Ssh"/> + <see cref="CatalogSelectorKinds.Service"/>
/// shape), never by product key or a literal service-name list -- deliberately
/// product-neutral so the VCF 9.x "`ssh` / named service" family (issue #743's
/// generalization seam) reuses this same rule when its execution path lands. Other
/// service-selector transports (<c>nsx-api</c>, <c>vcf-api</c>) are exchange-boundary
/// components of their own target kinds, not OS-level services of this appliance, and
/// are excluded here.
///
/// Pure domain logic with no I/O, same convention as
/// <see cref="ComponentCapabilityMatcher"/>: the caller supplies the already-loaded
/// catalog component set for the linked product version.
/// </summary>
public static class CatalogDeclaredServiceComponents
{
	/// <summary>
	/// Selects the catalog-declared ssh/service components of
	/// <paramref name="productVersionComponents"/> (the linked product version's full
	/// component set), excluding <paramref name="linkedCatalogComponentId"/> itself
	/// (defensive -- the linking root is a connection-boundary component, never its own
	/// service child). Deterministic: ordinally ordered by component key; a duplicate
	/// key (should not exist -- catalog natural keys forbid it) keeps the first and
	/// drops the rest rather than producing two children with one identity.
	/// </summary>
	public static IReadOnlyList<CatalogDeclaredChild> SelectDeclaredServiceChildren(
		IReadOnlyList<CatalogComponent> productVersionComponents, Guid linkedCatalogComponentId)
	{
		ArgumentNullException.ThrowIfNull(productVersionComponents);

		List<CatalogDeclaredChild> declared = [];
		HashSet<string> seenKeys = new(StringComparer.Ordinal);
		foreach (CatalogComponent component in productVersionComponents
			.Where(c => c.Id != linkedCatalogComponentId
				&& string.Equals(c.Transport, CatalogTransports.Ssh, StringComparison.Ordinal)
				&& string.Equals(c.SelectorKind, CatalogSelectorKinds.Service, StringComparison.Ordinal))
			.OrderBy(c => c.ComponentKey, StringComparer.Ordinal))
		{
			if (seenKeys.Add(component.ComponentKey))
			{
				declared.Add(new CatalogDeclaredChild(component.Id, component.ComponentKey, component.DisplayName));
			}
		}

		return declared;
	}
}

/// <summary>
/// Issue #741: the shared fact-inheritance rule for a catalog-declared child component.
/// A derived service child stores NO version fact of its own -- ADR-0023 mandates
/// exactly two fact provenances (configured/discovered) and the appliance's version IS
/// the service's version, so the child inherits the PARENT appliance component's facts
/// live at every evaluation rather than persisting a third, derived copy that could
/// drift. Both capability evaluation call sites
/// (<c>ScopeResolutionService.EvaluateComponentAsync</c> and
/// <c>GET /components/{id}/capability</c>) consult this one rule so they can never
/// disagree about which facts a child is matched against.
/// </summary>
public static class ComponentFactInheritance
{
	/// <summary>
	/// True when <paramref name="component"/> is a catalog-declared child: no
	/// independent vendor identity AND parented beneath another component -- the exact
	/// identity case ADR-0023 designates "parent identity plus catalog component key is
	/// authoritative". A top-level root (both null) and a discovered object
	/// (vendor identity non-null) are never fact-inheriting.
	/// </summary>
	public static bool IsCatalogDeclaredChild(Component component)
	{
		ArgumentNullException.ThrowIfNull(component);
		return component.VendorIdentity is null && component.ParentComponentId is not null;
	}

	/// <summary>
	/// Returns <paramref name="child"/> with the parent's version facts (and the
	/// parent's fact-conflict flag -- a conflicted appliance version honestly conflicts
	/// every service derived from it) substituted for capability evaluation. Identity
	/// fields (id, keys, lifecycle, linkage) stay the child's own.
	/// </summary>
	public static Component WithInheritedFacts(Component child, Component parent)
	{
		ArgumentNullException.ThrowIfNull(child);
		ArgumentNullException.ThrowIfNull(parent);
		return child with
		{
			ConfiguredFact = parent.ConfiguredFact,
			DiscoveredFact = parent.DiscoveredFact,
			FactConflict = parent.FactConflict,
		};
	}
}
