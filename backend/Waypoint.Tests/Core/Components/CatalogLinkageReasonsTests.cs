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
using Waypoint.Core.Components;
using Xunit;

namespace Waypoint.Tests.Core.Components;

/// <summary>
/// Round-2 finding T1 on PR #1232: <see cref="CatalogLinkageReasons"/> is a CLOSED
/// vocabulary published in <c>docs/api-contract.md</c> (both the
/// <c>/targets/{id}/discover</c> and <c>/components/{id}</c> rows), exactly like
/// <see cref="Waypoint.Core.Scans.ScanPlanSkipReasons.All"/> and
/// <see cref="ScopeOmissionReasons.All"/>. These are its drift guards: the member list
/// is pinned here, every emitting branch is asserted to be inside
/// <see cref="CatalogLinkageReasons.All"/> where it is produced
/// (<c>DiscoverJobHandlerCatalogLinkageTests</c>), and the doc must publish exactly the
/// same set -- so a fifth reason cannot reach <c>discover.progress</c> without the
/// contract being updated in the same change.
/// </summary>
public sealed class CatalogLinkageReasonsTests
{
	[Fact]
	public void All_IsTheClosedSetOfEveryDeclaredReason()
	{
		Assert.Equal(
			new[]
			{
				CatalogLinkageReasons.NoExactVersionFact,
				CatalogLinkageReasons.OutOfDeclaredScope,
				CatalogLinkageReasons.Ambiguous,
				CatalogLinkageReasons.LookupFailed,
			},
			CatalogLinkageReasons.All);
	}

	/// <summary>
	/// Doc-vs-code drift guard, following the <c>ParityMatrixCompletenessTests</c>
	/// precedent of reading the repository's own documentation from the test:
	/// <c>docs/api-contract.md</c>'s <c>/targets/{id}/discover</c> row publishes this
	/// vocabulary as an explicit pipe-delimited CLOSED set, and it must be exactly
	/// <see cref="CatalogLinkageReasons.All"/> -- same members, same order.
	///
	/// Issue #1272: both directions are guarded, which containment alone did not do.
	/// Each published list is EXTRACTED from its row and compared with
	/// <see cref="Assert.Equal{T}(System.Collections.Generic.IEnumerable{T}, System.Collections.Generic.IEnumerable{T})"/>,
	/// so adding a fifth reason in code without updating the contract fails here, AND so
	/// does adding a fifth value to either doc list that no code declares. The
	/// <c>/components/{id}</c> row enumerates the same set in prose and is extracted and
	/// compared the same way.
	/// </summary>
	[Fact]
	public void ApiContract_PublishesExactlyTheseReasons()
	{
		string doc = File.ReadAllText(FindApiContractDoc());

		// Issue #1286: anchor to the SPECIFIC table row each pattern is meant to guard,
		// not the whole document -- `docs/api-contract.md` already publishes several
		// other "closed `a`\|`b` set" vocabularies (the `/stigman/test` row, the
		// `/runs/{id}/purge` outcome set, the schedule `job_type` set) and more than one
		// row starts with `/components/{id}` (the DELETE row does not mention "unlinked
		// outcome" at all). A future row worded the same way landing ABOVE the intended
		// row would otherwise silently misdirect a whole-document first-match regex.
		string discoverRowLine = FindTableRow(doc, "`/targets/{id}/discover`", "closed `");

		// The discover row's literal closed-set enumeration, e.g.
		// the closed `no_exact_version_fact`\|`out_of_declared_scope`\|`ambiguous`\|`lookup_failed` set
		// (the backslash escapes the pipe inside a Markdown table cell), extracted only
		// from the one line that both names the `/targets/{id}/discover` row AND
		// contains a "closed <list> set" enumeration.
		Match discoverRow = Regex.Match(discoverRowLine, @"closed ((?:`[a-z_]+`\\\|)*`[a-z_]+`) set", RegexOptions.None, TimeSpan.FromSeconds(5));
		Assert.True(discoverRow.Success, "docs/api-contract.md's /targets/{id}/discover row no longer publishes a 'closed <list> set' catalog-linkage reason enumeration.");
		Assert.Equal(
			CatalogLinkageReasons.All,
			discoverRow.Groups[1].Value.Split("\\|").Select(token => token.Trim('`')).ToArray());

		// The /components/{id} (GET, PUT) row's prose enumeration, e.g.
		// "... for every unlinked outcome -- `a`, `b`, `c`, `d` -- not only ...".
		// `/components/{id}` also has DELETE and /observations rows in this same doc, so
		// anchor on the one row that both names `/components/{id}` AND contains "unlinked
		// outcome" -- never the whole document's first match of either pattern alone.
		string componentRowLine = FindTableRow(doc, "`/components/{id}`", "unlinked outcome");
		Match componentRow = Regex.Match(componentRowLine, @"unlinked outcome — (.+?) —", RegexOptions.None, TimeSpan.FromSeconds(5));
		Assert.True(componentRow.Success, "docs/api-contract.md's /components/{id} (GET, PUT) row no longer publishes the catalog-linkage reason prose enumeration.");
		Assert.Equal(
			CatalogLinkageReasons.All,
			Regex.Matches(componentRow.Groups[1].Value, "`([a-z_]+)`", RegexOptions.None, TimeSpan.FromSeconds(5))
				.Select(match => match.Groups[1].Value)
				.ToArray());
	}

