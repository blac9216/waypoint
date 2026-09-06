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

using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1392 AC2: the <c>versions</c>-file join correctly correlates ESXi build to
/// Tools version, including a row whose Tools version string is unparseable (falls
/// back to <see cref="VmToolsEsxVersionMapping.SequenceInFile"/> -- the file's own
/// newest-first-by-ESXi-build order -- rather than a numeric comparison). The fixture
/// content below is entirely invented (CLAUDE.md), shaped like research #1030's
/// documented 5-column layout but with fabricated values.
/// </summary>
public sealed class VmToolsVersionsFileParserTests
{
	private const string FixtureVersionsFile =
		"""
		# invented fixture, not a real vmtools versions file -- 5 whitespace-separated
		# columns: tools-version-code, esx/<version>, esxi-build, tools-version, tools-build
		13344 esx/9.1 25370933 13.1.0 25218885
		13322 esx/9.0.2u1 25300111 13.0.10 25100222
		13317 esx/9.0.1u1 25290000 RC-unparseable-string 25090000
		13312 esx/0.0 12.5.4 24900000-build
		this row has too many columns to ever be five 1 2 3
		""";

	[Fact]
	public void Parse_FixtureFile_ProducesOneMappingPerDataRow_InFileOrder()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse(FixtureVersionsFile);

		Assert.Equal(4, result.Mappings.Count);
		Assert.Equal([0, 1, 2, 3], result.Mappings.Select(m => m.SequenceInFile));
	}

	[Fact]
	public void Parse_WellFormedRow_ParsesToolsVersionSegmentsAndEsxJoinKey()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse(FixtureVersionsFile);

		VmToolsEsxVersionMapping head = result.Mappings[0];
		Assert.Equal("13344", head.ToolsVersionCode);
		Assert.Equal("esx/9.1", head.EsxiVersionDir);
		Assert.Equal("25370933", head.EsxiBuild);
		Assert.Equal("13.1.0", head.ToolsVersionRaw);
		Assert.Equal(13, head.ToolsVersionMajor);
		Assert.Equal(1, head.ToolsVersionMinor);
		Assert.Equal(0, head.ToolsVersionPatch);
		Assert.Equal("25218885", head.ToolsBuild);
	}

	/// <summary>
	/// Confirms the string-sort trap research #1030 flagged (13.0.10 &gt; 13.0.5, but
	/// "13.0.10" sorts before "13.0.5" as a string) is why segments are parsed as
	/// integers rather than compared as text.
	/// </summary>
	[Fact]
	public void Parse_ToolsVersionSegments_AreIntegersNotStrings()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse(FixtureVersionsFile);

		VmToolsEsxVersionMapping row = result.Mappings.Single(m => m.EsxiVersionDir == "esx/9.0.2u1");
		Assert.Equal(10, row.ToolsVersionPatch);
		Assert.True(row.ToolsVersionPatch > 5, "13.0.10 must compare greater than 13.0.5 numerically, not as a string.");
	}

	[Fact]
	public void Parse_UnparseableToolsVersion_YieldsNullSegments_ButKeepsItsFileSequence()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse(FixtureVersionsFile);

		VmToolsEsxVersionMapping unparseable = result.Mappings.Single(m => m.ToolsVersionRaw == "RC-unparseable-string");
		Assert.Null(unparseable.ToolsVersionMajor);
		Assert.Null(unparseable.ToolsVersionMinor);
		Assert.Null(unparseable.ToolsVersionPatch);
		// Release-recency fallback (AC2): the row's position in the newest-first-by-
		// ESXi-build file is still meaningful even though its version string is not.
		Assert.Equal(2, unparseable.SequenceInFile);
	}

	[Fact]
	public void Parse_EsxZeroZero_MeansNotBundled_AndIsNotTreatedAsAnError()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse(FixtureVersionsFile);

		VmToolsEsxVersionMapping notBundled = result.Mappings.Single(m => m.EsxiVersionDir == "esx/0.0");
		Assert.Null(notBundled.EsxiBuild);
		Assert.Equal("12.5.4", notBundled.ToolsVersionRaw);
	}

	[Fact]
	public void Parse_MalformedRow_IsSkippedAndSurfacedAsAWarning_NeverSwallowed()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse(FixtureVersionsFile);

		Assert.Single(result.Warnings);
		Assert.Contains("malformed row", result.Warnings[0], StringComparison.Ordinal);
		Assert.DoesNotContain(result.Mappings, m => m.RawRow.StartsWith("this row has too many", StringComparison.Ordinal));
	}

	[Fact]
	public void Parse_CommentAndBlankLines_AreIgnored()
	{
		VmToolsVersionsFileParseResult result = new VmToolsVersionsFileParser().Parse("# just a header\n\n");
		Assert.Empty(result.Mappings);
		Assert.Empty(result.Warnings);
	}
}
