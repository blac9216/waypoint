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

namespace Waypoint.Tests.Core.Subscriptions;

/// <summary>
/// Issue #1421 AC4: "No literal major-version numbers appear in the new code
/// (generations are data)". Greps the issue's own production source
/// (<c>Waypoint.Core/Subscriptions</c>, <c>Waypoint.Infrastructure/Subscriptions</c>)
/// for a dotted-numeric literal shaped like a version (<c>\b\d+\.\d+</c>) -- the
/// simplest honest proof, per the issue's own suggestion, rather than trusting review
/// alone. Test fixtures/other domains are out of scope by construction (only these two
/// new folders are scanned).
/// </summary>
public sealed class NoHardcodedVersionLiteralsTests
{
	private static readonly Regex DottedNumericLiteral = new(@"\b[0-9]+\.[0-9]+", RegexOptions.Compiled);

	[Fact]
	public void SubscriptionsSourceFiles_ContainNoDottedNumericVersionLiterals()
	{
		string repoRoot = FindRepoRoot();
		string[] scanDirs =
		[
			Path.Combine(repoRoot, "backend", "Waypoint.Core", "Subscriptions"),
			Path.Combine(repoRoot, "backend", "Waypoint.Infrastructure", "Subscriptions"),
		];

		List<string> offenders = [];
		foreach (string dir in scanDirs)
		{
			Assert.True(Directory.Exists(dir), $"Expected directory '{dir}' to exist.");
			foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
			{
				foreach (string rawLine in File.ReadLines(file))
				{
					string line = rawLine.TrimStart();

					// Comment/doc-comment lines are excluded: the license header's own
					// "Version 2.0"/"LICENSE-2.0" and illustrative doc-comment examples
					// (e.g. "<c>8.0.3</c>" naming which segment a granularity extracts)
					// are prose about the mechanism, not a hardcoded literal a caller
					// could accidentally depend on -- only actual code lines matter here.
					if (line.StartsWith("//", StringComparison.Ordinal))
					{
						continue;
					}

					// Migration slot numbers referenced in prose (e.g. "migration 0104")
					// are not dotted, so they never match this pattern; only an actual
					// dotted-numeric literal (a hardcoded version-shaped token) does.
					if (DottedNumericLiteral.IsMatch(line))
					{
						offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
					}
				}
			}
		}

		Assert.True(offenders.Count == 0, $"Found hardcoded dotted-numeric literals: {string.Join(" | ", offenders)}");
	}

	private static string FindRepoRoot()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
		{
			dir = dir.Parent;
		}
		Assert.NotNull(dir);
		return dir!.FullName;
	}
}
