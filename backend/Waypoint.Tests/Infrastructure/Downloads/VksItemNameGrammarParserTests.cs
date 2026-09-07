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
/// Issue #1480 AC1: the name-grammar parser correctly extracts dimensions across all
/// four naming eras research #1031 Layer B documented, including arch-absent legacy
/// names and unparseable/legacy-shape fallbacks. The 138/138 live-catalog claim is
/// research #1031's own measurement against the real public library; the fixture
/// below is entirely INVENTED (CLAUDE.md sanitization rules -- no real item name,
/// build id, or catalog content is copied), shaped to cover the same four-era
/// grammar and edge cases #1031 documented rather than to reproduce its cardinality.
/// </summary>
public sealed class VksItemNameGrammarParserTests
{
	public static IEnumerable<object[]> ParseableFixtures()
	{
		// legacy_k8s era: `*-k8s-*`, arch always absent from the name (#1031).
		yield return ["ob-10000001-photon-3-k8s-v1.16.8---vmware.1-tkg.1", VksNamingEras.LegacyK8s, "photon", "3", null!, "1.16.8", "1", false, VksReleaseLines.Tkg, "1", "10000001"];
		yield return ["ob-10000002-photon-3-k8s-v1.17.9---vmware.1-fips.1-tkg.2", VksNamingEras.LegacyK8s, "photon", "3", null!, "1.17.9", "1", true, VksReleaseLines.Tkg, "2", "10000002"];
		yield return ["ob-10000003-ubuntu-20.04-k8s-v1.18.10---vmware.2-tkg.3", VksNamingEras.LegacyK8s, "ubuntu", "20.04", null!, "1.18.10", "2", false, VksReleaseLines.Tkg, "3", "10000003"];
		yield return ["ob-10000004-ubuntu-20.04-k8s-v1.19.7---vmware.1-fips-tkg.1", VksNamingEras.LegacyK8s, "ubuntu", "20.04", null!, "1.19.7", "1", true, VksReleaseLines.Tkg, "1", "10000004"];
		yield return ["ob-10000005-photon-3-k8s-v1.16.15---vmware.3.1-tkg.4", VksNamingEras.LegacyK8s, "photon", "3", null!, "1.16.15", "3", false, VksReleaseLines.Tkg, "4", "10000005"];
		yield return ["ob-10000023-photon-3-k8s-v1.16.20---vmware.2-tkg.6", VksNamingEras.LegacyK8s, "photon", "3", null!, "1.16.20", "2", false, VksReleaseLines.Tkg, "6", "10000023"];
		yield return ["ob-10000028-photon-3-k8s-v1.16.25---vmware.4-tkg.8", VksNamingEras.LegacyK8s, "photon", "3", null!, "1.16.25", "4", false, VksReleaseLines.Tkg, "8", "10000028"];
		yield return ["ob-10000029-ubuntu-20.04-k8s-v1.18.20---vmware.2-fips.2-tkg.9", VksNamingEras.LegacyK8s, "ubuntu", "20.04", null!, "1.18.20", "2", true, VksReleaseLines.Tkg, "9", "10000029"];

		// tkgs_ova era: `tkgs-ova-*` prefix, arch also absent (#1031).
		yield return ["ob-10000006-tkgs-ova-photon-3-k8s-v1.16.8---vmware.1-tkg.1", VksNamingEras.TkgsOva, "photon", "3", null!, "1.16.8", "1", false, VksReleaseLines.Tkg, "1", "10000006"];
		yield return ["ob-10000007-tkgs-ova-ubuntu-20.04-k8s-v1.17.9---vmware.1-fips.1-tkg.2", VksNamingEras.TkgsOva, "ubuntu", "20.04", null!, "1.17.9", "1", true, VksReleaseLines.Tkg, "2", "10000007"];
		yield return ["ob-10000008-tkgs-ova-photon-3-k8s-v1.16.12---vmware.2-tkg.3", VksNamingEras.TkgsOva, "photon", "3", null!, "1.16.12", "2", false, VksReleaseLines.Tkg, "3", "10000008"];
		yield return ["ob-10000024-tkgs-ova-ubuntu-20.04-k8s-v1.17.15---vmware.3-tkg.7", VksNamingEras.TkgsOva, "ubuntu", "20.04", null!, "1.17.15", "3", false, VksReleaseLines.Tkg, "7", "10000024"];
		yield return ["ob-10000030-tkgs-ova-photon-3-k8s-v1.16.30---vmware.5-tkg.10", VksNamingEras.TkgsOva, "photon", "3", null!, "1.16.30", "5", false, VksReleaseLines.Tkg, "10", "10000030"];

		// vmi_k8s era: `*-vmi-k8s-*` token, arch present.
		yield return ["ob-10000009-ubuntu-22.04-amd64-vmi-k8s-v1.20.4---vmware.1-vkr.1", VksNamingEras.VmiK8s, "ubuntu", "22.04", "amd64", "1.20.4", "1", false, VksReleaseLines.Vkr, "1", "10000009"];
		yield return ["ob-10000010-photon-3-amd64-vmi-k8s-v1.21.6---vmware.1-fips.1-vkr.2", VksNamingEras.VmiK8s, "photon", "3", "amd64", "1.21.6", "1", true, VksReleaseLines.Vkr, "2", "10000010"];
		yield return ["ob-10000011-ubuntu-22.04-amd64-vmi-k8s-v1.22.8---vmware.2-tkg.5", VksNamingEras.VmiK8s, "ubuntu", "22.04", "amd64", "1.22.8", "2", false, VksReleaseLines.Tkg, "5", "10000011"];
		yield return ["ob-10000012-photon-5-amd64-vmi-k8s-v1.23.9---vmware.1-fips-vkr.3", VksNamingEras.VmiK8s, "photon", "5", "amd64", "1.23.9", "1", true, VksReleaseLines.Vkr, "3", "10000012"];
		yield return ["ob-10000025-ubuntu-22.04-amd64-vmi-k8s-v1.24.10---vmware.1-fips.2-vkr.6", VksNamingEras.VmiK8s, "ubuntu", "22.04", "amd64", "1.24.10", "1", true, VksReleaseLines.Vkr, "6", "10000025"];

		// current era: no k8s token at all, arch present.
		yield return ["ob-10000013-photon-5-amd64-v1.28.4---vmware.2-vkr.1", VksNamingEras.Current, "photon", "5", "amd64", "1.28.4", "2", false, VksReleaseLines.Vkr, "1", "10000013"];
		yield return ["ob-10000014-ubuntu-24.04-amd64-v1.29.5---vmware.1-vkr.2", VksNamingEras.Current, "ubuntu", "24.04", "amd64", "1.29.5", "1", false, VksReleaseLines.Vkr, "2", "10000014"];
		yield return ["ob-10000015-ubuntu-24.04-amd64-v1.30.2---vmware.3-fips.1-vkr.4", VksNamingEras.Current, "ubuntu", "24.04", "amd64", "1.30.2", "3", true, VksReleaseLines.Vkr, "4", "10000015"];
		yield return ["ob-10000016-photon-5-amd64-v1.31.1---vmware.1-vkr.5", VksNamingEras.Current, "photon", "5", "amd64", "1.31.1", "1", false, VksReleaseLines.Vkr, "5", "10000016"];
		yield return ["ob-10000017-ubuntu-22.04-amd64-v1.32.0---vmware.2-vkr.1", VksNamingEras.Current, "ubuntu", "22.04", "amd64", "1.32.0", "2", false, VksReleaseLines.Vkr, "1", "10000017"];
		yield return ["ob-10000018-photon-5-amd64-v1.33.3---vmware.1-vkr.2", VksNamingEras.Current, "photon", "5", "amd64", "1.33.3", "1", false, VksReleaseLines.Vkr, "2", "10000018"];
		yield return ["ob-10000019-ubuntu-24.04-amd64-v1.34.1---vmware.4-vkr.5", VksNamingEras.Current, "ubuntu", "24.04", "amd64", "1.34.1", "4", false, VksReleaseLines.Vkr, "5", "10000019"];
		yield return ["ob-10000020-ubuntu-24.04-amd64-v1.35.2---vmware.1-vkr.3", VksNamingEras.Current, "ubuntu", "24.04", "amd64", "1.35.2", "1", false, VksReleaseLines.Vkr, "3", "10000020"];
		yield return ["ob-10000021-photon-5-amd64-v1.36.1---vmware.4-vkr.5", VksNamingEras.Current, "photon", "5", "amd64", "1.36.1", "4", false, VksReleaseLines.Vkr, "5", "10000021"];
		yield return ["ob-10000022-ubuntu-22.04-arm64-v1.28.4---vmware.1-vkr.1.1", VksNamingEras.Current, "ubuntu", "22.04", "arm64", "1.28.4", "1", false, VksReleaseLines.Vkr, "1", "10000022"];
		yield return ["ob-10000026-photon-5-amd64-v1.27.9---vmware.2-fips.1-vkr.7", VksNamingEras.Current, "photon", "5", "amd64", "1.27.9", "2", true, VksReleaseLines.Vkr, "7", "10000026"];
		yield return ["ob-10000027-ubuntu-24.04-amd64-v1.25.14---vmware.1-vkr.8", VksNamingEras.Current, "ubuntu", "24.04", "amd64", "1.25.14", "1", false, VksReleaseLines.Vkr, "8", "10000027"];
	}

