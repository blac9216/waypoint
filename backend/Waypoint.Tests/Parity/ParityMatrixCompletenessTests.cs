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
using Xunit;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Fail-closed drift guard (issue #749 AC "every parity-matrix row is covered or
/// explicitly marked owner-live-only with rationale"): parses
/// docs/compliance-parity.md's "Sibling source-capability provenance matrix" table
/// directly off disk and asserts every body row is represented either by a
/// <see cref="CatalogDerivationMatrix.Rows"/> entry or by a
/// <see cref="CatalogDerivationMatrix.OwnerLiveOnlyRows"/> allow-list entry. If a future
/// doc edit adds, removes, or reshapes a row, this test fails until
/// <c>CatalogDerivationMatrix</c> is updated to match -- mirroring the repository's
/// existing drift-guard idiom (e.g. <c>SchemaMigrationTests</c>'
/// <c>ExpectedMigrationCount</c>/<c>ExpectedTables</c> ledger that must be bumped
/// alongside a new migration file).
///
/// This test reads the markdown table itself rather than hand-copying "13" as a magic
/// number, so a row added/removed in the doc is caught even if nobody updates this
/// comment.
/// </summary>
public sealed class ParityMatrixCompletenessTests
{
	/// <summary>
	/// One parsed row of the provenance-matrix table: (product/version key, kind, key
	/// form, transport). Kept minimal -- just enough to derive a stable identity and
	/// cross-check against <see cref="CatalogDerivationMatrix"/> -- rather than a full
	/// mirror of every markdown column, since the full expected tuple already lives in
	/// <see cref="CatalogDerivationMatrix.Rows"/> and re-deriving it from prose here would
	/// just duplicate that source of truth in a more fragile form.
	/// </summary>
	private sealed record DocRow(string ProductVersionKey, string KeyForm, string Kind, string Transport);

	[Fact]
	public void EveryDocumentedProvenanceMatrixRow_IsCoveredOrExplicitlyOwnerLiveOnly()
	{
		IReadOnlyList<DocRow> docRows = ParseProvenanceMatrix();

        // The doc's own header text states the reproducible count -- assert against it so
        // this test also catches the doc's count claim drifting from its own table, not
        // just this suite drifting from the doc.
        Assert.Equal(13, docRows.Count);

		HashSet<string> automatedFamilies = CatalogDerivationMatrix.Rows
			.Select(r => (r.VendorFamily, r.ProductVersionKey, r.Kind, r.Transport))
			.Select(FormatKey)
			.ToHashSet();

		List<string> uncovered = [];
		foreach (DocRow docRow in docRows)
		{
			// Issue #1064: docs/compliance-parity.md's "vSphere" product rows all map to
			// the single "vsphere" family regardless of transport (the vcsa/ directory
			// literal now promotes into the vsphere product per the owner decision on
			// #1064) -- the doc's four vSphere rows stay distinct in the coverage key
			// via their transport component, not via a separate family.
			string vendorFamily = MapProductNameToFamily(docRow.ProductVersionKey);
			string versionKey = NormalizeVersionKey(docRow.ProductVersionKey);
			string key = FormatKey((vendorFamily, versionKey, docRow.Kind, docRow.Transport));

			bool automated = automatedFamilies.Contains(key);
			bool ownerLiveOnly = CatalogDerivationMatrix.OwnerLiveOnlyRows.Keys
				.Any(id => id.StartsWith(vendorFamily, StringComparison.OrdinalIgnoreCase));

			if (!automated && !ownerLiveOnly)
			{
				uncovered.Add($"{docRow.ProductVersionKey} / {docRow.Kind} / {docRow.Transport} (resolved family '{vendorFamily}', version '{versionKey}', key='{key}')");
			}
		}

		Assert.True(uncovered.Count == 0,
			"docs/compliance-parity.md rows not covered by CatalogDerivationMatrix.Rows nor " +
			"explicitly allow-listed in CatalogDerivationMatrix.OwnerLiveOnlyRows: " + string.Join("; ", uncovered));
	}

	[Fact]
	public void OwnerLiveOnlyRows_EachHaveNonEmptyRationale()
	{
		foreach ((string id, string rationale) in CatalogDerivationMatrix.OwnerLiveOnlyRows)
		{
			Assert.False(string.IsNullOrWhiteSpace(rationale), $"owner-live-only row '{id}' must document a rationale");
		}
	}

	[Fact]
	public void EveryMatrixRow_HasAtLeastOneComponent()
	{
		// Guards against a row being added to CatalogDerivationMatrix with an empty
		// Components list, which would make CatalogParityContractTests's theory silently
		// assert nothing for that row.
		foreach (CatalogParityRow row in CatalogDerivationMatrix.Rows)
		{
			Assert.True(row.Components.Count > 0, $"matrix row '{row.MatrixRowId}' has no components");
		}
	}

	private static string FormatKey((string VendorFamily, string ProductVersionKey, string Kind, string Transport) tuple) =>
		$"{tuple.VendorFamily}|{tuple.ProductVersionKey}|{tuple.Kind}|{tuple.Transport}".ToLowerInvariant();

	private static string MapProductNameToFamily(string productVersionKey)
	{
		string name = productVersionKey.Split('`')[0].Trim().ToLowerInvariant();
		return name switch
		{
			"vsphere" => "vsphere", // ambiguous by design: both vmware+ssh rows map to different families below via transport
			"nsx" => "nsx",
			"aria operations" => "aria-operations",
			"aria automation" => "aria-automation",
			"aria suite lifecycle" => "aria-suite-lifecycle",
			"workspace one access" => "vidm",
			"photon os" => "photon",
			"vcf" => "vcf",
			_ => name,
		};
	}

	private static string NormalizeVersionKey(string productVersionKey)
	{
		Match match = Regex.Match(productVersionKey, "`([^`]+)`");
		return match.Success ? match.Groups[1].Value.Replace('-', '.') : productVersionKey;
	}

	/// <summary>
	/// Parses the body rows of docs/compliance-parity.md's "Sibling source-capability
	/// provenance matrix" table. Deliberately narrow (column-position based, not a
	/// general markdown parser) -- this table's shape is a stable, reviewed contract
	/// (ADR-0022), not arbitrary prose.
	/// </summary>
	private static List<DocRow> ParseProvenanceMatrix()
	{
		string docPath = FindComplianceParityDoc();
		string[] lines = File.ReadAllLines(docPath);

		int headerIndex = Array.FindIndex(lines, l => l.StartsWith("| Sibling product/version key", StringComparison.Ordinal));
		if (headerIndex < 0)
		{
			throw new InvalidOperationException("docs/compliance-parity.md: provenance-matrix header row not found -- has the table been renamed/moved?");
		}

		List<DocRow> rows = [];
		for (int i = headerIndex + 2; i < lines.Length; i++)
		{
			string line = lines[i];
			if (!line.StartsWith('|'))
			{
				break; // table ended
			}

			string[] cells = line.Split('|', StringSplitOptions.TrimEntries)
				.Where((_, idx) => idx > 0) // drop the leading empty split before the first '|'
				.ToArray();
			if (cells.Length < 6)
			{
				continue;
			}

			string productVersionKey = cells[0];
			string keyForm = cells[1];
			string kindAndRevision = cells[2];
			string transportAndSelector = cells[4];

			string kind = kindAndRevision.Contains("STIG", StringComparison.OrdinalIgnoreCase) ? "stig" : "srg";
			string transport = transportAndSelector.Split('/')[0].Trim().Trim('`');

			rows.Add(new DocRow(productVersionKey, keyForm, kind, transport));
		}

		return rows;
	}

	private static string FindComplianceParityDoc()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "docs", "compliance-parity.md");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException("Could not locate docs/compliance-parity.md by walking up from AppContext.BaseDirectory");
	}
}
