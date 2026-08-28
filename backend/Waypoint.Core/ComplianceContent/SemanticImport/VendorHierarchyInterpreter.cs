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

using System.Security.Cryptography;
using System.Text;

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>One rejected <see cref="VendorContentEntry"/> with an actionable reason (issue #729 AC "quarantined with actionable diagnostics rather than guessed").</summary>
public sealed record SemanticImportRejection(string ProfileKey, string Reason);

/// <summary>The interpreter's output for one content root: classified candidates plus quarantined/rejected entries.</summary>
public sealed record VendorHierarchyInterpretation(
	IReadOnlyList<SemanticCandidate> Candidates,
	IReadOnlyList<SemanticImportRejection> Rejections);

/// <summary>
/// Interprets the vendor compliance-content repository hierarchy (docs/compliance-parity.md's
/// documented vSphere/VCSA/NSX/Photon/Aria/vIDM layouts) into normalized
/// <see cref="SemanticCandidate"/> entries -- issue #729's replacement for the raw
/// recursive <c>inspec.yml</c> walk in <c>WaypointComplianceContent.psm1</c>.
///
/// This is a closed, data-driven family table, not a general path-inference engine
/// (ADR-0013: new products/components are data, never inferred code). Every family this
/// recognizes matches one documented row of docs/compliance-parity.md's provenance
/// matrix; a profile whose path does not match ANY family's shape is quarantined
/// (<see cref="SemanticImportRejection"/>), never guessed into the nearest-looking
/// family. Vocabulary-closed-set validation (does the resulting transport/selector/kind
/// actually belong to <see cref="CatalogVocabulary"/>) is a separate, later pass
/// (<see cref="SemanticImportReconciler"/>) -- this class only proves "the path/manifest
/// shape matches a documented family", not "the resulting values are catalog-legal".
/// </summary>
public static class VendorHierarchyInterpreter
{
	// Family layouts, one row per docs/compliance-parity.md provenance-matrix entry.
	// segments[0] is always the vendor-family directory name (case-insensitive); the
	// interpreter never accepts an unlisted first segment.
	// Issue #959 (epic #726): upstream `master` now nests the 9.x vSphere/vCenter/ESXi/VM
	// baselines under a consolidated `vcf/<major>.x/...` tree instead of a top-level
	// `vsphere/9-0/...` tree. This is the SAME vsphere product family and
	// ObjectKindSplit shape docs/compliance-parity.md already documents -- only the
	// vendor-repository directory literal differs, so `vcf` maps to the `vsphere`
	// VendorFamily.Name (not a new product family) rather than inventing a "vcf"
	// product. Nothing else changes: the vcf/ tree still fails closed for any layout
	// that is not this exact shape.
	private static readonly Dictionary<string, VendorFamily> Families = new Dictionary<string, VendorFamily>(StringComparer.OrdinalIgnoreCase)
	{
		["vsphere"] = new VendorFamily("vsphere", VendorFamilyShape.ObjectKindSplit),
		// Issue #1079 (epic #726, live validation round 11): a real content pull proved
		// upstream `master`'s `vcf/9.x` tree is NOT the flat ObjectKindSplit shape this
		// row previously claimed -- it inserts an extra grouping segment
		// (`vsphere/`, `nsx/`) between the baseline directory and the object-kind/
		// function leaf, spreads content across several sibling
		// `vmware-cloud-foundation-*-stig-baseline` directories, and names the ESXi
		// leaf `esx`, not `esxi`. Every single vcf/9.x profile quarantined under the
		// old row. See VendorFamilyShape.VcfGrouped / BuildVcfGrouped below for the
		// corrected shape.
		["vcf"] = new VendorFamily("vcf", VendorFamilyShape.VcfGrouped),
		// Issue #1064 (owner decision, epic #726): VCSA services (EAM, Lookup,
		// PostgreSQL, VAMI, ...) are implied subcomponents of every vCenter appliance,
		// defined by the benchmarks and scanned over the parent's SSH credential -- so
		// the `vcsa/` directory literal maps to the `vsphere` VendorFamily.Name (the
		// promoted catalog product), exactly as `vcf` -> `vsphere` above. Before this
		// fix the literal invented its own `vcsa` product, whose profiles derived an
		// EMPTY credential-requirement set (CredentialRequirementDerivation has no
		// `vcsa` row) and were invisible to #741's catalog-declared service expansion
		// (which only sees components of the linked vSphere product version). The shape
		// is unchanged: named-service leaves, `ssh` transport.
		["vcsa"] = new VendorFamily("vsphere", VendorFamilyShape.NamedServiceSplit),
		["nsx"] = new VendorFamily("nsx", VendorFamilyShape.NamedFunctionSplit),
		["photon"] = new VendorFamily("photon", VendorFamilyShape.WholeAppliance),
		["aria-operations"] = new VendorFamily("aria-operations", VendorFamilyShape.WholeAppliance),
		["aria-automation"] = new VendorFamily("aria-automation", VendorFamilyShape.WholeAppliance),
		["aria-suite-lifecycle"] = new VendorFamily("aria-suite-lifecycle", VendorFamilyShape.WholeAppliance),
		["vidm"] = new VendorFamily("vidm", VendorFamilyShape.WholeAppliance),
	};

	private static readonly Dictionary<string, string> ObjectKindSelectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["vcenter"] = CatalogSelectorKinds.VCenter,
		["esxi"] = CatalogSelectorKinds.Esxi,
		["vm"] = CatalogSelectorKinds.Vm,
	};

	// Issue #959: the current upstream `vsphere/7.0` and `vsphere/8.0` trees split by
	// object kind BEFORE the `inspec` segment -- `vsphere/<version>/<release>/
	// <object-kind>/inspec/<baseline>` -- rather than the documented
	// `<family>/<version>/<release>/inspec/<baseline>` shape. Only these two literal
	// directory names are recognized as that inserted object-kind segment (mirroring
	// vSphere's own object-kind vocabulary); anything else at that position is still a
	// near-miss and is quarantined, never guessed.
	private static readonly HashSet<string> ObjectKindBeforeInspecSegments =
		new(StringComparer.OrdinalIgnoreCase) { "vcsa", "vsphere" };

	// Issue #1079: the real `vcf/9.x/<release>/inspec/<baseline>/` tree's object-kind
	// and named-function leaves both sit one segment BELOW the baseline directory,
	// under a closed grouping-segment vocabulary -- `vsphere/[vcenter|esx|vm]` and
	// `nsx/<function-name>`. `esx` (not `esxi`) is the vendor's own ESXi leaf literal;
	// it normalizes to the same `esxi` component key/selector every other vSphere
	// object-kind row already uses.
	private static readonly Dictionary<string, string> VcfGroupedVSphereSelectorKinds =
		new(StringComparer.OrdinalIgnoreCase)
		{
			["vcenter"] = CatalogSelectorKinds.VCenter,
			["esx"] = CatalogSelectorKinds.Esxi,
			["vm"] = CatalogSelectorKinds.Vm,
		};

	// Issue #1079: named VCF-native service leaves that sit directly under a
	// `vmware-cloud-foundation-*-stig-baseline` directory (no grouping segment). Two
	// disjoint, closed leaf-name sets distinguish transport, matching each leaf's own
	// real inspec.yml inputs: the six API/token-authenticated application profiles
	// (hostname/apitoken/sessionToken/url inputs, no ssh-oriented input) under the
	// umbrella `vmware-cloud-foundation-stig-baseline` use `vcf-api`
	// (docs/compliance-parity.md "VCF `9.x` ... vcf-api / named service" row); every
	// other named VCF service leaf (SDDC Manager nginx/PostgreSQL, Operations
	// httpd/PostgreSQL, Operations HCX httpd, Operations Networks nginx-platform, ...,
	// each under its own distinctly-named baseline directory) is a local system service
	// reached over `ssh` (the sibling "ssh / named service" row). Neither name overlaps
	// the other set -- this is a closed, documented two-way split, never a guess.
	private static readonly HashSet<string> VcfApiNamedServiceLeaves = new(StringComparer.OrdinalIgnoreCase)
	{
		"application", "automation", "operations", "opshcx", "opsnet", "sddcmgr",
	};

	/// <summary>
	/// Interprets every discovered <paramref name="entries"/> against the documented
	/// family table. Ordering of both output lists is deterministic (profile key
	/// ordinal order) regardless of filesystem enumeration order -- issue #729 AC
	/// "duplicate leaf names remain distinct and deterministic".
	/// </summary>
	public static VendorHierarchyInterpretation Interpret(IReadOnlyList<VendorContentEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		List<SemanticCandidate> candidates = [];
		List<SemanticImportRejection> rejections = [];

		foreach (VendorContentEntry entry in entries.OrderBy(e => e.ProfileKey, StringComparer.Ordinal))
		{
			// Per-entry containment (epic #726 invariant "one failure never stops
			// siblings"): a single malformed/near-miss directory must never abort the
			// whole import. Any UNEXPECTED exception while interpreting one entry
			// quarantines THAT entry with the exception type/message as its diagnostic
			// and continues with its siblings. This is deliberately scoped per-entry, not
			// wrapped around the whole loop, so a genuine systemic bug still surfaces as a
			// wall of identical quarantine reasons rather than being silently swallowed.
			try
			{
				InterpretOne(entry, candidates, rejections);
			}
			catch (Exception ex)
			{
				string profileKey = entry?.ProfileKey ?? "(null entry)";
				rejections.Add(new SemanticImportRejection(profileKey,
					$"unexpected error interpreting profile path (quarantined so sibling profiles still import): {ex.GetType().Name}: {ex.Message}"));
			}
		}

		return new VendorHierarchyInterpretation(
			[.. candidates.OrderBy(c => c.ProfileKey, StringComparer.Ordinal)],
			[.. rejections.OrderBy(r => r.ProfileKey, StringComparer.Ordinal)]);
	}

	private static void InterpretOne(VendorContentEntry entry, List<SemanticCandidate> candidates, List<SemanticImportRejection> rejections)
	{
		string[] segments = entry.ProfileKey.Split('/', StringSplitOptions.RemoveEmptyEntries);

		// Every documented family layout is <family>/<version>/<release>/inspec/<baseline>/[leaf...]
		// -- the baseline profile directory (segments[4]) is mandatory for EVERY family
		// (whole-appliance profiles live AT the baseline dir; split families add one more
		// leaf segment after it). A path that stops at or before the mandatory baseline
		// directory (< 5 segments) is a near-miss, not a leaf: it is quarantined with a
		// diagnostic naming the missing structural level, never sliced blindly. This is
		// the fix for the crash class where a 4-segment path
		// (<family>/<version>/<release-stig>/inspec, no baseline dir) passed a too-loose
		// "< 4" guard and then threw at segments[5..].
		const int MinimumSegments = 5;
		if (segments.Length < MinimumSegments)
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey,
				$"profile path has {segments.Length} segment(s); every documented family layout requires at least {MinimumSegments} " +
				"(<family>/<version>/<release>/inspec/<baseline-directory>[/leaf]) -- this near-miss is missing the baseline profile directory and is quarantined, never guessed"));
			return;
		}

		if (!Families.TryGetValue(segments[0], out VendorFamily? family))
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey,
				$"'{segments[0]}' is not a recognized vendor family directory; unknown layouts are quarantined, never guessed"));
			return;
		}

		string productVersionKey = segments[1];
		if (string.IsNullOrWhiteSpace(productVersionKey))
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey, "missing product-version path segment"));
			return;
		}

		(string? kind, string? releaseKey) = ParseReleaseSegment(segments[2]);
		if (kind is null)
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey,
				$"release segment '{segments[2]}' does not declare a recognized -stig/-srg suffix (STIG and SRG are distinct first-class kinds, never inferred)"));
			return;
		}

		// Issue #959: the vsphere object-kind-split trees (the `vsphere` and consolidated
		// `vcf` directory literals) additionally accept an object-kind segment
		// (`vcsa`/`vsphere`) INSERTED before `inspec` --
		// <family>/<version>/<release>/<object-kind>/inspec/<baseline>[/leaf] -- one
		// segment deeper than every other documented family. This is still a single
		// documented, closed shape: an unrecognized segment at that position is a
		// near-miss and is quarantined below, never guessed into this shape. The guard
		// keys on the ObjectKindSplit SHAPE, not the family name, because issue #1064
		// also maps the top-level `vcsa` literal to family name `vsphere` -- that tree's
		// documented layout has no inserted object-kind segment, so it must not gain
		// one here.
		// Issue #1064: the two inserted object kinds carry DIFFERENT leaf vocabularies.
		// The `vsphere` subtree holds the vcenter/esxi/vm object-kind leaves (`vmware`
		// transport); the `vcsa` subtree holds named VCSA service leaves (EAM, Lookup,
		// PostgreSQL, ..., `ssh` transport) -- the same named-service-split shape as the
		// top-level `vcsa/` tree, and previously quarantined wholesale because its
		// leaves were forced through the object-kind vocabulary.
		int inspecIndex = 3;
		bool vcsaServiceSubtree = false;
		if (family.Shape == VendorFamilyShape.ObjectKindSplit
			&& !string.Equals(segments[3], "inspec", StringComparison.OrdinalIgnoreCase)
			&& ObjectKindBeforeInspecSegments.Contains(segments[3]))
		{
			inspecIndex = 4;
			vcsaServiceSubtree = string.Equals(segments[3], "vcsa", StringComparison.OrdinalIgnoreCase);
			if (segments.Length < MinimumSegments + 1)
			{
				rejections.Add(new SemanticImportRejection(entry.ProfileKey,
					$"profile path has {segments.Length} segment(s); the object-kind-before-inspec vsphere layout requires at least {MinimumSegments + 1} " +
					"(<family>/<version>/<release>/<object-kind>/inspec/<baseline-directory>[/leaf]) -- this near-miss is missing the baseline profile directory and is quarantined, never guessed"));
				return;
			}
		}

		if (!string.Equals(segments[inspecIndex], "inspec", StringComparison.OrdinalIgnoreCase))
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey,
				"expected an 'inspec' directory segment after the release directory"));
			return;
		}

		string? error;
		InspecManifest? manifest = InspecManifestParser.TryParse(entry.RawYaml, out error);
		if (manifest is null)
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey, $"inspec.yml could not be parsed safely: {error}"));
			return;
		}

		// Segments after "<inspec-segment>/" are the baseline directory plus (for split
		// families) the object-kind/service leaf. The baseline directory itself
		// (segments[inspecIndex + 1]) is never part of component identity -- it is the
		// vendor's own top-level profile folder name, already captured by
		// productVersionKey+releaseKey; only what comes AFTER it distinguishes
		// aggregate-vs-leaf and (for split families) which sub-component this is.
		// inspecIndex is either 3 (documented shape) or 4 (issue #959's
		// object-kind-before-inspec vsphere shape), and both branches above guarantee
		// segments.Length >= inspecIndex + 2, so this slice can never be out of range;
		// the explicit bound below is defence-in-depth against any future edit that
		// weakens either guard (same crash class as the round-1 blocker).
		int baselineIndex = inspecIndex + 1;
		string[] tail = segments.Length > baselineIndex + 1 ? segments[(baselineIndex + 1)..] : [];

		SemanticCandidate? candidate = family.Shape switch
		{
			VendorFamilyShape.WholeAppliance => BuildWholeAppliance(entry, family, productVersionKey, kind, releaseKey!, manifest, tail),
			// Issue #1064: the `vcsa` object-kind-before-inspec subtree carries named
			// VCSA service leaves, not vcenter/esxi/vm -- route it through the same
			// named-service-split builder as the top-level `vcsa/` tree.
			VendorFamilyShape.ObjectKindSplit when vcsaServiceSubtree => BuildNamedSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail, CatalogTransports.Ssh),
			VendorFamilyShape.ObjectKindSplit => BuildObjectKindSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail),
			VendorFamilyShape.NamedServiceSplit => BuildNamedSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail, CatalogTransports.Ssh),
			VendorFamilyShape.NamedFunctionSplit => BuildNamedSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail, CatalogTransports.NsxApi),
			VendorFamilyShape.VcfGrouped => BuildVcfGrouped(entry, productVersionKey, kind, releaseKey!, manifest, tail),
			_ => null,
		};

		if (candidate is null)
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey,
				$"'{family.Name}' family layout did not match any documented sub-shape for this path"));
			return;
		}

		candidates.Add(candidate);
	}

	/// <summary>
	/// Whole-appliance (<c>ssh / target</c>) family: Aria Operations/Automation/Suite
	/// Lifecycle, Workspace ONE Access (<c>vidm</c>), Photon OS. The component IS the
	/// appliance -- no sub-service name is invented (docs/compliance-parity.md). A
	/// non-empty tail after the baseline directory means this profile has nested
	/// sub-directories the documented layout does not expect for this family, so it is
	/// treated as an aggregate grouping node rather than a leaf.
	/// </summary>
	private static SemanticCandidate BuildWholeAppliance(
		VendorContentEntry entry, VendorFamily family, string productVersionKey, string kind, string releaseKey,
		InspecManifest manifest, string[] tail)
	{
		bool isAggregate = tail.Length > 0 || !entry.HasControlsDirectory;
		string componentKey = family.Name;
		return NewCandidate(entry, family, productVersionKey, kind, releaseKey, manifest,
			componentKey, manifest.Title ?? family.Name, CatalogTransports.Ssh, CatalogSelectorKinds.Target, selectorName: null, isAggregate);
	}

	/// <summary>
	/// vSphere object-kind split: <c>vcenter</c>/<c>esxi</c>/<c>vm</c> leaves directly
	/// under the baseline directory, transport <c>vmware</c>. A profile found AT the
	/// baseline directory itself (empty tail) is the aggregate parent grouping the three
	/// object-kind leaves; it is never independently executable.
	/// </summary>
	private static SemanticCandidate? BuildObjectKindSplit(
		VendorContentEntry entry, VendorFamily family, string productVersionKey, string kind, string releaseKey,
		InspecManifest manifest, string[] tail)
	{
		if (tail.Length == 0)
		{
			return NewCandidate(entry, family, productVersionKey, kind, releaseKey, manifest,
				componentKey: "aggregate", manifest.Title ?? "vSphere (aggregate)", CatalogTransports.VMware,
				CatalogSelectorKinds.VCenter, selectorName: null, isAggregate: true);
		}

		if (tail.Length != 1 || !ObjectKindSelectors.TryGetValue(tail[0], out string? selectorKind))
		{
			return null;
		}

		return NewCandidate(entry, family, productVersionKey, kind, releaseKey, manifest,
			componentKey: tail[0].ToLowerInvariant(), manifest.Title ?? tail[0], CatalogTransports.VMware,
			selectorKind, selectorName: null, isAggregate: false);
	}

	/// <summary>
	/// Named-sub-service split families: VCSA (transport <c>ssh</c>, e.g. EAM, Lookup,
	/// PostgreSQL, ...) and NSX (transport <c>nsx-api</c>, e.g. Manager, distributed
	/// firewall, ...). Both shapes put exactly one named-leaf segment directly under the
	/// baseline directory; a profile AT the baseline directory itself (empty tail) is
	/// the aggregate parent.
	/// </summary>
	private static SemanticCandidate? BuildNamedSplit(
		VendorContentEntry entry, VendorFamily family, string productVersionKey, string kind, string releaseKey,
		InspecManifest manifest, string[] tail, string transport)
	{
		if (tail.Length == 0)
		{
			return NewCandidate(entry, family, productVersionKey, kind, releaseKey, manifest,
				componentKey: "aggregate", manifest.Title ?? $"{family.Name} (aggregate)", transport,
				CatalogSelectorKinds.Service, selectorName: null, isAggregate: true);
		}

		if (tail.Length != 1 || string.IsNullOrWhiteSpace(tail[0]))
		{
			return null;
		}

		string selectorName = tail[0].ToLowerInvariant();
		return NewCandidate(entry, family, productVersionKey, kind, releaseKey, manifest,
			componentKey: selectorName, manifest.Title ?? tail[0], transport,
			CatalogSelectorKinds.Service, selectorName, isAggregate: false);
	}

	/// <summary>
	/// Issue #1079: the real `vcf/&lt;version&gt;/&lt;release&gt;/inspec/&lt;baseline&gt;/` tree, one
	/// documented shape with three tail dispositions -- an empty tail is the umbrella
	/// baseline's aggregate parent (never independently executable, same disposition
	/// as every other family's bare-baseline-directory profile); a single-segment tail
	/// is a named VCF-native service leaf (family <c>vcf</c>, transport chosen from the
	/// closed <see cref="VcfApiNamedServiceLeaves"/> split); a two-segment tail is a
	/// closed grouping-segment leaf (`vsphere/&lt;object-kind&gt;` promotes to the
	/// `vsphere` family/`vmware` transport; `nsx/&lt;function-name&gt;` promotes to the
	/// `nsx` family/`nsx-api` transport). Anything else is a near-miss and is
	/// quarantined, never guessed.
	/// </summary>
	private static SemanticCandidate? BuildVcfGrouped(
		VendorContentEntry entry, string productVersionKey, string kind, string releaseKey,
		InspecManifest manifest, string[] tail)
	{
		if (tail.Length == 0)
		{
			return NewCandidate(entry, VsphereAggregateFamily, productVersionKey, kind, releaseKey, manifest,
				componentKey: "aggregate", manifest.Title ?? "vSphere (aggregate)", CatalogTransports.VMware,
				CatalogSelectorKinds.VCenter, selectorName: null, isAggregate: true);
		}

		if (tail.Length == 1)
		{
			string leaf = tail[0].ToLowerInvariant();
			string transport = VcfApiNamedServiceLeaves.Contains(leaf) ? CatalogTransports.VcfApi : CatalogTransports.Ssh;
			return NewCandidate(entry, VcfFamily, productVersionKey, kind, releaseKey, manifest,
				componentKey: leaf, manifest.Title ?? tail[0], transport, CatalogSelectorKinds.Service, leaf, isAggregate: false);
		}

		if (tail.Length == 2 && string.Equals(tail[0], "vsphere", StringComparison.OrdinalIgnoreCase)
			&& VcfGroupedVSphereSelectorKinds.TryGetValue(tail[1], out string? selectorKind))
		{
			string componentKey = string.Equals(tail[1], "esx", StringComparison.OrdinalIgnoreCase) ? "esxi" : tail[1].ToLowerInvariant();
			return NewCandidate(entry, VsphereAggregateFamily, productVersionKey, kind, releaseKey, manifest,
				componentKey, manifest.Title ?? tail[1], CatalogTransports.VMware, selectorKind, selectorName: null, isAggregate: false);
		}

		if (tail.Length == 2 && string.Equals(tail[0], "nsx", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tail[1]))
		{
			string selectorName = tail[1].ToLowerInvariant();
			return NewCandidate(entry, NsxFamily, productVersionKey, kind, releaseKey, manifest,
				componentKey: selectorName, manifest.Title ?? tail[1], CatalogTransports.NsxApi, CatalogSelectorKinds.Service, selectorName, isAggregate: false);
		}

		return null;
	}

	// Small fixed VendorFamily instances BuildVcfGrouped resolves its actual product
	// family from per-branch (the top-level "vcf" Families entry's own name/shape is
	// deliberately generic -- see its comment); Shape is unused by NewCandidate.
	private static readonly VendorFamily VsphereAggregateFamily = new("vsphere", VendorFamilyShape.ObjectKindSplit);
	private static readonly VendorFamily VcfFamily = new("vcf", VendorFamilyShape.VcfGrouped);
	private static readonly VendorFamily NsxFamily = new("nsx", VendorFamilyShape.NamedFunctionSplit);

	private static SemanticCandidate NewCandidate(
		VendorContentEntry entry, VendorFamily family, string productVersionKey, string kind, string releaseKey,
		InspecManifest manifest, string componentKey, string displayName, string transport, string selectorKind,
		string? selectorName, bool isAggregate)
	{
		return new SemanticCandidate(
			entry.ProfileKey,
			family.Name,
			productVersionKey,
			kind,
			componentKey,
			releaseKey,
			displayName,
			transport,
			selectorKind,
			selectorName,
			isAggregate,
			manifest.Title,
			manifest.Version,
			manifest.Inputs,
			manifest.Supports,
			manifest.Depends,
			ComputeDigest(entry, manifest, releaseKey));
	}

	/// <summary>
	/// Deterministic content digest for one profile (issue #729 AC "content digest ...
	/// populated from source metadata"; deliverable 5 "deterministic import report"). Two
	/// separate parses of byte-identical inputs always produce the same digest -- inputs
	/// are hashed in a stable, explicit order rather than relying on any incidental
	/// collection order upstream.
	/// </summary>
	private static string ComputeDigest(VendorContentEntry entry, InspecManifest manifest, string releaseKey)
	{
		StringBuilder builder = new();
		builder.Append(entry.ProfileKey).Append('\n');
		builder.Append(releaseKey).Append('\n');
		builder.Append(manifest.Name).Append('\n');
		builder.Append(manifest.Title).Append('\n');
		builder.Append(manifest.Version).Append('\n');
		foreach (InspecManifestInput input in manifest.Inputs.OrderBy(i => i.Name, StringComparer.Ordinal))
		{
			builder.Append(input.Name).Append(':').Append(input.Type).Append(':').Append(input.Required).Append(';');
		}

		builder.Append('\n');
		foreach (string control in entry.ControlFileNames.OrderBy(c => c, StringComparer.Ordinal))
		{
			builder.Append(control).Append(';');
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	/// <summary>
	/// Splits a release directory name (e.g. <c>v2r3-stig</c>, <c>Y26M05-srg</c>) into
	/// its (kind, releaseKey) -- kind is read from an explicit trailing
	/// <c>-stig</c>/<c>-srg</c> suffix, never inferred from any other part of the name
	/// (docs/compliance-parity.md "STIG and SRG content are distinct first-class kinds").
	/// </summary>
	private static (string? Kind, string? ReleaseKey) ParseReleaseSegment(string segment)
	{
		if (segment.EndsWith("-stig", StringComparison.OrdinalIgnoreCase))
		{
			return (CatalogKinds.Stig, segment);
		}

		if (segment.EndsWith("-srg", StringComparison.OrdinalIgnoreCase))
		{
			return (CatalogKinds.Srg, segment);
		}

		return (null, null);
	}

	private sealed record VendorFamily(string Name, VendorFamilyShape Shape);

	private enum VendorFamilyShape
	{
		WholeAppliance,
		ObjectKindSplit,
		NamedServiceSplit,
		NamedFunctionSplit,
		VcfGrouped,
	}
}
