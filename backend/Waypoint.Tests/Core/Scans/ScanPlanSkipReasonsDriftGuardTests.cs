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
using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Issue #1138: <see cref="ScanPlanSkipReasons.All"/> is a closed set enforced only in
/// application code -- <c>scan_plans.skips_json</c> (migration 0057) is JSONB with no
/// CHECK constraint, so there is no database vocabulary to drift-guard against the way
/// <c>ComponentResultStatusConstraintDriftTests</c> does for a CHECK-constrained
/// column. Instead this test parses docs/api-contract.md's own "closed vocabulary"
/// list -- the documentation this codebase treats as the contract of record for every
/// other closed set (see that file's other "closed vocabulary" tables) -- and asserts
/// it matches <see cref="ScanPlanSkipReasons.All"/> exactly, in order. A future skip
/// reason added to one without the other fails this test.
/// </summary>
public sealed class ScanPlanSkipReasonsDriftGuardTests
{
	[Fact]
	public void ScanPlanSkipReasons_All_EqualsApiContractDocumentedClosedVocabulary()
	{
		string repoRoot = FindRepoRoot();
		string docPath = Path.Combine(repoRoot, "docs", "api-contract.md");
		string doc = File.ReadAllText(docPath);

		int headingIndex = doc.IndexOf("#### `/runs/{id}/plan` skip reason closed vocabulary", StringComparison.Ordinal);
		Assert.True(headingIndex >= 0, "docs/api-contract.md must document the /runs/{id}/plan skip reason closed vocabulary.");

		int nextHeadingIndex = doc.IndexOf("\n#### ", headingIndex + 1, StringComparison.Ordinal);
		string section = nextHeadingIndex > 0
			? doc[headingIndex..nextHeadingIndex]
			: doc[headingIndex..];

		// Only the bullet-list lines (`- `reason` — ...`), never the closing
		// `unmapped_benchmark` retirement paragraph below the list.
		List<string> documentedReasons = [.. Regex.Matches(section, @"^- `(?<reason>[a-z_]+)`", RegexOptions.Multiline)
			.Select(m => m.Groups["reason"].Value)];

		Assert.NotEmpty(documentedReasons);
		Assert.Equal(ScanPlanSkipReasons.All, documentedReasons);
	}

	private static string FindRepoRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "docs", "api-contract.md")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Could not locate repository root (docs/api-contract.md not found in any ancestor directory).");
	}
}
