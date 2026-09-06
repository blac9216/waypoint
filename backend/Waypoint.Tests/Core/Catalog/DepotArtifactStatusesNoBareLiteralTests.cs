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
// System.Linq brought in transitively by implicit usings (net8.0) -- Waypoint.Tests
// enables ImplicitUsings, matching every other test file in this project.

namespace Waypoint.Tests.Core.Catalog;

/// <summary>
/// Issue #1675 AC2: every production call site that reads or writes
/// <c>depot_artifacts.status</c> must use <c>Waypoint.Core.Catalog.DepotArtifactStatuses</c>,
/// never a bare literal -- a typo in one of these strings (<c>"Present"</c> vs
/// <c>"present"</c>, or a future caller inventing a fifth status string) compiles
/// cleanly and fails silently at an ordinal-comparison call site otherwise.
///
/// Deliberately scoped to the known call sites named on the issue (and
/// <see cref="Waypoint.Infrastructure.Catalog.CatalogIndexJobHandler"/>, the #1512
/// rework this test lands alongside) rather than a repo-wide grep for the five
/// vocabulary words: several OTHER closed-vocabulary constants classes in this
/// codebase legitimately reuse the same words for unrelated columns
/// (<c>Waypoint.Core.Downloads.Download.DownloadStates</c>'s own
/// <c>"downloading"</c>/<c>"failed"</c>, <c>Waypoint.Core.Catalog.LibraryItem</c>'s own
/// <c>"present"</c>/<c>"missing"</c>, <c>Waypoint.Core.Jobs.JobStates</c>'s own
/// <c>"failed"</c>, and more) -- a repo-wide word grep would flag every one of those as
/// a false positive. A future <c>depot_artifacts.status</c> call site added to a file
/// not in this list would not be caught here; the drift guard
/// <see cref="Waypoint.Tests.Infrastructure.Postgres.DepotArtifactStatusesConstraintDriftTests"/>
/// is the backstop for the vocabulary itself, not for every call site's literal-vs-
/// constant hygiene.
/// </summary>
public sealed class DepotArtifactStatusesNoBareLiteralTests
{
	private static readonly string[] Vocabulary = ["indexed", "downloading", "present", "failed", "missing"];

	private static readonly string[] KnownCallSiteFiles =
	[
		"backend/Waypoint.Core/Catalog/VendorProductVersionCatalogParser.cs",
		"backend/Waypoint.Core/Catalog/LibraryPresenceEvaluator.cs",
		"backend/Waypoint.Infrastructure.Execution/Downloads/DownloadJobHandler.cs",
		"backend/Waypoint.Infrastructure.Execution/Downloads/BinariesDownloadJobHandler.cs",
		"backend/Waypoint.Infrastructure.Execution/Catalog/CatalogIndexJobHandler.cs",
	];

	[Fact]
	public void NoKnownCallSiteFile_ContainsABareDepotArtifactStatusLiteral()
	{
		string repoRoot = FindRepoRoot();

		List<string> offenders = [];
		foreach (string relativePath in KnownCallSiteFiles)
		{
			string fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Assert.True(File.Exists(fullPath), $"expected '{relativePath}' to exist");

			// Only actual code lines -- doc comments (///) and line comments (//)
			// legitimately quote the raw vocabulary word in prose (e.g. "upserts
			// status = \"failed\""), which is not the bare-literal-in-code hygiene gap
			// this test guards against.
			string[] codeLines = [.. File.ReadAllLines(fullPath)
				.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))];
			string content = string.Join('\n', codeLines);
			foreach (string word in Vocabulary)
			{
				// Only literal C# string tokens ("present"), never a substring of a
				// longer literal or an identifier -- word-bounded on both quote sides.
				if (Regex.IsMatch(content, $"(?<![A-Za-z0-9_])\"{Regex.Escape(word)}\"(?![A-Za-z0-9_])"))
				{
					offenders.Add($"{relativePath}: \"{word}\"");
				}
			}
		}

		Assert.Empty(offenders);
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
