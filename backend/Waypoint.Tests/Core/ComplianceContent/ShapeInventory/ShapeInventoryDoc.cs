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

namespace Waypoint.Tests.Core.ComplianceContent.ShapeInventory;

/// <summary>
/// Shared reader for <c>docs/compliance-content-shape-inventory.md</c> (issue #1077),
/// generalizing <c>LayoutTableParityTests</c>' "parse the authoritative doc table and
/// assert it against the code" pattern (issue #959) to every vendor-content parser's
/// shape inventory. The doc is the authority: a <c>*ShapeInventoryTests</c> class
/// asserts BOTH that every documented shape ID has an implemented fixture/assertion,
/// AND that every implemented fixture/assertion has a documented row -- so the two
/// cannot silently drift apart.
///
/// The binding is doc &lt;-&gt; fixture set, NOT doc &lt;-&gt; parser branches: a parser
/// can gain a branch with no row and no fixture and this assertion stays green (PR
/// #1098 round-1 review demonstrated it). Keeping the inventory in step with the
/// parsers is review-enforced; see the "What this guard does and does not cover"
/// section of <c>docs/compliance-content-shape-inventory.md</c>.
/// </summary>
public static class ShapeInventoryDoc
{
	/// <summary>
	/// Parses the shape-ID column (first backtick-quoted column of each table row)
	/// out of the section starting at <paramref name="sectionHeading"/> (an exact
	/// <c>## </c> heading line) and ending at the next <c>## </c> heading or end of
	/// file.
	/// </summary>
	public static List<string> ParseShapeIds(string sectionHeading)
	{
		string section = ReadSection(sectionHeading);

		List<string> ids = [];
		foreach (Match rowMatch in Regex.Matches(section, @"^\| `([a-z0-9-]+)` \|", RegexOptions.Multiline))
		{
			ids.Add(rowMatch.Groups[1].Value);
		}

		return ids;
	}

	/// <summary>
	/// Asserts that <paramref name="implementedShapeIds"/> (the shape IDs a
	/// <c>*ShapeInventoryTests</c> class actually has a fixture/assertion for) is
	/// exactly the set documented under <paramref name="sectionHeading"/> -- neither a
	/// documented row missing a fixture, nor a fixture with no documented row.
	/// </summary>
	/// <param name="sectionHeading">The doc section to check, as in <see cref="ParseShapeIds"/>.</param>
	/// <param name="implementedShapeIds">Every shape ID this parser's test class has a fixture/assertion for.</param>
	/// <param name="rejectedShapeIds">
	/// The subset of <paramref name="implementedShapeIds"/> whose fixture actually asserts rejection (a
	/// <c>ShapeIsRejected</c>-style theory case), e.g. <c>StigZipReaderShapeInventoryTests.RejectedShapeIds</c>.
	/// Every other implemented shape is assumed to assert acceptance. Defaults to empty for parsers with no
	/// reject fixtures at all. Binds the doc's Expected verdict word to what the fixture actually does (issue
	/// #1121): a row whose word says "Accepted" while its fixture is listed here, or vice versa, fails the
	/// build -- so a documentation-only edit to the Expected cell can no longer disarm a shape's protection
	/// while leaving the suite green.
	/// </param>
	public static void AssertCompleteness(string sectionHeading, IEnumerable<string> implementedShapeIds, IReadOnlySet<string>? rejectedShapeIds = null)
	{
		AssertExpectedVocabulary(sectionHeading);

		List<string> documented = ParseShapeIds(sectionHeading);
		Assert.NotEmpty(documented);

		HashSet<string> documentedSet = new(documented, StringComparer.Ordinal);
		HashSet<string> implementedSet = new(implementedShapeIds, StringComparer.Ordinal);

		List<string> documentedButNotImplemented = documentedSet.Except(implementedSet).OrderBy(id => id, StringComparer.Ordinal).ToList();
		List<string> implementedButNotDocumented = implementedSet.Except(documentedSet).OrderBy(id => id, StringComparer.Ordinal).ToList();

		Assert.True(
			documentedButNotImplemented.Count == 0 && implementedButNotDocumented.Count == 0,
			"Shape inventory drift under '" + sectionHeading + "':" +
			(documentedButNotImplemented.Count > 0 ? $"\n  documented but no fixture: {string.Join(", ", documentedButNotImplemented)}" : string.Empty) +
			(implementedButNotDocumented.Count > 0 ? $"\n  fixture but not documented: {string.Join(", ", implementedButNotDocumented)}" : string.Empty));

		AssertVerdictMatchesFixtures(sectionHeading, rejectedShapeIds ?? new HashSet<string>(StringComparer.Ordinal));
	}

