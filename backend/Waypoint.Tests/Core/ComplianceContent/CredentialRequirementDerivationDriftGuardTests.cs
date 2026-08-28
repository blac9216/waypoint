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
using Waypoint.Core.Secrets;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent;

/// <summary>
/// Class-killing guard for issue #1012's own defect class (the same idiom
/// <c>LayoutTableParityTests</c>/<c>ExecutionCatalogSeedDriftGuardTests</c> already
/// establish for the importer/seed shape tables): this test parses
/// docs/compliance-parity.md's "Sibling source-capability provenance matrix" Purpose
/// column directly and proves, for every documented (product family, transport,
/// selector kind) row, that <see cref="CredentialRequirementDerivation.DeriveRequiredPurposes"/>
/// -- the ingest-time rule <see cref="CatalogRepository.PromoteCandidateAsync"/> now
/// calls -- returns exactly the documented purpose set. A future doc edit that changes
/// a row's Purpose column without updating the derivation (or vice versa) fails this
/// test, not a live pull that silently promotes a credential-less profile again.
///
/// No real vendor content, path, or output appears anywhere in this file -- every
/// mapping below is read straight out of the doc's own table.
/// </summary>
public sealed class CredentialRequirementDerivationDriftGuardTests
{
	private sealed record ProvenanceRow(string ProductVersionKey, string TransportSelector, string Purpose);

	/// <summary>
	/// Maps the doc's "Sibling product/version key" leading product name to the
	/// <c>VendorFamily</c>/<c>catalog_products.product_key</c> literal the importer and
	/// seed migrations both use for that family (docs/compliance-parity.md's own
	/// "Recognized on-disk import layouts" table + migrations 0064/0067/0069's
	/// product_key literals are the source for this mapping).
	/// </summary>
	private static readonly Dictionary<string, string> ProductNameToFamily = new(StringComparer.Ordinal)
	{
		["vSphere"] = "vsphere",
		["NSX"] = "nsx",
		["Aria Operations"] = "aria-operations",
		["Aria Automation"] = "aria-automation",
		["Aria Suite Lifecycle"] = "aria-suite-lifecycle",
		["Workspace ONE Access"] = "vidm",
		["Photon OS"] = "photon",
		["VCF"] = "vcf",
	};

	[Fact]
	public void EveryProvenanceMatrixRow_DerivesExactlyItsDocumentedPurposeSet()
	{
		List<ProvenanceRow> rows = ParseProvenanceRows();
		Assert.Equal(13, rows.Count);

		List<string> failures = [];
		foreach (ProvenanceRow row in rows)
		{
			(string transport, string selectorKind) = ParseTransportSelector(row.TransportSelector);
			string family = ResolveFamily(row.ProductVersionKey);

			HashSet<string> documentedPurposes = ParseDocumentedPurposes(row.Purpose);
			if (documentedPurposes.Count == 0)
			{
				// The one row (VCF vcf-api) whose Purpose column is prose ("catalog-declared
				// API purpose (#807)") rather than a literal purpose token -- ADR-0024/issue
				// #977 resolved that prose to the literal 'vcf-api' purpose, which this test
				// checks explicitly below instead of trying to parse it out of the prose.
				documentedPurposes = [CredentialPurposes.VcfApi];
			}

			HashSet<string> derivedPurposes = [.. CredentialRequirementDerivation.DeriveRequiredPurposes(family, transport, selectorKind)];

			if (!derivedPurposes.SetEquals(documentedPurposes))
			{
				failures.Add(
					$"{row.ProductVersionKey} / {row.TransportSelector}: doc says [{string.Join(", ", documentedPurposes.OrderBy(p => p, StringComparer.Ordinal))}] " +
					$"but derivation returned [{string.Join(", ", derivedPurposes.OrderBy(p => p, StringComparer.Ordinal))}] (family='{family}', transport='{transport}', selectorKind='{selectorKind}').");
			}
		}

		Assert.True(failures.Count == 0, "Credential-purpose derivation drift (doc vs. CredentialRequirementDerivation):\n" + string.Join("\n", failures));
	}

