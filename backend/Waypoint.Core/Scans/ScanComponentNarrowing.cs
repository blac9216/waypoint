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

namespace Waypoint.Core.Scans;

/// <summary>
/// Issue #737 item-4 (epic #726 Wave 2, ADR-0024), extended by #741/#743 (Wave 3 SSH
/// family) and #742 (Wave 3 NSX family, the epic's final transport): the single,
/// shared rule deciding whether one accepted plan item's <c>transport</c>/
/// <c>selector_kind</c> can be executed as a component-NARROWED scan (its own job
/// executes only that component's leaf profile) or must fall back to a whole-target
/// scan.
///
/// This is the load-bearing invariant the round-1 review demanded: a <c>target_scope</c>
/// run must never fan out N sibling jobs that each re-scan the whole target. Both the
/// fan-out side (<c>RunCreationService</c>, which emits one job per narrowable item and
/// exactly ONE collapsed whole-target job for the un-narrowable remainder) and the
/// execution side (<c>ScanJobHandler</c>, which narrows the InSpec invocation to the
/// item's object when this returns true) consult the SAME classifier, so the two can
/// never disagree about which items are narrowed.
///
/// Narrowing remainder, stated per transport (what today's runner can NOT yet narrow,
/// so those items collapse to one whole-target job):
/// <list type="bullet">
/// <item><description><c>vmware</c> / <c>vcenter</c>, <c>esxi</c>, <c>vm</c> -- NARROWED
///   (InSpec vmware train scoped to that vCenter / ESXi host / VM via input file).</description></item>
/// <item><description><c>ssh</c> / <c>service</c> -- NARROWED (issue #741): a named VCSA
///   OS-level service (Envoy, PostgreSQL, VAMI, STS, UI, EAM, Photon, ...), per
///   docs/compliance-parity.md's "ssh / named VCSA service" rows. Each named service is
///   its own leaf profile/benchmark with its own job, executed over the owning
///   appliance's ssh transport with its own attribution -- there is no vmware-train
///   object selector for these; "narrowed" means one job per named service rather than
///   one whole-appliance scan covering every service at once.</description></item>
/// <item><description><c>ssh</c> / <c>target</c> -- NARROWED (issue #743): a whole-
///   appliance SSH product (Photon OS, Aria Operations/Automation/Suite Lifecycle,
///   Workspace ONE Access, ...), per docs/compliance-parity.md's "ssh / target" rows. The
///   component IS the appliance, so narrowing here means one job per catalog component
///   on that transport (never collapsing two independent appliance products behind one
///   representative job).</description></item>
/// <item><description><c>nsx-api</c> / <c>service</c> -- NARROWED (issue #742, the
///   epic's final Wave 3 transport): a named NSX functional component (Manager,
///   distributed firewall, tier-0/tier-1 firewall/router, and any newer set the
///   activated catalog/release adds), per docs/compliance-parity.md's "NSX ... named
///   function" rows. Each component is its own leaf profile/benchmark (or SRG closure
///   for the VCF 9.x NSX baselines) with its own job, executed over the manager's
///   nsx-api transport with its own attribution -- there is no whole-Manager object
///   selector below the component itself; "narrowed" means one job per named
///   component rather than one whole-Manager scan covering every function at once.
///   This shrinks #892's residual to the <c>vcf-api</c> row only, restated
///   below.</description></item>
/// <item><description><c>vcf-api</c> -- NOT narrowed: no runner path consumes it yet;
///   collapses (residual tracked by #892 -- the sole remaining un-narrowable
///   transport once this issue lands).</description></item>
/// </list>
/// </summary>
public static class ScanComponentNarrowing
{
	/// <summary>
	/// True when an item with this <paramref name="transport"/> and
	/// <paramref name="selectorKind"/> can be executed as a component-narrowed scan.
	/// Today: the vSphere-family object selectors on the vmware transport (#737), the
	/// ssh-family named-service and whole-appliance selectors (#741/#743), and the
	/// nsx-api named-function selector (#742).
	/// </summary>
	public static bool CanNarrow(string? transport, string? selectorKind) =>
		(string.Equals(transport, CatalogTransports.VMware, StringComparison.Ordinal)
			&& selectorKind is CatalogSelectorKinds.VCenter or CatalogSelectorKinds.Esxi or CatalogSelectorKinds.Vm)
		|| (string.Equals(transport, CatalogTransports.Ssh, StringComparison.Ordinal)
			&& selectorKind is CatalogSelectorKinds.Service or CatalogSelectorKinds.Target)
		|| (string.Equals(transport, CatalogTransports.NsxApi, StringComparison.Ordinal)
			&& string.Equals(selectorKind, CatalogSelectorKinds.Service, StringComparison.Ordinal));

	/// <summary>Convenience overload for a resolved <see cref="ScanPlanItem"/>.</summary>
	public static bool CanNarrow(ScanPlanItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		return CanNarrow(item.Transport, item.SelectorKind);
	}
}