	/// <summary>
	/// Asserts that every row's classified Expected verdict (<c>ClassifyExpectedCell</c>) agrees with whether
	/// its shape ID is a member of <paramref name="rejectedShapeIds"/> -- i.e. with what the corresponding
	/// <c>ShapeIsAccepted</c>/<c>ShapeIsRejected</c> fixture actually asserts. See <see cref="AssertCompleteness"/>.
	/// </summary>
	private static void AssertVerdictMatchesFixtures(string sectionHeading, IReadOnlySet<string> rejectedShapeIds)
	{
		List<string> mismatches = [];
		foreach ((string shapeId, string? verdict) in ClassifyShapes(sectionHeading))
		{
			string expectedVerdict = rejectedShapeIds.Contains(shapeId) ? "reject" : "accept";
			if (verdict != expectedVerdict)
			{
				mismatches.Add($"{shapeId}: doc's Expected cell reads '{verdict ?? "unclassifiable"}', but its fixture asserts '{expectedVerdict}'");
			}
		}

		Assert.True(
			mismatches.Count == 0,
			"Shape inventory Expected verdict does not match what the fixture actually asserts under '" + sectionHeading + "' -- " +
			"a documentation-only edit to the Expected cell must not be able to disarm a shape's protection. Offending row(s):\n  " +
			string.Join("\n  ", mismatches));
	}

	/// <summary>
	/// Classifies every row's Expected cell under <paramref name="sectionHeading"/> as <c>"accept"</c>,
	/// <c>"reject"</c>, or <c>null</c> (see <see cref="ClassifyExpectedCell"/>), keyed by shape ID. This is the
	/// single authoritative classification: <c>ShapeVerdictDump</c> serializes it to JSON so
	/// <c>scripts/parser-shape-diff.sh</c> reads the SAME column split and classification this class uses,
	/// rather than re-parsing the markdown table with an independent (and, per issue #1120, subtly different)
	/// implementation.
	/// </summary>
	public static Dictionary<string, string?> ClassifyShapes(string sectionHeading)
	{
		Dictionary<string, string?> result = new(StringComparer.Ordinal);
		foreach ((string shapeId, string expectedCell) in EnumerateExpectedCells(sectionHeading))
		{
			result[shapeId] = ClassifyExpectedCell(expectedCell);
		}

		return result;
	}

	/// <summary>
	/// Asserts that every row's <c>Expected</c> column in <paramref name="sectionHeading"/>
	/// opens with a recognized verdict token -- <c>Accepted</c> or <c>Rejected</c>, ignoring
	/// markdown emphasis, backticks and case. <c>scripts/parser-shape-diff.sh</c> reads that
	/// token to tell a documented-accept shape from a documented-reject one, and fails closed
	/// (<c>UNVERIFIABLE</c>, exit 1) on anything it cannot classify; this assertion keeps an
	/// unclassifiable cell from ever reaching the script, so a cosmetic edit to the wording
	/// fails the suite here rather than quietly weakening the differential (PR #1098 round-2
	/// review).
	/// </summary>
	public static void AssertExpectedVocabulary(string sectionHeading)
	{
		List<string> malformed = [];
		foreach ((string shapeId, string expectedCell) in EnumerateExpectedCells(sectionHeading))
		{
			if (ClassifyExpectedCell(expectedCell) is null)
			{
				malformed.Add($"{shapeId}: \"{expectedCell.Trim()}\"");
			}
		}

		Assert.True(
			malformed.Count == 0,
			"Every Expected cell under '" + sectionHeading + "' must begin with 'Accepted' or 'Rejected' -- " +
			"scripts/parser-shape-diff.sh parses that token to classify a rejected -> accepted flip, " +
			"and treats anything it cannot classify as UNVERIFIABLE. Offending row(s):\n  " +
			string.Join("\n  ", malformed));
	}

