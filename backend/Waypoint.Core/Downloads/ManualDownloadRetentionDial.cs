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

namespace Waypoint.Core.Downloads;

/// <summary>
/// The three-state manual/ad-hoc download retention dial (issue #1440, epic #1182,
/// split from design record #1047, approved design #16 section 2: "a configurable
/// dial governs manual downloads"). Its wire representation is
/// <c>download_retention_policies.manual_download_dial_default</c> (migration 0107,
/// issue #1406) -- this type is the typed, validated domain-layer counterpart to
/// that column's raw <see cref="ManualDownloadDialOptions"/> string, the same
/// "C# owns the enumeration, the CHECK only bounds the value set" split
/// <see cref="RetainedContentStateTransitions"/> already establishes for
/// <c>download_retained_content_state.state</c>.
/// </summary>
public enum ManualDownloadDial
{
	/// <summary>Manual downloads are eligible for the normal grace/auto-prune lifecycle, same as any other tracked content.</summary>
	AutoPrune,

	/// <summary>Manual downloads are never auto-pruned by the sweep -- an explicit Admin action is required to remove them.</summary>
	Keep,

	/// <summary>Manual downloads are never auto-pruned and are surfaced on the review list (<see cref="IReviewListService"/>) for explicit Admin disposition.</summary>
	Review,
}

/// <summary>
/// Parses and resolves <see cref="ManualDownloadDial"/> against the persisted
/// <see cref="RetentionPolicy.ManualDownloadDialDefault"/> string -- the one place
/// in the domain layer that translates between the two representations, so no
/// caller re-implements the mapping (or drifts from
/// <c>download_retention_policies_dial_check</c>, migration 0107) itself.
/// </summary>
public static class ManualDownloadRetentionDialResolver
{
	/// <summary>
	/// Parses <paramref name="wireValue"/> (one of <see cref="ManualDownloadDialOptions"/>'s
	/// three constants) into the typed <see cref="ManualDownloadDial"/>. Throws
	/// <see cref="ArgumentException"/> for anything else -- including a value that
	/// somehow bypassed <c>download_retention_policies_dial_check</c> -- rather than
	/// silently defaulting, since a silent default here would misresolve which
	/// manual downloads are protected from auto-prune.
	/// </summary>
	public static ManualDownloadDial Parse(string wireValue)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(wireValue);

		return wireValue switch
		{
			ManualDownloadDialOptions.AutoPrune => ManualDownloadDial.AutoPrune,
			ManualDownloadDialOptions.Keep => ManualDownloadDial.Keep,
			ManualDownloadDialOptions.Review => ManualDownloadDial.Review,
			_ => throw new ArgumentException($"'{wireValue}' is not a recognized manual-download dial value; expected one of '{ManualDownloadDialOptions.AutoPrune}', '{ManualDownloadDialOptions.Keep}', '{ManualDownloadDialOptions.Review}'.", nameof(wireValue)),
		};
	}

	/// <summary>The inverse of <see cref="Parse"/> -- the exact string <c>download_retention_policies.manual_download_dial_default</c> stores for <paramref name="dial"/>.</summary>
	public static string ToWireValue(ManualDownloadDial dial) => dial switch
	{
		ManualDownloadDial.AutoPrune => ManualDownloadDialOptions.AutoPrune,
		ManualDownloadDial.Keep => ManualDownloadDialOptions.Keep,
		ManualDownloadDial.Review => ManualDownloadDialOptions.Review,
		_ => throw new ArgumentOutOfRangeException(nameof(dial), dial, "unrecognized ManualDownloadDial value."),
	};

	/// <summary>
	/// Resolves the effective dial for a manual/ad-hoc download governed by
	/// <paramref name="policy"/> -- today always the scope-level default
	/// (<see cref="RetentionPolicy.ManualDownloadDialDefault"/>); there is no
	/// per-artifact override in this slice (approved design #16 section 2 describes
	/// a "per-installation (or per-scope, per design)" dial, and #1406's own
	/// migration 0107 only persists the scope-level default -- see that column's
	/// doc comment). <paramref name="policy"/> is the caller's already-resolved
	/// scope policy (e.g. via <see cref="IRetentionPolicyRepository.GetByScopeKeyAsync"/>
	/// falling back to <see cref="RetentionPolicyScopes.Default"/>, the same
	/// resolution <c>RetentionSweepService</c> performs) -- this method does not
	/// re-resolve scope itself.
	/// </summary>
	public static ManualDownloadDial Resolve(RetentionPolicy policy)
	{
		ArgumentNullException.ThrowIfNull(policy);

		return Parse(policy.ManualDownloadDialDefault);
	}

	/// <summary>True when <paramref name="dial"/> exempts a manual download from the sweep's normal auto-prune pass -- <see cref="ManualDownloadDial.Keep"/> and <see cref="ManualDownloadDial.Review"/> both do; only <see cref="ManualDownloadDial.AutoPrune"/> does not.</summary>
	public static bool SkipsAutoPrune(ManualDownloadDial dial) => dial != ManualDownloadDial.AutoPrune;

	/// <summary>True when <paramref name="dial"/> means the manual download belongs on <see cref="IReviewListService"/>'s review list for explicit Admin disposition -- <see cref="ManualDownloadDial.Review"/> only.</summary>
	public static bool RequiresReview(ManualDownloadDial dial) => dial == ManualDownloadDial.Review;
}
