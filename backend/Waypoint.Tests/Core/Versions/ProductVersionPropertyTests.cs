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
/// A total order must be reflexive, antisymmetric in sign, and transitive over every
/// pair/triple it is asked to compare -- checked exhaustively (all pairs, all triples)
/// over the generated set below, which is the property half of issue #1039's testing
/// (the table half is <c>ProductVersionComparerTests.Rows</c>).
/// <para>
/// What the set actually spans, stated precisely because round 1's finding F4 was that
/// the previous version of this comment overclaimed: every version shape the table
/// tests cover -- dotted 2/3/4-segment (#1027 depot, #1027 Q4 vcfManifest, #1028
/// ESX/UMDS), zero-padded and oversized build suffixes (<c>00900</c>,
/// <c>9.1.0.100000</c>), the Photon "10 vs 2" branch axis and the tdnf
/// version-release shape <c>3.6.5-2</c> (#1029), large build-id tags
/// (<c>13.1.0-25218885</c>), VKR's <c>+vmware.N-fips.N-tkg.N</c> metadata and its
/// on-disk <c>---</c> encoding (#1031), the <c>v</c>-prefixed form, tag case pairs,
/// and deliberately unparseable strings (<c>N/A</c>, <c>TBD</c>, <c>""</c>,
/// <c>a.b.c</c>, <c>----</c>) so the comparer is exercised over inputs it cannot make
/// sense of, not just the ones it can.
/// </para>
/// <para>
/// It additionally spans the two axes the round-1 review found missing.
/// (a) <b>Digit-initial alphanumeric tag tokens</b> -- <c>5e3f</c>, <c>9a</c>,
/// <c>21AF26D3</c> -- interleaved with the plain numeric tokens <c>9</c>/<c>10</c> and
/// with leading-zero forms (<c>09</c>, <c>0100</c>). This is the exact shape that made
/// the old comparer intransitive (finding F1), and
/// <see cref="Compare_DigitInitialAlphanumericTag_IsTransitive_Issue1039F1Regression"/>
/// pins the reviewer's three-way probe as a named regression case.
/// (b) <b>Per-product parses through
/// <see cref="ProductVersionParser.ShapeRules"/></b> -- every product key in that table
/// (asserted by <see cref="Generated_CoversEveryShapeRuleProduct"/>, so adding a rule
/// without extending this set fails), plus an unknown product key to cover the
/// identity fallback and a null product for the no-context path.
/// </para>
/// </summary>
public sealed class ProductVersionPropertyTests
{
	// Every entry is written through this named-argument wrapper rather than as a bare
	// array literal: several entries are real four-dotted-segment vendor version
	// strings whose octets are all <=255, which is shape-identical to an IPv4 literal
	// (issue #1694's trap). `.github/sanitize/scan_repo_specific.py`'s
	// IPv4 detector waives a candidate quad only when the literal word "version"
	// (colon, optional space, optional quote) sits immediately before it -- the named
	// argument `version:` below is exactly that form, verified clean by the scanner
	// before this file's introduction.
	private static (string Text, string? Product) Case(string version, string? product = null) => (version, product);