	/// <summary>
	/// Returns <c>"accept"</c>, <c>"reject"</c>, or <c>null</c> when the cell's leading word is
	/// neither. Mirrors the normalization in <c>scripts/parser-shape-diff.sh</c>: strip leading
	/// whitespace and markdown emphasis/backtick characters, then compare the leading run of
	/// letters case-insensitively.
	/// </summary>
	internal static string? ClassifyExpectedCell(string cell)
	{
		string token = Regex.Match(cell.TrimStart().TrimStart('*', '_', '`', ' '), "^[A-Za-z]+").Value.ToLowerInvariant();
		return token switch
		{
			"accepted" => "accept",
			"rejected" => "reject",
			_ => null,
		};
	}

	/// <summary>
	/// Yields each row's shape ID and Expected-cell text under <paramref name="sectionHeading"/>, splitting
	/// each row on its last UNESCAPED pipe (see <see cref="LastColumn"/>). The single place both
	/// <see cref="AssertExpectedVocabulary"/> and <see cref="ClassifyShapes"/> read the table from, so the two
	/// cannot drift on how a row is split into columns.
	/// </summary>
	private static IEnumerable<(string ShapeId, string ExpectedCell)> EnumerateExpectedCells(string sectionHeading)
	{
		string section = ReadSection(sectionHeading);
		foreach (Match rowMatch in Regex.Matches(section, @"^\| `([a-z0-9-]+)` \|(.*)\|[^\S\n]*$", RegexOptions.Multiline))
		{
			yield return (rowMatch.Groups[1].Value, LastColumn(rowMatch.Groups[2].Value));
		}
	}

	/// <summary>
	/// Splits a table row's remainder (everything after the shape-ID column, minus the closing
	/// pipe) on its last UNESCAPED pipe, so a description containing a literal <c>\|</c> -- as
	/// the block-scalar row does -- does not shift which column is read as Expected.
	/// Internal rather than private so <c>ShapeInventoryDocColumnSplitTests</c> can cover the
	/// escape-aware walk-back directly: no row of the live inventory carries a <c>\|</c> in its
	/// Expected cell, so without those tests this guard's escape handling could be deleted with
	/// the suite green (issue #1120 AC3, PR #1126 round-2 review).
	/// </summary>
	internal static string LastColumn(string rowRemainder)
	{
		for (int i = rowRemainder.Length - 1; i >= 0; i--)
		{
			if (rowRemainder[i] == '|' && (i == 0 || rowRemainder[i - 1] != '\\'))
			{
				return rowRemainder[(i + 1)..];
			}
		}

		return rowRemainder;
	}

	private static string ReadSection(string sectionHeading)
	{
		string doc = ReadDocFile();
		string headingLine = $"## {sectionHeading}";
		int sectionStart = doc.IndexOf(headingLine, StringComparison.Ordinal);
		Assert.True(sectionStart >= 0, $"docs/compliance-content-shape-inventory.md is missing the '{sectionHeading}' section this guard parses.");
		int sectionEnd = doc.IndexOf("\n## ", sectionStart + 1, StringComparison.Ordinal);
		return sectionEnd >= 0 ? doc[sectionStart..sectionEnd] : doc[sectionStart..];
	}

	private static string ReadDocFile()
	{
		const string repoRelativePath = "docs/compliance-content-shape-inventory.md";
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
