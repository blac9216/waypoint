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

namespace Waypoint.Core.Versions;

/// <summary>
/// Total order over <see cref="ProductVersion"/> (issue #1039, closes #572's ordinal
/// misranking, e.g. <c>9.10</c> sorting before <c>9.9</c>). Compares numeric segments
/// element-wise (shorter lists zero-padded, so <c>9.1</c> == <c>9.1.0</c>), then falls
/// back to a natural/version-aware comparison of <see cref="ProductVersion.Tag"/> when
/// every numeric segment is equal -- research on lane #1031 found VKR entries that
/// share every numeric segment and even the same catalog releaseDate, so the tag
/// comparison is load-bearing, not cosmetic. Never throws: an unparsed version (empty
/// segments, null tag) simply sorts as the all-zero version with no tag.
/// <para>
/// The order is a <em>total preorder</em>: reflexive, antisymmetric in sign
/// (<c>sign(Compare(a,b)) == -sign(Compare(b,a))</c>), and transitive over every
/// triple, including garbage input -- so <see cref="Array.Sort(Array)"/> and
/// <c>OrderBy</c> can never throw
/// <see cref="InvalidOperationException"/> on it. It is deliberately <em>not</em>
/// consistent with equality: <c>Compare(a,b) == 0</c> means "same rank", not "same
/// string". Documented, table-pinned ties are segment-count padding
/// (<c>9.1</c> vs <c>9.1.0</c>), tag case (<c>-RC1</c> vs <c>-rc1</c>), tag delimiter
/// shape (<c>+a.b</c> vs <c>-a-b</c> tokenise identically), and every pair of
/// unparseable strings (<c>N/A</c> vs <c>TBD</c>), which all rank equal by design.
/// <see cref="IComparer{T}"/> carries no consistency-with-equals obligation; callers
/// that must not rank unparseable versions at all use
/// <see cref="ProductVersionClassifier"/> instead of this comparer directly.
/// </para>
/// </summary>
public sealed class ProductVersionComparer : IComparer<ProductVersion>
{
	public static readonly ProductVersionComparer Instance = new();

	public int Compare(ProductVersion? x, ProductVersion? y)
	{
		if (ReferenceEquals(x, y))
		{
			return 0;
		}
		if (x is null)
		{
			return -1;
		}
		if (y is null)
		{
			return 1;
		}

		int segmentCount = Math.Max(x.NumericSegments.Count, y.NumericSegments.Count);
		for (int i = 0; i < segmentCount; i++)
		{
			long a = i < x.NumericSegments.Count ? x.NumericSegments[i] : 0;
			long b = i < y.NumericSegments.Count ? y.NumericSegments[i] : 0;
			int cmp = a.CompareTo(b);
			if (cmp != 0)
			{
				return cmp;
			}
		}

		return CompareTag(x.Tag, y.Tag);
	}

	/// <summary>
	/// A version with no tag ranks after one with a tag at the same numeric position
	/// (the untagged form reads as the "plain"/released form; semver's pre-release
	/// precedence rule is the closest existing convention and this repo has no reason
	/// to invent a different one). Two tags are compared token-by-token
	/// (<see cref="CompareNaturalTokens"/>) rather than ordinally, so
	/// <c>vmware.3-fips.1-tkg.1</c> orders below <c>vmware.4-vkr.5</c> on the numeric
	/// build discriminator instead of a lexical accident.
	/// </summary>
	private static int CompareTag(string? x, string? y)
	{
		if (x is null && y is null)
		{
			return 0;
		}
		if (x is null)
		{
			return 1;
		}
		if (y is null)
		{
			return -1;
		}
		if (string.Equals(x, y, StringComparison.Ordinal))
		{
			return 0;
		}

		return CompareNaturalTokens(x, y);
	}

