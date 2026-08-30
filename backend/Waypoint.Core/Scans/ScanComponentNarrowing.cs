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

	/// <summary>
	/// Issue #1138: the ONE rule deciding whether a component's <c>DisplayName</c> is
	/// safe to hand to the vendor content as a narrowed job's <c>selector_name</c>.
	/// Returns <see langword="null"/> when the name is safe for
	/// <paramref name="selectorKind"/>, or a short human-readable phrase naming the
	/// offending CHARACTER CLASS when it is not (used verbatim in the
	/// <c>unsafe_selector_name</c> plan-skip detail so an operator can see WHY a name
	/// was rejected).
	///
	/// The rule is decided PER SELECTOR KIND, because the vendored
	/// <c>dod-compliance-and-automation</c> content quotes the two kinds differently.
	/// Measured over the vendored content (vSphere 7.0 + 8.0, commit <c>cd6c1f0</c>):
	/// <list type="bullet">
	/// <item><description><c>Get-VM -Name '#{...}'</c> (SINGLE-QUOTED) -- 277 files;
	/// unquoted <c>Get-VM -Name #{...}</c> -- 0 files.</description></item>
	/// <item><description><c>Get-VMHost -Name #{vmhostName}</c> (UNQUOTED) -- 740 files;
	/// quoted -- 6 files.</description></item>
	/// </list>
	///
	/// <b><c>esxi</c> -- strict allow-list <c>[A-Za-z0-9._-]</c>.</b> The ESX baselines
	/// interpolate the value UNQUOTED into a PowerCLI <c>-Name</c> argument, where
	/// <c>;</c> terminates the statement, <c>$(...)</c> executes a subexpression and
	/// whitespace splits the value into more than one argument. Waypoint neither
	/// controls nor can prove the quoting of any given release's profile, so anything
	/// it cannot positively vouch for is refused. ESXi host names are FQDNs anyway, so
	/// the allow-list costs no realistic coverage.
	///
	/// <b><c>vm</c> -- reject only what is hazardous inside a single-quoted string.</b>
	/// The vm baselines interpolate the value into a PowerShell SINGLE-QUOTED literal,
	/// in which <c>`</c> <c>$</c> <c>;</c> <c>|</c> <c>&amp;</c> <c>(</c> <c>)</c>
	/// <c>{</c> <c>}</c> <c>&lt;</c> <c>&gt;</c> <c>"</c> <c>#</c> <c>,</c> <c>=</c>
	/// <c>^</c> <c>!</c> <c>%</c> <c>~</c> and WHITESPACE are all literal. Rejecting
	/// them would omit every VM whose display name contains a space -- the ordinary
	/// shape of a Windows VM name (<c>Windows Server 2022 - test</c>) -- to defend
	/// against a hazard the imported content does not have. So spaces and ordinary
	/// punctuation are ALLOWED for <c>vm</c>; what stays rejected is:
	/// <list type="bullet">
	/// <item><description><c>'</c> -- breaks OUT of the single-quoted literal.</description></item>
	/// <item><description>PowerCLI <b>wildcards</b> (<c>*</c> <c>?</c> <c>[</c> <c>]</c>)
	/// -- <c>-Name</c> is a wildcard-matching parameter regardless of quoting, so a VM
	/// literally named <c>web*</c> resolves to EVERY VM whose name starts with
	/// <c>web</c>: the same silent widening of an explicitly narrowed scope ADR-0023
	/// forbids, and exactly the hazard this issue exists to close.</description></item>
	/// <item><description><b>Control</b> characters -- they do not survive an input
	/// file as themselves.</description></item>
	/// <item><description><b>Non-ASCII</b> characters -- rejected CONSERVATIVELY rather
	/// than because single quoting fails: neither the vendor Ruby's encoding of the
	/// generated input file nor the remote PowerShell host's code page is something
	/// Waypoint can prove round-trips the byte sequence, so a name that would silently
	/// mis-match is refused instead of scanned against the wrong object. Widening this
	/// is tracked with the durable fix in #1137 (declared-input roles).</description></item>
	/// </list>
	///
	/// Any other selector kind falls back to the strict allow-list. A name that is
	/// empty is likewise unsafe for every kind: there is nothing to narrow to.
	/// </summary>
	public static string? DescribeUnsafeSelectorName(string? selectorKind, string? name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "an empty name";
		}

		bool singleQuotedByVendor = string.Equals(selectorKind, CatalogSelectorKinds.Vm, StringComparison.Ordinal);

		foreach (char c in name)
		{
			if (IsSafeSelectorNameCharacter(c, singleQuotedByVendor))
			{
				continue;
			}

			return c switch
			{
				'*' or '?' or '[' or ']' => $"a PowerCLI wildcard character ('{c}')",
				'\'' when singleQuotedByVendor => "a single quote (')",
				'`' or '$' or ';' or '|' or '&' or '(' or ')' or '{' or '}' or '<' or '>'
					or '\'' or '"' or '#' or ',' or '=' or '^' or '!' or '%' or '~' =>
					$"a PowerShell metacharacter ('{c}')",
				_ when char.IsWhiteSpace(c) => "a whitespace character",
				_ when char.IsControl(c) => "a control character",
				_ when c > MaxAsciiPrintable => "a non-ASCII character",
				_ => $"a character outside the safe set [A-Za-z0-9._-] ('{c}')",
			};
		}

		return null;
	}

	/// <summary>
	/// True when <paramref name="name"/> is safe to pass as a narrowed job's
	/// <c>selector_name</c> for <paramref name="selectorKind"/>. The sole gate; see
	/// <see cref="DescribeUnsafeSelectorName"/> for the per-kind rule table and its
	/// rationale.
	/// </summary>
	public static bool IsSafeSelectorName(string? selectorKind, string? name) =>
		DescribeUnsafeSelectorName(selectorKind, name) is null;

	/// <summary>Highest printable US-ASCII code point (<c>~</c>).</summary>
	private const char MaxAsciiPrintable = (char)0x7E;

	/// <summary>
	/// The per-kind character table. <paramref name="singleQuotedByVendor"/> is true
	/// only for the kinds the vendor content wraps in a PowerShell single-quoted
	/// literal (today: <c>vm</c>), where printable ASCII other than <c>'</c> and the
	/// PowerCLI wildcards is literal and therefore safe.
	/// </summary>
	private static bool IsSafeSelectorNameCharacter(char c, bool singleQuotedByVendor)
	{
		if (singleQuotedByVendor)
		{
			return c is >= ' ' and <= MaxAsciiPrintable
				&& c is not ('\'' or '*' or '?' or '[' or ']');
		}

		return IsStrictSelectorNameCharacter(c);
	}

	private static bool IsStrictSelectorNameCharacter(char c) =>
		c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
