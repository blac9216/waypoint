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
		string doc = ReadDocFile();
		string headingLine = $"## {sectionHeading}";
		int sectionStart = doc.IndexOf(headingLine, StringComparison.Ordinal);
		Assert.True(sectionStart >= 0, $"docs/compliance-content-shape-inventory.md is missing the '{sectionHeading}' section this guard parses.");
		int sectionEnd = doc.IndexOf("\n## ", sectionStart + 1, StringComparison.Ordinal);
		string section = sectionEnd >= 0 ? doc[sectionStart..sectionEnd] : doc[sectionStart..];

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
	public static void AssertCompleteness(string sectionHeading, IEnumerable<string> implementedShapeIds)
	{
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
