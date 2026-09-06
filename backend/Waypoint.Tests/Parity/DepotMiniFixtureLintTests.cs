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
/// PR #1742 review round-1 note 2: every literal, invented checksum checked into
/// <c>Fixtures/depot-mini/</c> must have the real SHA-256 shape (64 lowercase hex
/// characters) even though it is deliberately never-matching -- a wrong-length or
/// wrong-case literal would let a hash-length/format regression in the parsers slip
/// past this fixture unnoticed. Skips the <c>{{sha256:...}}</c> template tokens
/// (<see cref="Support.DepotMiniFixture"/>/<c>New-DepotMiniFixture.ps1</c> rewrite
/// those against real materialized bytes, uppercase, at fixture-load time -- a
/// different, already-verified contract) and scans only the checked-in, un-rewritten
/// source tree.
/// </summary>
public sealed class DepotMiniFixtureLintTests
{
	private static readonly string SourceRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "depot-mini");

	// A "checksum"/checksum-type="sha-256" value that is NOT a {{sha256:...}} template
	// token -- i.e. a literal, invented hash.
	private static readonly Regex JsonChecksumPattern = new("\"checksum\"\\s*:\\s*\"(?!\\{\\{)([^\"]+)\"", RegexOptions.Compiled);
	private static readonly Regex XmlChecksumPattern = new("checksum-type=\"sha-256\">(?!\\{\\{)([^<]+)<", RegexOptions.Compiled);
	private static readonly Regex Sha256Shape = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

	public static IEnumerable<object[]> LiteralChecksums()
	{
		foreach (string file in Directory.GetFiles(SourceRoot, "*", SearchOption.AllDirectories))
		{
			string extension = Path.GetExtension(file);
			if (extension is not (".json" or ".xml"))
			{
				continue;
			}

			string content = File.ReadAllText(file);
			Regex pattern = extension == ".json" ? JsonChecksumPattern : XmlChecksumPattern;
			foreach (Match match in pattern.Matches(content))
			{
				yield return [Path.GetRelativePath(SourceRoot, file), match.Groups[1].Value];
			}
		}
	}

	[Theory]
	[MemberData(nameof(LiteralChecksums))]
	public void LiteralChecksum_Is64LowercaseHexCharacters(string relativePath, string checksum)
	{
		Assert.True(
			Sha256Shape.IsMatch(checksum),
			$"{relativePath}: literal checksum '{checksum}' ({checksum.Length} chars) is not 64 lowercase hex characters.");
	}

	[Fact]
	public void AtLeastOneLiteralChecksumWasScanned()
	{
		// Guards against the theory above silently passing with zero cases if the
		// fixture tree or its checksum shape ever changes underneath this test.
		Assert.NotEmpty(LiteralChecksums());
	}
}
