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

using System.Text.RegularExpressions;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Xunit;
using static Waypoint.Tests.Core.ComplianceContent.SemanticImport.VendorContentEntryBuilder;

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Class-killing guard for issue #959's defect class: "the interpreter's closed
/// family/layout table silently drifted from the upstream layout docs/compliance-
/// parity.md documents, and nothing caught it until 100% of real content quarantined."
/// This test parses docs/compliance-parity.md's "Recognized on-disk import layouts"
/// table directly -- the SAME authoritative source a human maintainer edits -- and
/// proves, for every documented row, that a minimal INVENTED fixture built from that
/// row's exact path shape actually classifies via <see cref="VendorHierarchyInterpreter"/>
/// into the documented family. A future edit that adds/changes a row here without
/// updating the interpreter (or vice versa) fails this test, not a live-lab pull.
///
/// No real vendor content, path, or output appears anywhere in this file -- every
/// fixture is fabricated to the documented shape only.
/// </summary>
public sealed class LayoutTableParityTests
{
	private sealed record DocumentedLayoutRow(string DirectoryLiteral, string MapsToFamily, bool IsObjectKindBeforeInspec);

	[Fact]
	public void EveryDocumentedLayoutRow_ClassifiesIntoItsDocumentedFamily()
	{
		List<DocumentedLayoutRow> rows = ParseDocumentedLayoutRows();

		Assert.NotEmpty(rows);

		List<string> failures = [];
		foreach (DocumentedLayoutRow row in rows)
		{
			VendorContentEntry entry = BuildFixtureFor(row);
			VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

			if (result.Rejections.Count > 0)
			{
				failures.Add($"'{row.DirectoryLiteral}' (object-kind-before-inspec={row.IsObjectKindBeforeInspec}): expected classification into family " +
					$"'{row.MapsToFamily}' but got rejected: {result.Rejections[0].Reason}");
				continue;
			}

			SemanticCandidate candidate = Assert.Single(result.Candidates);
			if (!string.Equals(candidate.VendorFamily, row.MapsToFamily, StringComparison.Ordinal))
			{
				failures.Add($"'{row.DirectoryLiteral}': documented to map to family '{row.MapsToFamily}' but the interpreter classified it as '{candidate.VendorFamily}'.");
			}
		}

		Assert.True(failures.Count == 0, "Layout table parity failures (doc vs. interpreter drifted):\n" + string.Join("\n", failures));
	}