	public static IEnumerable<object[]> UnparseableFixtures()
	{
		yield return ["completely-bogus-name-without-grammar"];
		yield return ["ob-abc-photon-3-v1.16---vmware.1-vkr.1"];
		yield return ["ob-10000031-photon-3-v1.16.8-vmware.1-vkr.1"];
	}

	[Theory]
	[MemberData(nameof(ParseableFixtures))]
	public void Parse_EveryFixtureName_ExtractsEachDimension(
		string rawName,
		string expectedEra,
		string expectedDistro,
		string expectedDistroVersion,
		string? expectedArch,
		string expectedK8sVersion,
		string expectedVmwareBuild,
		bool expectedFips,
		string expectedReleaseLine,
		string expectedLineBuild,
		string expectedObBuildId)
	{
		VksItemNameParseResult result = new VksItemNameGrammarParser().Parse(rawName);

		Assert.Equal(VksParseStatuses.Parsed, result.ParseStatus);
		Assert.Equal(expectedEra, result.NamingEra);
		Assert.Equal(expectedDistro, result.Dimensions.Distro);
		Assert.Equal(expectedDistroVersion, result.Dimensions.DistroVersion);
		Assert.Equal(expectedArch, result.Dimensions.Arch);
		Assert.Equal(expectedK8sVersion, result.Dimensions.K8sVersion);
		Assert.Equal(expectedVmwareBuild, result.Dimensions.VmwareBuild);
		Assert.Equal(expectedFips, result.Dimensions.Fips);
		Assert.Equal(expectedReleaseLine, result.Dimensions.ReleaseLine);
		Assert.Equal(expectedLineBuild, result.Dimensions.LineBuild);
		Assert.Equal(expectedObBuildId, result.Dimensions.ObBuildId);
	}

