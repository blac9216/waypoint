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
/// Scoped to a directory glob of the four namespaces the known call sites live in
/// (<see cref="ScannedDirectories"/>), not a repo-wide grep for the five vocabulary
/// words: several OTHER closed-vocabulary constants classes in this codebase
/// legitimately reuse the same words for unrelated columns
/// (<c>Waypoint.Core.Downloads.Download.DownloadStates</c>'s own
/// <c>"downloading"</c>/<c>"failed"</c>, <c>Waypoint.Core.Catalog.LibraryItem</c>'s own
/// <c>"present"</c>/<c>"missing"</c>, <c>Waypoint.Core.Catalog.CatalogPullOutcomes</c>'s
/// own <c>"failed"</c>, <c>Waypoint.Core.Jobs.JobStates</c>'s own <c>"failed"</c>, and
/// more) -- a repo-wide word grep would flag every one of those as a false positive,
/// which is why this test does not scan the whole repository. Within the four scanned
/// directories, <see cref="ExcludedFiles"/> names the small, closed set of files that
/// legitimately declare or reuse the vocabulary for something other than
/// <c>depot_artifacts.status</c> (each with its own reason at the exclusion site) --
/// PR #1805 round-1 review finding note 7: unlike the prior hardcoded
/// call-site allowlist, a NEW file added to any of these four directories is
/// automatically picked up by the glob and scanned, so it cannot silently escape this
/// test the way a file omitted from a fixed list could. The drift guard
/// <see cref="Waypoint.Tests.Infrastructure.Postgres.DepotArtifactStatusesConstraintDriftTests"/>
/// remains the backstop for the vocabulary itself, not for every call site's literal-
/// vs-constant hygiene.
/// </summary>
public sealed class DepotArtifactStatusesNoBareLiteralTests
{
	private static readonly string[] Vocabulary = ["indexed", "downloading", "present", "failed", "missing"];

	/// <summary>Every production namespace a <c>depot_artifacts.status</c> call site lives in today (issue #1675's own list, plus #1512's <c>CatalogIndexJobHandler</c>).</summary>
	private static readonly string[] ScannedDirectories =
	[
		"backend/Waypoint.Core/Catalog",
		"backend/Waypoint.Infrastructure/Catalog",
		"backend/Waypoint.Infrastructure.Execution/Catalog",
		"backend/Waypoint.Infrastructure.Execution/Downloads",
	];

	/// <summary>Files within <see cref="ScannedDirectories"/> that legitimately use the vocabulary for something other than <c>depot_artifacts.status</c>, each with its own reason.</summary>
	private static readonly string[] ExcludedFiles =
	[
		// Declares DepotArtifactStatuses itself -- the constants' own literal values.
		"backend/Waypoint.Core/Catalog/DepotArtifact.cs",
		// LibraryItem's own present/missing vocabulary -- a different column (library_items.status), not depot_artifacts.status.
		"backend/Waypoint.Core/Catalog/LibraryItem.cs",
		// CatalogPullOutcomes' own "failed" -- catalog_pull_state.last_outcome, not depot_artifacts.status.
		"backend/Waypoint.Core/Catalog/CatalogPullState.cs",
	];

	[Fact]
	public void NoScannedFile_ContainsABareDepotArtifactStatusLiteral()
	{
		string repoRoot = FindRepoRoot();
		HashSet<string> excluded = new(
			ExcludedFiles.Select(path => path.Replace('/', Path.DirectorySeparatorChar)), StringComparer.Ordinal);

		List<string> scannedFiles = [];
		foreach (string relativeDirectory in ScannedDirectories)
		{
			string fullDirectory = Path.Combine(repoRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
			Assert.True(Directory.Exists(fullDirectory), $"expected '{relativeDirectory}' to exist");

			foreach (string fullPath in Directory.GetFiles(fullDirectory, "*.cs", SearchOption.TopDirectoryOnly))
			{
				string relativePath = Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
				if (excluded.Contains(relativePath.Replace('/', Path.DirectorySeparatorChar)))
				{
					continue;
				}

				scannedFiles.Add(relativePath);
			}
		}

		// Sanity check that the glob is actually finding the known call sites, not
		// silently scanning an empty set (e.g. a directory rename that broke
		// ScannedDirectories without breaking the build).
		Assert.Contains(scannedFiles, path => path.EndsWith("VendorProductVersionCatalogParser.cs", StringComparison.Ordinal));
		Assert.Contains(scannedFiles, path => path.EndsWith("CatalogIndexJobHandler.cs", StringComparison.Ordinal));

		List<string> offenders = [];
		foreach (string relativePath in scannedFiles)
		{
			string fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
