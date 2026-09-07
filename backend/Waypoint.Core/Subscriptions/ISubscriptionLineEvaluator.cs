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

namespace Waypoint.Core.Subscriptions;

/// <summary>Outcome of asking whether one candidate version falls within a subscription's tracked line.</summary>
public enum SubscriptionLineMembership
{
	/// <summary>The candidate's line matches the anchor's line at the declared granularity.</summary>
	InLine,

	/// <summary>The candidate parsed (or date-ordered) but its line does not match the anchor's.</summary>
	OutOfLine,

	/// <summary>
	/// The candidate is quarantined (undated and unparseable, epic #16 decision R2-5)
	/// -- delegated straight through from <see cref="ProductVersionClassifier"/>'s
	/// fallback ladder, never silently treated as in-line or out-of-line.
	/// </summary>
	Quarantined,
}

/// <summary>One line-membership answer, carrying the extracted lines when both were resolvable (diagnostic/display use).</summary>
/// <param name="Membership">The membership outcome.</param>
/// <param name="AnchorLine">The anchor version's extracted line at the declared granularity, or <c>null</c> when not applicable/resolvable.</param>
/// <param name="CandidateLine">The candidate version's extracted line at the declared granularity, or <c>null</c> when not applicable/resolvable.</param>
public sealed record SubscriptionLineEvaluation(SubscriptionLineMembership Membership, string? AnchorLine, string? CandidateLine);

/// <summary>
/// Answers "does this candidate version fall within a subscription's tracked line"
/// (issue #1421 AC3) -- a thin wrapper around the #1039 comparator/parser
/// (<see cref="ProductVersionParser"/>, <see cref="ProductVersionClassifier"/>,
/// <see cref="VersionLineExtractor"/>); it never reimplements version parsing or
/// ordering itself, per this issue's own Risks note ("keep the wrapper thin"). The
/// evaluation job (#1046) and cross-lane supersession (#1437) are this interface's
/// callers; wiring which subscriptions get evaluated on what schedule is their slice,
/// not this one's.
/// </summary>
public interface ISubscriptionLineEvaluator
{
	/// <summary>
	/// Evaluates <paramref name="candidateVersion"/> against <paramref name="anchorVersion"/>'s
	/// line at <paramref name="granularity"/>. A quarantined candidate (undated and
	/// unparseable) always yields <see cref="SubscriptionLineMembership.Quarantined"/>,
	/// never <see cref="SubscriptionLineMembership.InLine"/> -- automation must never
	/// silently include a quarantined version (epic #16 decision R2-5).
	/// </summary>
	/// <param name="anchorVersion">The subscription's or preset's declared anchor version string.</param>
	/// <param name="product">The catalog product key, used to select a per-product parsing rule; may be <c>null</c>.</param>
	/// <param name="granularity">The declared tracking-line granularity.</param>
	/// <param name="candidateVersion">The catalog version string being evaluated for line membership.</param>
	/// <param name="candidateCatalogReleaseDate">The candidate's catalog <c>releaseDate</c>, used only by the date-ordered fallback rung when the candidate does not parse.</param>
	SubscriptionLineEvaluation Evaluate(
		string anchorVersion,
		string? product,
		SubscriptionLineGranularity granularity,
		string candidateVersion,
		DateTimeOffset? candidateCatalogReleaseDate);
}
