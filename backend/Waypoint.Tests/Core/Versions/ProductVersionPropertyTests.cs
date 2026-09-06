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
/// A total order must be reflexive, antisymmetric, and transitive over every pair/
/// triple it is asked to compare -- checked exhaustively (all pairs, all triples) over
/// a generated set spanning every version shape this issue's table tests cover,
/// including deliberately unparseable strings, so the comparer is proven total even
/// over the inputs it cannot make sense of, not just the ones it can.
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
	private static string Version(string version) => version;

	private static readonly string[] GeneratedVersionStrings =
	[
		Version(version: "9.10"),
		Version(version: "9.9"),
		Version(version: "13.0.10"),
		Version(version: "13.0.5"),
		Version(version: "13.1.0"),
		Version(version: "8.0.3.00900"),
		Version(version: "8.0.3.00901"),
		Version(version: "9.1.0.0100"),
		Version(version: "9.1.0.0200"),
		Version(version: "4.4.1.0"),
		Version(version: "5.2.4.0"),
		Version(version: "2.0"),
		Version(version: "10.0"),
		Version(version: "5.0"),
		Version(version: "9.01.0"),
		Version(version: "9.1"),
		Version(version: "9.1.0"),
		Version(version: "9.1.1"),
		Version(version: "1.25.7+vmware.3-fips.1-tkg.1"),
		Version(version: "1.36.1+vmware.4-vkr.5"),
		Version(version: "v1.35.2+vmware.1-vkr.3"),
		Version(version: "1.0.0"),
		Version(version: "1.0.0-rc1"),
		Version(version: "1.0.0-RC1"),
		Version(version: "9.1.0.100000"),
		Version(version: "9.1.0.99999"),
		Version(version: "N/A"),
		Version(version: "TBD"),
		Version(version: ""),
		Version(version: "9.1.0.0100-1"),
		Version(version: "9.1.0.0100-2"),
	];

	private static ProductVersion[] Generated() =>
		[.. GeneratedVersionStrings.Select(v => ProductVersionParser.Parse(v))];

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
						Assert.True(ac <= 0, $"transitivity violated: {a.Original!} <= {b.Original!} <= {c.Original!} but not {a.Original!} <= {c.Original!}");
					}
					if (ab >= 0 && bc >= 0)
					{
						Assert.True(ac >= 0, $"transitivity violated: {a.Original!} >= {b.Original!} >= {c.Original!} but not {a.Original!} >= {c.Original!}");
					}
				}
			}
		}
	}
}