	/// <summary>
	/// Splits both tags on any run of non-alphanumeric characters and compares
	/// token-by-token with <see cref="CompareToken"/>, lexicographically: the first
	/// differing token decides, and a tag that is a token-wise prefix of the other
	/// sorts first, mirroring semver's "fewer fields is lower precedence" rule. A
	/// lexicographic order built on a total order over tokens is itself total, which is
	/// what makes the whole comparer safe for <c>Array.Sort</c>/<c>OrderBy</c>.
	/// </summary>
	private static int CompareNaturalTokens(string x, string y)
	{
		string[] xTokens = TagTokenPattern.Split(x).Where(t => t.Length > 0).ToArray();
		string[] yTokens = TagTokenPattern.Split(y).Where(t => t.Length > 0).ToArray();

		int count = Math.Max(xTokens.Length, yTokens.Length);
		for (int i = 0; i < count; i++)
		{
			if (i >= xTokens.Length)
			{
				return -1;
			}
			if (i >= yTokens.Length)
			{
				return 1;
			}

			int cmp = CompareToken(xTokens[i], yTokens[i]);
			if (cmp != 0)
			{
				return cmp;
			}
		}

		return 0;
	}

	/// <summary>
	/// Orders one tag token against another under a rule that is total by construction.
	/// Every token is classified first as <em>numeric</em> (all ASCII digits) or
	/// <em>alphanumeric</em> (anything else -- tokens never contain a separator, since
	/// <see cref="TagTokenPattern"/> split them out). The classes never interleave:
	/// <b>numeric sorts before alphanumeric</b>, always. That class rule is the fix for
	/// issue #1039's round-1 finding F1 -- comparing a pair numerically only when both
	/// sides happened to parse as an integer, and lexically otherwise, made the order
	/// intransitive the moment a digit-initial alphanumeric token such as semver's own
	/// <c>+21AF26D3</c> build metadata (or <c>5e3f</c>, <c>9a</c>) appeared:
	/// <c>build.9 &lt; build.10</c> and <c>build.10 &lt; build.5e3f</c> yet
	/// <c>build.9 &gt; build.5e3f</c>. Numeric-before-alphanumeric is semver's own
	/// precedence rule ("numeric identifiers always have lower precedence than
	/// non-numeric identifiers"), so this repo is not inventing a convention.
	/// <list type="bullet">
	/// <item>numeric vs numeric: by value, compared as digit strings (leading zeros
	/// stripped, then length, then ordinal) so a build id far wider than
	/// <see cref="long"/> can never overflow; equal values with different leading-zero
	/// padding tie-break on the raw token length, fewer zeros first, purely so the
	/// result is deterministic (semver forbids leading zeros outright, so no vendor
	/// shape depends on which way this falls).</item>
	/// <item>alphanumeric vs alphanumeric: <see cref="StringComparison.OrdinalIgnoreCase"/>,
	/// which is transitive and keeps the deliberate, table-pinned <c>-RC1</c> ==
	/// <c>-rc1</c> tie (vendors vary the case of the same build; ranking them apart on
	/// case would be an accident, not information).</item>
	/// </list>
	/// </summary>
	private static int CompareToken(string x, string y)
	{
		bool xIsNumeric = IsNumeric(x);
		bool yIsNumeric = IsNumeric(y);
		if (xIsNumeric != yIsNumeric)
		{
			return xIsNumeric ? -1 : 1;
		}

		if (!xIsNumeric)
		{
			return Math.Sign(string.Compare(x, y, StringComparison.OrdinalIgnoreCase));
		}

		string xDigits = x.TrimStart('0');
		string yDigits = y.TrimStart('0');
		if (xDigits.Length != yDigits.Length)
		{
			return xDigits.Length < yDigits.Length ? -1 : 1;
		}

		int digitCmp = Math.Sign(string.CompareOrdinal(xDigits, yDigits));
		return digitCmp != 0 ? digitCmp : Math.Sign(x.Length.CompareTo(y.Length));
	}

	/// <summary>True when every character is an ASCII digit (an empty token is not numeric; the split never yields one).</summary>
	private static bool IsNumeric(string token)
	{
		if (token.Length == 0)
		{
			return false;
		}

		foreach (char c in token)
		{
			if (c is < '0' or > '9')
			{
				return false;
			}
		}

		return true;
	}

	private static readonly System.Text.RegularExpressions.Regex TagTokenPattern =
		new(@"[^A-Za-z0-9]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
