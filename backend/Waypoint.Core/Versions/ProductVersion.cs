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

namespace Waypoint.Core.Versions;

/// <summary>
/// One product version string parsed into comparable form (issue #1039, epic #16
/// decision R2-5). Serves subscriptions, supersession, retention ranking, and presets
/// -- one core parser/comparator instead of each consumer inventing its own. <see
/// cref="Original"/> is always retained (never discarded, even when parsing fails) so
/// callers can surface the vendor's own string; <see cref="IsParsed"/> is <c>false</c>
/// when <see cref="Original"/> did not start with a recognisable dotted-numeric
/// version -- callers use <see cref="ProductVersionClassifier"/> to decide what an
/// unparsed version means (dated fallback vs quarantine), never this type alone.
/// </summary>
/// <param name="Original">The exact string as read from the vendor catalog/manifest -- never normalised, so it can be shown back to an operator unchanged.</param>
/// <param name="Product">The catalog product key (e.g. <c>VCENTER</c>, <c>VKR</c>) if known, used to select a per-product parsing rule from <see cref="ProductVersionParser.ShapeRules"/>. Null when the caller does not have product context.</param>
/// <param name="IsParsed">True when at least one leading numeric segment was recognised.</param>
/// <param name="NumericSegments">The dotted-numeric segments in order (major, minor, subminor, build, ...), as <see cref="long"/> so a five-plus-digit build suffix (e.g. the <c>00900</c> in <c>8.0.3.00900</c>) never overflows. Empty when unparsed.</param>
/// <param name="Tag">Everything after the numeric segments (pre-release/build-metadata combined, e.g. the <c>+vmware.3-fips.1-tkg.1</c> VKR carries), used only as a secondary/tertiary ordering key -- research on lane #1031 found VKR entries that share every numeric segment and a catalog releaseDate, so the tag is load-bearing for those, not cosmetic. Null when there is no such suffix.</param>
public sealed record ProductVersion(
	string Original,
	string? Product,
	bool IsParsed,
	IReadOnlyList<long> NumericSegments,
	string? Tag)
{
	/// <summary>The unparsed sentinel: no numeric segments, no tag, retains the original string.</summary>
	public static ProductVersion Unparsed(string original, string? product) =>
		new(original, product, IsParsed: false, NumericSegments: [], Tag: null);
}

/// <summary>
/// Parses vendor version strings into <see cref="ProductVersion"/>. Never throws --
/// an input that does not start with a recognisable numeric version yields an
/// <see cref="ProductVersion.IsParsed"/> <c>false</c> result, not an exception, because
/// callers (catalog indexing, presence evaluation) must keep processing the rest of a
/// batch when one entry's version string is exotic or malformed.
/// </summary>
public static class ProductVersionParser
{
	// Matches an optional leading "v" (VKR's `v1.35.2+vmware.1-vkr.3` shape), then one
	// or more dot-separated digit runs (the numeric segments), then an optional
	// remainder starting at the first '+' or '-' (pre-release/build metadata, kept as
	// one opaque tag string -- see ProductVersion.Tag).
	private static readonly Regex VersionPattern = new(
		@"^v?(?<num>\d+(?:\.\d+)*)(?<tag>[+-].*)?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>
	/// Per-product string preprocessors applied before the generic numeric-dotted
	/// parse runs, keyed by catalog product key (ordinal, case-insensitive). Extensible
	/// so a future product's quirk (a new naming era, per lane #1031's "keep the raw
	/// name so re-parsing is possible") is one table entry, not a parser rewrite.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, Func<string, string>> ShapeRules =
		new Dictionary<string, Func<string, string>>(StringComparer.OrdinalIgnoreCase)
		{
			// VKR library item names encode '+' as '---' on disk (VCSP forbids '+' in
			// item names); the signed catalog's productVersion strings use the real
			// '+' form, but normalising both through the same rule keeps callers that
			// resolve VKR versions from either source safe. Findings: issue #1031.
			["VKR"] = raw => raw.Replace("---", "+", StringComparison.Ordinal),
		};

	/// <summary>Parses <paramref name="version"/>, applying <paramref name="product"/>'s shape rule (if any). Never throws.</summary>
	public static ProductVersion Parse(string? version, string? product = null)
	{
		if (string.IsNullOrWhiteSpace(version))
		{
			return ProductVersion.Unparsed(version ?? string.Empty, product);
		}

		string normalized = product is not null && ShapeRules.TryGetValue(product, out Func<string, string>? rule)
			? rule(version)
			: version;

		Match match = VersionPattern.Match(normalized);
		if (!match.Success)
		{
			return ProductVersion.Unparsed(version, product);
		}

		string[] parts = match.Groups["num"].Value.Split('.');
		long[] segments = new long[parts.Length];
		for (int i = 0; i < parts.Length; i++)
		{
			// Each part matched \d+ so it is always parseable; long.Parse is exact
			// (never overflows on realistic version segments, e.g. the 00900 build
			// suffix in 8.0.3.00900, and even the largest observed build ids fit
			// comfortably).
			if (!long.TryParse(parts[i], out segments[i]))
			{
				return ProductVersion.Unparsed(version, product);
			}
		}

		string? tag = match.Groups["tag"].Success && match.Groups["tag"].Value.Length > 0
			? match.Groups["tag"].Value
			: null;

		return new ProductVersion(version, product, IsParsed: true, NumericSegments: segments, Tag: tag);
	}
}
