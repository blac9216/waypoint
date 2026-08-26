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
	private static readonly Dictionary<string, VendorFamily> Families = new Dictionary<string, VendorFamily>(StringComparer.OrdinalIgnoreCase)
	{
		["vsphere"] = new VendorFamily("vsphere", VendorFamilyShape.ObjectKindSplit),
		["vcsa"] = new VendorFamily("vcsa", VendorFamilyShape.NamedServiceSplit),
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
			InterpretOne(entry, candidates, rejections);
		}

		return new VendorHierarchyInterpretation(
			[.. candidates.OrderBy(c => c.ProfileKey, StringComparer.Ordinal)],
			[.. rejections.OrderBy(r => r.ProfileKey, StringComparer.Ordinal)]);
	}

	private static void InterpretOne(VendorContentEntry entry, List<SemanticCandidate> candidates, List<SemanticImportRejection> rejections)
	{
		string[] segments = entry.ProfileKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 4)
		{
			rejections.Add(new SemanticImportRejection(entry.ProfileKey,
				"profile path has fewer segments than any documented family layout (expected <family>/<version>/<release>/inspec/...)"));
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

		if (segments.Length < 4 || !string.Equals(segments[3], "inspec", StringComparison.OrdinalIgnoreCase))
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

		// Segments after "<family>/<version>/<release>/inspec/" are the baseline
		// directory plus (for split families) the object-kind/service leaf. The
		// baseline directory itself (segments[4]) is never part of component identity
		// -- it is the vendor's own top-level profile folder name, already captured by
		// productVersionKey+releaseKey; only what comes AFTER it distinguishes
		// aggregate-vs-leaf and (for split families) which sub-component this is.
		string[] tail = segments[5..];

		SemanticCandidate? candidate = family.Shape switch
		{
			VendorFamilyShape.WholeAppliance => BuildWholeAppliance(entry, family, productVersionKey, kind, releaseKey!, manifest, tail),
			VendorFamilyShape.ObjectKindSplit => BuildObjectKindSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail),
			VendorFamilyShape.NamedServiceSplit => BuildNamedSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail, CatalogTransports.Ssh),
			VendorFamilyShape.NamedFunctionSplit => BuildNamedSplit(entry, family, productVersionKey, kind, releaseKey!, manifest, tail, CatalogTransports.NsxApi),
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
	}
}
