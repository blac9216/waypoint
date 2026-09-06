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
/// segments, null tag) simply sorts as the all-zero version with no tag, which keeps
/// the comparer total (reflexive, antisymmetric, transitive) even over garbage input --
/// callers that must not rank unparseable versions at all use
/// <see cref="ProductVersionClassifier"/> instead of this comparer directly.
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
	/// token-by-token: two tokens that both parse as integers compare numerically (so
	/// <c>vmware.4</c> &gt; <c>vmware.3</c>, not a lexical accident), everything else
	/// compares case-insensitively. A missing token (shorter tag) sorts before any
	/// token the longer tag has at that position, mirroring semver's "fewer fields is
	/// lower precedence" rule.
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

			string a = xTokens[i];
			string b = yTokens[i];
			if (long.TryParse(a, out long numA) && long.TryParse(b, out long numB))
			{
				int numericCmp = numA.CompareTo(numB);
				if (numericCmp != 0)
				{
					return numericCmp;
				}
				continue;
			}

			int cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
			if (cmp != 0)
			{
				return cmp;
			}
		}

		return 0;
	}

	private static readonly System.Text.RegularExpressions.Regex TagTokenPattern =
		new(@"[^A-Za-z0-9]+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