	private static readonly (string Text, string? Product)[] GeneratedCases =
	[
		// -- #572's misranking pair and the dotted-numeric core --
		Case(version: "9.10"),
		Case(version: "9.9"),
		Case(version: "9.0"),
		Case(version: "9.1"),
		Case(version: "9.1.0"),
		Case(version: "9.01.0"),
		Case(version: "9.1.1"),
		Case(version: "13.0.10"),
		Case(version: "13.0.5"),
		Case(version: "13.1.0"),
		// -- Photon branch axis (#1029): the "10 vs 2" lexical trap --
		Case(version: "2.0"),
		Case(version: "10.0"),
		Case(version: "5.0"),
		Case(version: "4.0"),
		// -- tdnf package version-release shape (#1029) --
		Case(version: "3.6.5-2"),
		Case(version: "3.5.2-1"),
		// -- zero-padded and oversized build suffixes (#1027) --
		Case(version: "8.0.3.00900"),
		Case(version: "8.0.3.00901"),
		Case(version: "9.1.0.0100"),
		Case(version: "9.1.0.0200"),
		Case(version: "9.1.0.99999"),
		Case(version: "9.1.0.100000"),
		Case(version: "9.1.0.255"),
		Case(version: "9.1.0.256"),
		// -- four dotted segments, all octets <=255 (#1027 Q4 vcfManifest, #1028 ESX) --
		Case(version: "4.4.1.0"),
		Case(version: "5.2.4.0"),
		Case(version: "9.0.1.0"),
		Case(version: "9.0.2.0"),
		Case(version: "9.1.0.0100-1"),
		Case(version: "9.1.0.0100-2"),
		// -- large build-id tags, numeric not lexical --
		Case(version: "13.1.0-25218885"),
		Case(version: "13.1.0-25370933"),
		// -- release vs pre-release, and tag case pairs --
		Case(version: "1.0.0"),
		Case(version: "1.0.0-rc1"),
		Case(version: "1.0.0-RC1"),
		Case(version: "1.0.0-alpha.1"),
		Case(version: "1.0.0-alpha.beta"),
		// -- F1: digit-initial alphanumeric tag tokens interleaved with numeric ones.
		// `5e3f`/`21AF26D3` are semver's own build-metadata shape (a git hash); the old
		// comparer ranked build.9 < build.10 < build.5e3f < build.9, an intransitive cycle.
		Case(version: "1.0.0+build.9"),
		Case(version: "1.0.0+build.10"),
		Case(version: "1.0.0+build.09"),
		Case(version: "1.0.0+build.0100"),
		Case(version: "1.0.0+build.100"),
		Case(version: "1.0.0+build.5e3f"),
		Case(version: "1.0.0+build.9a"),
		Case(version: "1.0.0+build.1a"),
		Case(version: "1.0.0+21AF26D3"),
		Case(version: "1.0.0+21af26d3"),
		Case(version: "1.0.0-0100"),
		Case(version: "1.0.0-ph4"),
		// -- VKR (#1031): `+vmware.N` metadata, `v` prefix, and the on-disk `---` form
		// parsed with product context so ShapeRules is exercised by the property check --
		Case(version: "1.25.7+vmware.3-fips.1-tkg.1"),
		Case(version: "1.36.1+vmware.4-vkr.5"),
		Case(version: "v1.35.2+vmware.1-vkr.3"),
		Case(version: "1.35.2+vmware.2-vkr.1"),
		Case(version: "1.25.7---vmware.3-fips.1-tkg.1", product: "VKR"),
		Case(version: "1.25.7+vmware.3-fips.1-tkg.1", product: "VKR"),
		Case(version: "1.36.1---vmware.4-vkr.5", product: "VKR"),
		Case(version: "v1.35.2---vmware.1-vkr.3", product: "vkr"),
		// -- the same on-disk form WITHOUT product context: no rule applies, so it stays
		// unparsed; and an unknown product key exercising the identity fallback --
		Case(version: "1.25.7---vmware.3-fips.1-tkg.1"),
		Case(version: "9.1.0.0100", product: "VCENTER"),
		Case(version: "13.0.10", product: "ESX"),
		// -- deliberately unparseable / malformed input --
		Case(version: "N/A"),
		Case(version: "TBD"),
		Case(version: ""),
		Case(version: "a.b.c"),
		Case(version: "9..1"),
		Case(version: "----"),
		Case(version: "9.1.0.0100-", product: "VKR"),
	];

	private static ProductVersion[] Generated() =>
		[.. GeneratedCases.Select(c => ProductVersionParser.Parse(c.Text, c.Product))];

	private static string Describe(ProductVersion v) => $"{v.Original}[{v.Product ?? "-"}]";

	[Fact]
	public void Generated_IsLargeEnough_AndCoversDigitInitialAlphanumericTags()
	{
		// The triple check below is only meaningful over a set wide enough to mix the
		// shapes; 60 is the floor this issue's brief set.
		Assert.True(GeneratedCases.Length >= 60, $"generated set has {GeneratedCases.Length} entries, expected >= 60");
		Assert.Contains(GeneratedCases, c => c.Text.EndsWith("5e3f", StringComparison.Ordinal));
		Assert.Contains(GeneratedCases, c => c.Text.EndsWith("9a", StringComparison.Ordinal));
	}