	/// <summary>
	/// Reverse direction: an unmapped/undocumented (family, transport, selector) shape
	/// must derive nothing at all -- fail-closed, never a guessed purpose (issue #1012
	/// AC). Exercises one shape per closed transport that has no documented row.
	/// </summary>
	[Fact]
	public void UndocumentedShapes_DeriveNoRequirements_FailClosed()
	{
		// ssh/service under a family that is neither vsphere nor vcf -- no doc row
		// documents any ssh/service purpose for any other family.
		Assert.Empty(CredentialRequirementDerivation.DeriveRequiredPurposes("photon", CatalogTransports.Ssh, CatalogSelectorKinds.Service));
		Assert.Empty(CredentialRequirementDerivation.DeriveRequiredPurposes("aria-operations", CatalogTransports.Ssh, CatalogSelectorKinds.Service));

		// ssh/vcenter (or any generic object-kind selector) is not a documented ssh
		// shape at all -- only vmware transport uses the object-kind selectors.
		Assert.Empty(CredentialRequirementDerivation.DeriveRequiredPurposes("vsphere", CatalogTransports.Ssh, CatalogSelectorKinds.VCenter));
	}

	private static string ResolveFamily(string productVersionKey)
	{
		foreach ((string productName, string family) in ProductNameToFamily)
		{
			if (productVersionKey.StartsWith(productName, StringComparison.Ordinal))
			{
				return family;
			}
		}

		throw new InvalidOperationException($"No family mapping for provenance-matrix product/version key '{productVersionKey}' -- add it to {nameof(ProductNameToFamily)}.");
	}

	/// <summary>
	/// Parses the doc's "Transport / selector" column (e.g. <c>`vmware` / object kind</c>,
	/// <c>`ssh` / named VCSA service</c>, <c>`ssh` / target</c>, <c>`nsx-api` / named
	/// function</c>, <c>`vcf-api` / named service</c>) into a closed
	/// (transport, selectorKind) pair. "object kind" rows use the vCenter/ESXi/VM
	/// selectors interchangeably for derivation purposes (all three derive the same
	/// purpose set), so this test picks the vcenter selector as a representative.
	/// </summary>
	private static (string Transport, string SelectorKind) ParseTransportSelector(string transportSelector)
	{
		Match match = Regex.Match(transportSelector, @"`([a-z-]+)`\s*/\s*(.+)");
		Assert.True(match.Success, $"Could not parse transport/selector column '{transportSelector}'.");
		string transport = match.Groups[1].Value;
		string selectorText = match.Groups[2].Value.Trim();

		string selectorKind = selectorText switch
		{
			"object kind" => CatalogSelectorKinds.VCenter,
			"target" => CatalogSelectorKinds.Target,
			"named VCSA service" or "named function" or "named service" => CatalogSelectorKinds.Service,
			_ => throw new InvalidOperationException($"Unrecognized selector text '{selectorText}' in transport/selector column '{transportSelector}'."),
		};

		return (transport, selectorKind);
	}

	/// <summary>
	/// Parses the doc's Purpose column into a set of closed purpose literals -- e.g.
	/// <c>`vsphere-api`</c> -&gt; {vsphere-api}, <c>`vsphere-api` + `vcsa-ssh`</c> -&gt;
	/// {vsphere-api, vcsa-ssh}. Returns an empty set for prose (the vcf-api row), which
	/// the caller resolves explicitly.
	/// </summary>
	private static HashSet<string> ParseDocumentedPurposes(string purposeColumn)
	{
		HashSet<string> purposes = new(StringComparer.Ordinal);
		foreach (Match match in Regex.Matches(purposeColumn, "`([a-z-]+)`"))
		{
			purposes.Add(match.Groups[1].Value);
		}

		return purposes;
	}

	private static List<ProvenanceRow> ParseProvenanceRows()
	{
		string doc = ReadRepoFile("docs", "compliance-parity.md");
		int sectionStart = doc.IndexOf("## Sibling source-capability provenance matrix", StringComparison.Ordinal);
		Assert.True(sectionStart >= 0, "docs/compliance-parity.md is missing the provenance matrix section this guard parses.");
		int sectionEnd = doc.IndexOf("\n## ", sectionStart + 1, StringComparison.Ordinal);
		string section = sectionEnd >= 0 ? doc[sectionStart..sectionEnd] : doc[sectionStart..];

		List<ProvenanceRow> rows = [];
		foreach (Match rowMatch in Regex.Matches(
			section,
			@"^\| ([^|]+?) \| (exact|family) \| (?:STIG|SRG) / `[^`]+` \| [^|]+ \| ([^|]+) \| ([^|]+) \| [^|]+ \|$",
			RegexOptions.Multiline))
		{
			rows.Add(new ProvenanceRow(
				ProductVersionKey: rowMatch.Groups[1].Value.Trim(),
				TransportSelector: rowMatch.Groups[3].Value.Trim(),
				Purpose: rowMatch.Groups[4].Value.Trim()));
		}

		return rows;
	}

	private static string ReadRepoFile(params string[] repoRelativeParts)
	{
		string repoRelativePath = Path.Combine(repoRelativeParts);
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
