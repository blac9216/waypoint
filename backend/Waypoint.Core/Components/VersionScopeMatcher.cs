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

using System.Globalization;

namespace Waypoint.Core.Components;

/// <summary>
/// Pure, closed two-form version-scope matcher (issue #998's CORRECTED owner decision,
/// 2026-08-28, which supersedes an earlier "minor-level keys" comment posted
/// prematurely on the same issue): the vendor's compliance-content repository is
/// HETEROGENEOUS -- some product trees declare a minor-scoped version directory
/// (<c>vsphere/8.0</c>, <c>vsphere/7.0</c>) and others declare a major-line-scoped
/// directory (<c>vcf/9.x</c>, NSX's <c>4.x</c>/<c>5.x</c> -- the vendor's own profile
/// titles say "9.X"). The catalog product-version key is therefore the vendor's
/// declared version scope, VERBATIM, whatever form that content directory declares --
/// never a Waypoint-normalized minor-level key, never a range Waypoint infers.
///
/// This class computes the ONE match rule that scope key form implies at lookup time:
/// <list type="bullet">
/// <item>a catalog key of the form <c>N.M</c> (exactly two dot-separated non-negative
/// integer segments -- "minor-scoped") matches an observed version iff the observed
/// version STARTS WITH "N.M." or equals "N.M" exactly (a bare prefix like "8.10"
/// must never match catalog key "8.1" -- see <see cref="Matches"/>'s prefix-trap
/// guard);</item>
/// <item>a catalog key of the form <c>N.x</c> (a leading non-negative integer segment,
/// case-insensitive literal "x" as the final segment -- "major-line-scoped") matches an
/// observed version iff the observed version STARTS WITH "N." or equals "N" exactly;</item>
/// <item>a catalog key with a literal "x" in a NON-final segment (e.g. "3.3.x", which the
/// doc's Workspace ONE Access `3-3-x` row declares) is still the major-line form: the
/// scope is defined by every concrete (non-"x") leading segment, so "3.3.x" matches an
/// observed version that starts with "3.3." or equals "3.3" exactly -- the trailing "x"
/// only marks that this key is a family/range declaration, not a third form;</item>
/// <item>any other key shape (empty, whitespace, more than one "x" segment, a
/// non-numeric non-"x" segment, trailing dot, etc.) is UNKNOWN and fails closed --
/// <see cref="Matches"/> returns false for every observed version, never guessing a
/// nearest-version or range interpretation (ADR-0022's "never guesses a winner"
/// extended by the CORRECTED #998 decision to key-FORM recognition itself);</item>
/// <item>an unparseable/empty/whitespace observed version also fails closed regardless
/// of key form.</item>
/// </list>
///
/// This is NOT nearest-version inference and NOT a numeric range comparison -- it is
/// exactly the scope the vendor's own directory name declares, recomputed at lookup
/// time from the two verbatim strings; no third derived fact is stored anywhere (hosts
/// keep exactly two stored facts, full observed version and build number -- see
/// <see cref="Waypoint.Core.Discovery.DiscoveredInventoryItem"/> -- the scope key lives
/// only on the catalog side).
/// </summary>
public static class VersionScopeMatcher
{
	/// <summary>
	/// Returns true iff <paramref name="observedVersion"/> falls within the declared
	/// scope of <paramref name="catalogVersionKey"/> per the closed two-form test above.
	/// Fails closed (returns false) for any unknown key form or unparseable/blank
	/// observed version -- never throws for bad input, since both values originate from
	/// external boundaries (a live vSphere <c>Version</c> string; catalog-authored seed
	/// data) that this matcher must stay defensive against.
	/// </summary>
	public static bool Matches(string? observedVersion, string? catalogVersionKey)
	{
		if (string.IsNullOrWhiteSpace(observedVersion) || string.IsNullOrWhiteSpace(catalogVersionKey))
		{
			return false;
		}

		string trimmedObserved = observedVersion.Trim();
		string trimmedKey = catalogVersionKey.Trim();

		ScopePrefix? prefix = ParseScopePrefix(trimmedKey);
		if (prefix is null)
		{
			// Unknown key form: fails closed rather than falling back to byte-equality
			// or any other guessed interpretation.
			return false;
		}

		return ObservedVersionIsWithinPrefix(trimmedObserved, prefix.Value.Segments);
	}

