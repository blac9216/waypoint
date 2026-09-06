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
/// Table coverage over real vendor version shapes found by epic #16's research lanes
/// (#1027 depot/catalog, #1028 UMDS/ESX, #1030 VMware Tools, #1031 VKS) plus the #572
/// misranking case. Every row states which shape it is exercising so a future shape
/// addition has an obvious place to land. Every version string is written through
/// <see cref="V"/>, a named-argument wrapper: several rows are real four-dotted-segment
/// vendor version strings whose octets are all &lt;=255, which is shape-identical to an
/// IPv4 literal (issue #1694's trap).
/// <c>.github/sanitize/scan_repo_specific.py</c>'s IPv4 detector waives a candidate quad
/// only when the literal word "version" (colon, optional space, optional quote) sits
/// immediately before it -- the named argument <c>version:</c> below is exactly that
/// form, verified clean by the scanner before this file's introduction.
/// </summary>
public sealed class ProductVersionComparerTests
{
	private static string V(string version) => version;

	public static readonly TheoryData<string, string, int> Rows = new()
	{
		// -- issue #572's own misranking cases (ordinal string compare gets these backwards) --
		{ V(version: "9.10"), V(version: "9.9"), 1 },
		{ V(version: "9.9"), V(version: "9.10"), -1 },

		// -- ESX/Tools `versions` column 4 (#1030): "13.0.10 > 13.0.5", string sort is wrong --
		{ V(version: "13.0.10"), V(version: "13.0.5"), 1 },
		{ V(version: "13.0.5"), V(version: "13.0.10"), -1 },
		{ V(version: "13.1.0"), V(version: "13.0.10"), 1 },

		// -- dotted-numeric build-suffixed depot versions (#1027), five-digit builds --
		{ V(version: "8.0.3.00900"), V(version: "8.0.3.00901"), -1 },
		{ V(version: "9.1.0.0100"), V(version: "9.1.0.0200"), -1 },
		{ V(version: "9.1.0.0100"), V(version: "9.1.0.0100"), 0 },

		// -- vcfManifest.json release versions (#1027 Q4), four dotted segments <=255 --
		{ V(version: "4.4.1.0"), V(version: "5.2.4.0"), -1 },

		// -- Photon branch axis (#1029): numeric compare, not lexical ("10" vs "2" trap) --
		{ V(version: "2.0"), V(version: "10.0"), -1 },
		{ V(version: "5.0"), V(version: "4.0"), 1 },

		// -- tdnf package version-release shape (#1029): "3.6.5-2" vs "3.5.2-1" --
		{ V(version: "3.6.5-2"), V(version: "3.5.2-1"), 1 },

		// -- padding / segment-count normalisation --
		{ V(version: "9.01.0"), V(version: "9.1.0"), 0 },
		{ V(version: "9.1"), V(version: "9.1.0"), 0 },
		{ V(version: "9.1"), V(version: "9.1.1"), -1 },
		{ V(version: "9.0"), V(version: "10.0"), -1 },

		// -- VKR (#1031): `+vmware.N` build metadata, `-fips.N`/`-vkr.N`/`-tkg.N` tags --
		{ V(version: "1.25.7+vmware.3-fips.1-tkg.1"), V(version: "1.36.1+vmware.4-vkr.5"), -1 },
		{ V(version: "1.36.1+vmware.4-vkr.5"), V(version: "v1.35.2+vmware.1-vkr.3"), 1 },
		{ V(version: "1.35.2+vmware.1-vkr.3"), V(version: "1.35.2+vmware.2-vkr.1"), -1 },
		{ V(version: "1.25.7+vmware.3-fips.1-tkg.1"), V(version: "1.25.7+vmware.3-fips.1-tkg.1"), 0 },

		// -- build-id tag as tertiary tiebreak, numeric not lexical (large build ids) --
		{ V(version: "13.1.0-25218885"), V(version: "13.1.0-25370933"), -1 },
		{ V(version: "9.1.0.0100-1"), V(version: "9.1.0.0100-2"), -1 },

		// -- build suffix exceeding 255 (the trap this parser must not corrupt via byte overflow) --
		{ V(version: "9.1.0.100000"), V(version: "9.1.0.99999"), 1 },
		{ V(version: "9.1.0.256"), V(version: "9.1.0.255"), 1 },

		// -- untagged (release) ranks after a tagged build of the same numeric version --
		{ V(version: "1.0.0"), V(version: "1.0.0-rc1"), 1 },
		{ V(version: "1.0.0-rc1"), V(version: "1.0.0"), -1 },

		// -- tag comparison is case-insensitive --
		{ V(version: "1.0.0-RC1"), V(version: "1.0.0-rc1"), 0 },

		// -- ESX host-platform / manifest four-segment version, both <=255 octets --
		{ V(version: "9.0.2.0"), V(version: "9.0.1.0"), 1 },

		// -- unparseable input never throws and sorts as the lowest (all-zero, no tag) version --
		{ V(version: "N/A"), V(version: "9.0"), -1 },
		{ V(version: "9.0"), V(version: "N/A"), 1 },
		{ V(version: "N/A"), V(version: "TBD"), 0 },
		{ V(version: ""), V(version: "9.0"), -1 },
	};

	[Theory]
	[MemberData(nameof(Rows))]
	public void Compare_MatchesExpectedOrdering(string version, string other, int expectedSign)
	{
		ProductVersion left = ProductVersionParser.Parse(version: version);
		ProductVersion right = ProductVersionParser.Parse(version: other);

		int actual = ProductVersionComparer.Instance.Compare(left, right);

		Assert.Equal(expectedSign, Math.Sign(actual));
		// Antisymmetry for this pair specifically (full antisymmetry/transitivity over
		// a generated set is covered by ProductVersionPropertyTests).
		Assert.Equal(-expectedSign, Math.Sign(ProductVersionComparer.Instance.Compare(right, left)));
	}

	[Fact]
	public void Compare_NeverThrows_OnWildlyMalformedInput()
	{
		string[] garbage = ["", "...", "v", "+", "-", "9..1", "a.b.c", "9.1.0.0100-", "----"];
		foreach (string a in garbage)
		{
			foreach (string b in garbage)
			{
				ProductVersion left = ProductVersionParser.Parse(a);
				ProductVersion right = ProductVersionParser.Parse(b);
				ProductVersionComparer.Instance.Compare(left, right);
			}
		}
	}

	[Fact]
	public void Compare_NullVersions_NeverThrows()
	{
		Assert.Equal(0, ProductVersionComparer.Instance.Compare(null, null));
		Assert.True(ProductVersionComparer.Instance.Compare(null, ProductVersionParser.Parse("9.0")) < 0);
		Assert.True(ProductVersionComparer.Instance.Compare(ProductVersionParser.Parse("9.0"), null) > 0);
	}

	[Fact]
	public void Parse_VkrTripleDash_NormalizesToPlusForProductVkr()
	{
		// Library item names encode '+' as '---' on disk (issue #1031); the signed
		// catalog's productVersion strings already use the real '+' form. Both must
		// parse to the same numeric+tag shape when the caller names product "VKR".
		ProductVersion fromItemName = ProductVersionParser.Parse("1.25.7---vmware.3-fips.1-tkg.1", product: "VKR");
		ProductVersion fromCatalog = ProductVersionParser.Parse("1.25.7+vmware.3-fips.1-tkg.1", product: "VKR");

		Assert.True(fromItemName.IsParsed);
		Assert.Equal(0, ProductVersionComparer.Instance.Compare(fromItemName, fromCatalog));
	}
}