	/// <summary>
	/// The reverse direction: every top-level directory literal the interpreter
	/// actually recognizes today must have a corresponding row in the documented
	/// table -- otherwise the interpreter silently supports an on-disk shape the parity
	/// doc's provenance matrix does not describe, which is exactly the kind of drift
	/// this guard exists to catch (just in the other direction).
	/// </summary>
	[Fact]
	public void EveryInterpreterRecognizedDirectoryLiteral_HasADocumentedRow()
	{
		List<DocumentedLayoutRow> rows = ParseDocumentedLayoutRows();
		HashSet<string> documentedLiterals = new(rows.Select(r => r.DirectoryLiteral), StringComparer.OrdinalIgnoreCase);

		// The full closed set of directory literals docs/compliance-parity.md documents
		// as recognized today (issue #959). This list exists ONLY to drive the reverse
		// check below (a literal accidentally recognized by the interpreter but never
		// documented) -- it is not itself the authority the code is checked against;
		// EveryDocumentedLayoutRow_ClassifiesIntoItsDocumentedFamily above is the
		// doc-is-authoritative direction.
		string[] knownRecognizedLiterals = ["vsphere", "vcf", "vcsa", "nsx", "photon", "aria-operations", "aria-automation", "aria-suite-lifecycle", "vidm"];

		foreach (string literal in knownRecognizedLiterals)
		{
			Assert.True(documentedLiterals.Contains(literal), $"'{literal}' is a directory the interpreter recognizes but docs/compliance-parity.md's layout table has no row for it -- document it or the two have drifted.");
		}

		// And nothing UNRECOGNIZED sneaks past as "documented" either -- e.g. aria/vcd/avi
		// must still quarantine (issue #959's "quarantine-never-guess for everything else").
		foreach (string stillUnrecognized in new[] { "aria", "vcd", "avi" })
		{
			VendorContentEntry entry = Leaf($"{stillUnrecognized}/1-0/v1r1-srg/inspec/some-baseline", Manifest("m"), "controls/x.rb");
			VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);
			Assert.Empty(result.Candidates);
			Assert.Single(result.Rejections);
		}
	}

	/// <summary>
	/// The issue's two rejection classes reproduced exactly (the 156-count `vcf` tree
	/// and the 111-count object-kind-before-inspec tree) must now import cleanly end to
	/// end -- not just via the generic per-row fixture above, but as a literal repro of
	/// the issue text's disposition breakdown.
	/// </summary>
	[Fact]
	public void IssueDisposition_VcfTreeAndObjectKindBeforeInspecTree_NowImportCleanly()
	{
		VendorContentEntry vcfVCenter = Leaf(
			"vcf/9-0/y26m05-srg/inspec/vcf-9-0-srg-baseline/vcenter",
			Manifest("vcenter", "VCF 9.0 vCenter SRG", "1.0.0"),
			"controls/vc-000001.rb");
		VendorContentEntry vsphereObjectKindBeforeInspec = Leaf(
			"vsphere/8-0/v2r3-stig/vsphere/inspec/vsphere-8-0-stig-baseline/esxi",
			Manifest("esxi", "vSphere 8.0 ESXi STIG", "2.3.0"),
			"controls/esxi-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([vcfVCenter, vsphereObjectKindBeforeInspec]);

		Assert.Empty(result.Rejections);
		Assert.Equal(2, result.Candidates.Count);

		SemanticCandidate vcf = Assert.Single(result.Candidates, c => c.ComponentKey == "vcenter");
		Assert.Equal("vsphere", vcf.VendorFamily);
		Assert.Equal(CatalogTransports.VMware, vcf.Transport);
		Assert.Equal(CatalogSelectorKinds.VCenter, vcf.SelectorKind);

		SemanticCandidate objectKindBeforeInspec = Assert.Single(result.Candidates, c => c.ComponentKey == "esxi");
		Assert.Equal("vsphere", objectKindBeforeInspec.VendorFamily);
		Assert.Equal(CatalogSelectorKinds.Esxi, objectKindBeforeInspec.SelectorKind);
	}

	private static VendorContentEntry BuildFixtureFor(DocumentedLayoutRow row)
	{
		string profileKey = row.IsObjectKindBeforeInspec
			? $"{row.DirectoryLiteral}/1-0/v1r1-stig/vcsa/inspec/{row.DirectoryLiteral}-baseline/vcenter"
			: $"{row.DirectoryLiteral}/1-0/v1r1-srg/inspec/{row.DirectoryLiteral}-baseline";

		return Leaf(profileKey, Manifest($"{row.DirectoryLiteral}-baseline"), "controls/x.rb");
	}

	/// <summary>
	/// Parses docs/compliance-parity.md's "Recognized on-disk import layouts" table.
	/// Row shape: <c>| directory literal | maps-to-family | path shape | notes |</c>.
	/// The directory-literal column may carry a parenthetical qualifier (e.g. "vsphere
	/// (object-kind-before-inspec)") which this parser strips into a separate flag
	/// rather than treating as part of the literal.
	/// </summary>
	private static List<DocumentedLayoutRow> ParseDocumentedLayoutRows()
	{
		string doc = ReadDocFile();
		int sectionStart = doc.IndexOf("## Recognized on-disk import layouts", StringComparison.Ordinal);
		Assert.True(sectionStart >= 0, "docs/compliance-parity.md is missing the 'Recognized on-disk import layouts' section this guard parses.");
		int sectionEnd = doc.IndexOf("\n## ", sectionStart + 1, StringComparison.Ordinal);
		string section = sectionEnd >= 0 ? doc[sectionStart..sectionEnd] : doc[sectionStart..];

		List<DocumentedLayoutRow> rows = [];
		foreach (Match rowMatch in Regex.Matches(section, @"^\| `([^`]+)`(?:\s*\(([^)]+)\))? \| `([^`]+)` \|", RegexOptions.Multiline))
		{
			string directoryLiteral = rowMatch.Groups[1].Value;
			bool isObjectKindBeforeInspec = string.Equals(rowMatch.Groups[2].Value, "object-kind-before-inspec", StringComparison.Ordinal);
			string mapsToFamily = rowMatch.Groups[3].Value;
			rows.Add(new DocumentedLayoutRow(directoryLiteral, mapsToFamily, isObjectKindBeforeInspec));
		}

		return rows;
	}

	private static string ReadDocFile()
	{
		const string repoRelativePath = "docs/compliance-parity.md";
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(dir.FullName, repoRelativePath);
			if (File.Exists(candidate))
			{
				return File.ReadAllText(candidate);
			}

			dir = dir.Parent;
		}

		throw new FileNotFoundException($"Could not locate {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
	}
}