	/// <summary>
	/// A recognized scope key's leading concrete (non-"x") integer segments. Both closed
	/// forms reduce to "the observed version must start with these segments, in order":
	/// <c>N.M</c> yields <c>[N, M]</c>; <c>N.x</c> yields <c>[N]</c>; <c>3.3.x</c> yields
	/// <c>[3, 3]</c>.
	/// </summary>
	private readonly record struct ScopePrefix(IReadOnlyList<int> Segments);

	private static ScopePrefix? ParseScopePrefix(string key)
	{
		string[] segments = key.Split('.');
		if (segments.Length < 2)
		{
			// Neither closed form ("N.M" or "N.x") has fewer than two segments.
			return null;
		}

		bool sawWildcard = false;
		List<int> concreteSegments = [];
		for (int i = 0; i < segments.Length; i++)
		{
			string segment = segments[i];
			bool isFinalSegment = i == segments.Length - 1;

			if (isFinalSegment && string.Equals(segment, "x", StringComparison.OrdinalIgnoreCase))
			{
				sawWildcard = true;
				continue;
			}

			// A literal "x" anywhere other than the final segment, or more than one
			// wildcard, is not a recognized shape -- fail closed rather than guess.
			if (string.Equals(segment, "x", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			if (!IsNonNegativeInteger(segment))
			{
				return null;
			}

			concreteSegments.Add(int.Parse(segment, NumberStyles.None, CultureInfo.InvariantCulture));
		}

		if (concreteSegments.Count == 0)
		{
			// e.g. a bare "x" -- no concrete leading segment to scope against.
			return null;
		}

		// N.M (no wildcard) requires EXACTLY two concrete segments -- "8.0.1" or "8" are
		// not the minor-scoped form. N.x (wildcard present) accepts any number of leading
		// concrete segments ("9.x" -> [9], "3.3.x" -> [3, 3]).
		if (!sawWildcard && concreteSegments.Count != 2)
		{
			return null;
		}

		return new ScopePrefix(concreteSegments);
	}

	private static bool IsNonNegativeInteger(string segment)
	{
		if (segment.Length == 0)
		{
			return false;
		}

		foreach (char c in segment)
		{
			if (c is < '0' or > '9')
			{
				return false;
			}
		}

		// Reject a leading-zero multi-digit segment ("08") -- not a form any real vendor
		// version component uses, and accepting it would let "08.0" and "8.0" both parse
		// to the same integer while looking like different keys.
		return segment.Length == 1 || segment[0] != '0';
	}

	/// <summary>
	/// True iff <paramref name="observedVersion"/>'s own dot-separated integer segments
	/// start with exactly <paramref name="prefixSegments"/>, in order. Guards the
	/// classic prefix trap explicitly: "8.10" must never match prefix [8, 1] just
	/// because the STRING "8.10" starts with the substring "8.1" -- segments are
	/// compared as parsed integers, not raw string prefixes.
	/// </summary>
	private static bool ObservedVersionIsWithinPrefix(string observedVersion, IReadOnlyList<int> prefixSegments)
	{
		string[] observedSegments = observedVersion.Split('.');
		if (observedSegments.Length < prefixSegments.Count)
		{
			return false;
		}

		for (int i = 0; i < prefixSegments.Count; i++)
		{
			if (!IsNonNegativeInteger(observedSegments[i]))
			{
				return false;
			}

			int observedValue = int.Parse(observedSegments[i], NumberStyles.None, CultureInfo.InvariantCulture);
			if (observedValue != prefixSegments[i])
			{
				return false;
			}
		}

		return true;
	}
}