	/// <summary>
	/// Issue #1286's own proof: an identical-shaped "closed &lt;list&gt; set" vocabulary
	/// living elsewhere in the document -- e.g. the real `/stigman/test` and
	/// `/runs/{id}/purge` rows already do -- must never satisfy this guard just because
	/// it is the FIRST such phrase encountered. Before the anchor fix, a whole-document
	/// <c>Regex.Match</c> would happily return whichever "closed ... set" phrase comes
	/// first; this proves the row-anchored lookup instead finds (or fails to find)
	/// exactly the `/targets/{id}/discover` row, never a decoy.
	/// </summary>
	[Fact]
	public void FindTableRow_IgnoresIdenticallyShapedVocabularyOnAnUnrelatedRow()
	{
		const string decoyRowAboveTheRealOne =
			"| `/stigman/test` | POST | Some other closed `alpha`\\|`beta`\\|`gamma` set entirely, unrelated to catalog linkage. |\n";
		const string realDiscoverRow =
			"| `/targets/{id}/discover` | POST | ... breakdown over the closed `no_exact_version_fact`\\|`out_of_declared_scope`\\|`ambiguous`\\|`lookup_failed` set ... |\n";
		string fabricatedDoc = decoyRowAboveTheRealOne + realDiscoverRow;

		string foundLine = FindTableRow(fabricatedDoc, "`/targets/{id}/discover`", "closed `");

		Assert.Equal(fabricatedDoc.Split('\n')[1], foundLine);
		Assert.DoesNotContain("stigman", foundLine, StringComparison.Ordinal);

		Match discoverRow = Regex.Match(foundLine, @"closed ((?:`[a-z_]+`\\\|)*`[a-z_]+`) set", RegexOptions.None, TimeSpan.FromSeconds(5));
		Assert.True(discoverRow.Success);
		Assert.Equal(
			CatalogLinkageReasons.All,
			discoverRow.Groups[1].Value.Split("\\|").Select(token => token.Trim('`')).ToArray());

		// And the inverse: a decoy-only document (no real row present) must fail loudly,
		// never silently fall back to matching the decoy.
		Assert.Throws<Xunit.Sdk.TrueException>(() => FindTableRow(decoyRowAboveTheRealOne, "`/targets/{id}/discover`", "closed `"));
	}

	/// <summary>
	/// Returns the single line of <paramref name="doc"/> containing both
	/// <paramref name="rowIdentity"/> (the row's own literal path, e.g.
	/// <c>`/components/{id}`</c>) and <paramref name="rowMarker"/> (a phrase unique to
	/// the specific row being guarded, distinguishing it from sibling rows that share
	/// the same path). Fails loudly (rather than falling back to a whole-document scan)
	/// if zero or more than one line matches, since either case means the anchor no
	/// longer identifies exactly one row.
	/// </summary>
	private static string FindTableRow(string doc, string rowIdentity, string rowMarker)
	{
		string[] matchingLines = doc
			.Split('\n')
			.Where(line => line.Contains(rowIdentity, StringComparison.Ordinal) && line.Contains(rowMarker, StringComparison.Ordinal))
			.ToArray();

		Assert.True(
			matchingLines.Length == 1,
			$"Expected exactly one docs/api-contract.md line containing both '{rowIdentity}' and '{rowMarker}', found {matchingLines.Length}.");

		return matchingLines[0];
	}

	private static string FindApiContractDoc()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "docs", "api-contract.md");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException("Could not locate docs/api-contract.md by walking up from AppContext.BaseDirectory");
	}
}
