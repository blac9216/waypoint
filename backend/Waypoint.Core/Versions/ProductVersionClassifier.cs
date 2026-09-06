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
/// The fallback ladder's outcome for one version string (epic #16 decision R2-5):
/// parsed and structurally comparable; unparseable but the catalog gave a releaseDate,
/// so date-ordering stands in; or quarantined -- undated and unparseable, visible and
/// manually manageable but never fed to automation. This is a first-class outcome, not
/// a null, so a caller cannot accidentally treat "we don't know" as "there is nothing
/// here".
/// </summary>
public enum VersionClassificationOutcome
{
	Parsed,
	DateOrdered,
	Quarantined,
}

/// <summary>
/// One version string run through the fallback ladder. <see cref="Parsed"/> is set only
/// when <see cref="Outcome"/> is <see cref="VersionClassificationOutcome.Parsed"/>;
/// <see cref="CatalogReleaseDate"/> is carried through for
/// <see cref="VersionClassificationOutcome.DateOrdered"/> (and retained, harmlessly,
/// alongside a successful parse) so a caller displaying "as of" provenance never has to
/// re-fetch it.
/// </summary>
public sealed record ClassifiedProductVersion(
	string Original,
	string? Product,
	ProductVersion? Parsed,
	DateTimeOffset? CatalogReleaseDate,
	VersionClassificationOutcome Outcome);

/// <summary>
/// Implements decision R2-5's fallback ladder: parse; if that fails, fall back to the
/// catalog's own releaseDate; if there is no date either, quarantine. This is the
/// single API subscriptions (#1421), supersession (#1437), and retention ranking
/// (#1450) call to decide what a version string means before ranking it -- none of
/// those slices are wired here (out of #1039's scope), but this is the surface they
/// call.
/// </summary>
public static class ProductVersionClassifier
{
	/// <summary>Runs one version string through the fallback ladder. Never throws.</summary>
	public static ClassifiedProductVersion Classify(string? version, string? product, DateTimeOffset? catalogReleaseDate)
	{
		ProductVersion parsed = ProductVersionParser.Parse(version, product);
		if (parsed.IsParsed)
		{
			return new ClassifiedProductVersion(parsed.Original, product, parsed, catalogReleaseDate, VersionClassificationOutcome.Parsed);
		}

		return catalogReleaseDate is not null
			? new ClassifiedProductVersion(parsed.Original, product, null, catalogReleaseDate, VersionClassificationOutcome.DateOrdered)
			: new ClassifiedProductVersion(parsed.Original, product, null, null, VersionClassificationOutcome.Quarantined);
	}
}

/// <summary>
/// Total order over <see cref="ClassifiedProductVersion"/> that implements the fallback
/// ladder's ranking rule: <see cref="VersionClassificationOutcome.Parsed"/> entries rank
/// by <see cref="ProductVersionComparer"/>; <see cref="VersionClassificationOutcome.DateOrdered"/>
/// entries rank by <see cref="ClassifiedProductVersion.CatalogReleaseDate"/>; a
/// <see cref="VersionClassificationOutcome.Parsed"/> entry always outranks a
/// <see cref="VersionClassificationOutcome.DateOrdered"/> one at the same nominal
/// position, because a structural parse is strictly more trustworthy evidence than a
/// timestamp -- this is a documented assumption, not a vendor-stated rule, since no
/// source names how the two families compare against each other. Quarantined entries
/// always rank last: this comparer is total (so quarantined versions still sort
/// somewhere for display), but no automation-facing caller should ever be looking at a
/// quarantined entry to begin with -- see <see cref="ProductVersionAutomationCandidates"/>.
/// </summary>
public sealed class ClassifiedProductVersionComparer : IComparer<ClassifiedProductVersion>
{
	public static readonly ClassifiedProductVersionComparer Instance = new();

	public int Compare(ClassifiedProductVersion? x, ClassifiedProductVersion? y)
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

		int rankCmp = Rank(x.Outcome).CompareTo(Rank(y.Outcome));
		if (rankCmp != 0)
		{
			return rankCmp;
		}

		return x.Outcome switch
		{
			VersionClassificationOutcome.Parsed => ProductVersionComparer.Instance.Compare(x.Parsed, y.Parsed),
			VersionClassificationOutcome.DateOrdered => Nullable.Compare(x.CatalogReleaseDate, y.CatalogReleaseDate),
			_ => 0,
		};
	}

	private static int Rank(VersionClassificationOutcome outcome) => outcome switch
	{
		VersionClassificationOutcome.Quarantined => 0,
		VersionClassificationOutcome.DateOrdered => 1,
		VersionClassificationOutcome.Parsed => 2,
		_ => 0,
	};
}

/// <summary>
/// The one place any supersession/prune/retention-ranking caller should ask "which of
/// these versions are eligible for automation to act on, ordered newest first" --
/// enforces decision R2-5's "undated+unparseable are quarantined ... never
/// auto-pruned/superseded" as code rather than as a convention every caller has to
/// remember.
/// </summary>
public static class ProductVersionAutomationCandidates
{
	/// <summary>
	/// Filters out every <see cref="VersionClassificationOutcome.Quarantined"/> entry
	/// and orders what remains newest-first. The result is exactly the candidate set
	/// automation (supersession, retention pruning, subscription evaluation) is allowed
	/// to consider; a quarantined version never appears here, by construction.
	/// </summary>
	public static IReadOnlyList<ClassifiedProductVersion> SelectNewestFirst(IEnumerable<ClassifiedProductVersion> versions)
	{
		ArgumentNullException.ThrowIfNull(versions);

		return versions
			.Where(v => v.Outcome != VersionClassificationOutcome.Quarantined)
			.OrderByDescending(v => v, ClassifiedProductVersionComparer.Instance)
			.ToArray();
	}
}