	[Fact]
	public void Generated_CoversEveryShapeRuleProduct()
	{
		// Fails the moment a product-specific parsing rule is added without a
		// corresponding entry here, which is what kept ShapeRules out of the round-0
		// property check entirely (finding F4).
		foreach (string product in ProductVersionParser.ShapeRules.Keys)
		{
			Assert.Contains(
				GeneratedCases,
				c => string.Equals(c.Product, product, StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void Compare_IsReflexive_OverGeneratedSet()
	{
		foreach (ProductVersion v in Generated())
		{
			Assert.Equal(0, ProductVersionComparer.Instance.Compare(v, v));
		}
	}

	[Fact]
	public void Compare_IsAntisymmetric_OverEveryGeneratedPair()
	{
		ProductVersion[] versions = Generated();
		foreach (ProductVersion a in versions)
		{
			foreach (ProductVersion b in versions)
			{
				int forward = Math.Sign(ProductVersionComparer.Instance.Compare(a, b));
				int backward = Math.Sign(ProductVersionComparer.Instance.Compare(b, a));
				Assert.Equal(-forward, backward);
			}
		}
	}

	[Fact]
	public void Compare_IsTransitive_OverEveryGeneratedTriple()
	{
		ProductVersion[] versions = Generated();
		foreach (ProductVersion a in versions)
		{
			foreach (ProductVersion b in versions)
			{
				foreach (ProductVersion c in versions)
				{
					int ab = ProductVersionComparer.Instance.Compare(a, b);
					int bc = ProductVersionComparer.Instance.Compare(b, c);
					int ac = ProductVersionComparer.Instance.Compare(a, c);

					if (ab <= 0 && bc <= 0)
					{
						Assert.True(ac <= 0, $"transitivity violated: {Describe(a)} <= {Describe(b)} <= {Describe(c)} but not {Describe(a)} <= {Describe(c)}");
					}
					if (ab >= 0 && bc >= 0)
					{
						Assert.True(ac >= 0, $"transitivity violated: {Describe(a)} >= {Describe(b)} >= {Describe(c)} but not {Describe(a)} >= {Describe(c)}");
					}
				}
			}
		}
	}

	[Fact]
	public void Compare_IsConsistentWithEquality_OverEveryGeneratedPair()
	{
		// The direction that must hold for a well-formed comparer: equal values rank
		// equal. The converse deliberately does NOT hold -- rank ties are documented on
		// ProductVersionComparer (segment padding, tag case, tag delimiter shape, and
		// all unparseable strings) -- so this asserts equality-implies-tie plus the
		// weaker consistency that a tie is symmetric and transitive (already covered
		// above), not identity.
		ProductVersion[] versions = Generated();
		foreach (ProductVersion a in versions)
		{
			foreach (ProductVersion b in versions)
			{
				if (a.Equals(b))
				{
					Assert.Equal(0, ProductVersionComparer.Instance.Compare(a, b));
				}
			}
		}
	}

	[Fact]
	public void Compare_DigitInitialAlphanumericTag_IsTransitive_Issue1039F1Regression()
	{
		// The round-1 reviewer's three-way probe, pinned verbatim. Against the old
		// comparer this read -1 / -1 / +1: build.9 < build.10 < build.5e3f < build.9.
		// The fixed rule puts every all-digit token below every alphanumeric one, so
		// build.9 and build.10 both rank below build.5e3f and the cycle cannot form.
		ProductVersion nine = ProductVersionParser.Parse("1.0.0+build.9");
		ProductVersion ten = ProductVersionParser.Parse("1.0.0+build.10");
		ProductVersion hash = ProductVersionParser.Parse("1.0.0+build.5e3f");

		Assert.Equal(-1, Math.Sign(ProductVersionComparer.Instance.Compare(nine, ten)));
		Assert.Equal(-1, Math.Sign(ProductVersionComparer.Instance.Compare(ten, hash)));
		Assert.Equal(-1, Math.Sign(ProductVersionComparer.Instance.Compare(nine, hash)));
	}

	[Fact]
	public void Sort_OverShuffledGeneratedSet_NeverThrows_AndIsStableInRank()
	{
		// .NET's introsort throws InvalidOperationException ("IComparer.Compare() method
		// returns inconsistent results") when handed an intransitive comparer, and which
		// permutation triggers it depends on the input order -- so shuffle and sort 100
		// times rather than once. Every run must also produce the same rank sequence.
		ProductVersion[] versions = Generated();
		int[]? expectedRanks = null;

		for (int iteration = 0; iteration < 100; iteration++)
		{
			Random rng = new(iteration);
			ProductVersion[] shuffled = [.. versions.OrderBy(_ => rng.Next())];

			ProductVersion[] sorted = [.. shuffled];
			Array.Sort(sorted, ProductVersionComparer.Instance);
			ProductVersion[] byLinq = [.. shuffled.OrderBy(v => v, ProductVersionComparer.Instance)];

			for (int i = 1; i < sorted.Length; i++)
			{
				Assert.True(
					ProductVersionComparer.Instance.Compare(sorted[i - 1], sorted[i]) <= 0,
					$"sorted output out of order at {i}: {Describe(sorted[i - 1])} then {Describe(sorted[i])}");
			}

			int[] ranks = [.. sorted.Select(v => Array.FindIndex(sorted, w => ProductVersionComparer.Instance.Compare(v, w) == 0))];
			expectedRanks ??= ranks;
			Assert.Equal(expectedRanks, ranks);
			Assert.Equal(
				sorted.Select(v => ProductVersionComparer.Instance.Compare(v, v)),
				byLinq.Select(v => ProductVersionComparer.Instance.Compare(v, v)));
			for (int i = 0; i < sorted.Length; i++)
			{
				Assert.Equal(0, ProductVersionComparer.Instance.Compare(sorted[i], byLinq[i]));
			}
		}
	}
}
