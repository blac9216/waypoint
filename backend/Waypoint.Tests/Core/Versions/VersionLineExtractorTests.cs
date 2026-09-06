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

using Waypoint.Core.Versions;
using Xunit;

namespace Waypoint.Tests.Core.Versions;

/// <summary>
/// Tracking-granularity line extraction (epic #16 decision 5: "tracking at
/// subminor/minor/major granularity").
/// </summary>
public sealed class VersionLineExtractorTests
{
	[Theory]
	[InlineData(VersionLineGranularity.Major, "9")]
	[InlineData(VersionLineGranularity.Minor, "9.1")]
	[InlineData(VersionLineGranularity.Subminor, "9.1.0")]
	public void TryGetLine_FourSegmentVersion_ReturnsRequestedGranularity(VersionLineGranularity granularity, string expected)
	{
		ProductVersion version = ProductVersionParser.Parse(version: "9.1.0.0100");

		Assert.Equal(expected, VersionLineExtractor.TryGetLine(version, granularity));
	}

	[Fact]
	public void TryGetLine_FewerSegmentsThanRequested_ReturnsNull_NeverFabricatesTrailingZero()
	{
		ProductVersion version = ProductVersionParser.Parse("9.1");

		Assert.Equal("9", VersionLineExtractor.TryGetLine(version, VersionLineGranularity.Major));
		Assert.Equal("9.1", VersionLineExtractor.TryGetLine(version, VersionLineGranularity.Minor));
		Assert.Null(VersionLineExtractor.TryGetLine(version, VersionLineGranularity.Subminor));
	}

	[Fact]
	public void TryGetLine_UnparsedVersion_ReturnsNull_NeverThrows()
	{
		ProductVersion version = ProductVersionParser.Parse("N/A");

		Assert.Null(VersionLineExtractor.TryGetLine(version, VersionLineGranularity.Major));
		Assert.Null(VersionLineExtractor.TryGetLine(version, VersionLineGranularity.Minor));
		Assert.Null(VersionLineExtractor.TryGetLine(version, VersionLineGranularity.Subminor));
	}
}