	/// <summary>Issue #1480 AC2: an item with no parseable arch is stored with arch = null rather than failing the parse.</summary>
	[Fact]
	public void Parse_LegacyAndTkgsOvaEras_AlwaysYieldNullArch_ButStillParse()
	{
		foreach (object[] fixture in ParseableFixtures())
		{
			string era = (string)fixture[1];
			if (era is VksNamingEras.LegacyK8s or VksNamingEras.TkgsOva)
			{
				VksItemNameParseResult result = new VksItemNameGrammarParser().Parse((string)fixture[0]);
				Assert.Equal(VksParseStatuses.Parsed, result.ParseStatus);
				Assert.Null(result.Dimensions.Arch);
			}
		}
	}

	[Theory]
	[MemberData(nameof(UnparseableFixtures))]
	public void Parse_UnparseableNames_NeverThrow_AndAreFlaggedRatherThanDropped(string rawName)
	{
		VksItemNameParseResult result = new VksItemNameGrammarParser().Parse(rawName);

		Assert.Equal(VksNamingEras.Unparsed, result.NamingEra);
		Assert.Equal(VksParseStatuses.Unparsed, result.ParseStatus);
		Assert.Equal(VksItemDimensions.Empty, result.Dimensions);
	}

	[Fact]
	public void FixtureSet_CoversAtLeastThirtyNames_AcrossAllFourErasPlusUnparseable()
	{
		object[][] parseable = [.. ParseableFixtures()];
		object[][] unparseable = [.. UnparseableFixtures()];

		Assert.True(parseable.Length + unparseable.Length >= 30, "Fixture set must cover at least 30 invented names (issue #1480 test-plan requirement).");
		Assert.True(unparseable.Length >= 3, "At least 3 unparseable/legacy-shape names are required.");
		Assert.Contains(parseable, f => (string)f[1] == VksNamingEras.LegacyK8s);
		Assert.Contains(parseable, f => (string)f[1] == VksNamingEras.TkgsOva);
		Assert.Contains(parseable, f => (string)f[1] == VksNamingEras.VmiK8s);
		Assert.Contains(parseable, f => (string)f[1] == VksNamingEras.Current);
		Assert.Contains(parseable, f => (bool)f[7]);
		Assert.Contains(parseable, f => !(bool)f[7]);
		Assert.Contains(parseable, f => (string)f[8] == VksReleaseLines.Vkr);
		Assert.Contains(parseable, f => (string)f[8] == VksReleaseLines.Tkg);
	}
}
