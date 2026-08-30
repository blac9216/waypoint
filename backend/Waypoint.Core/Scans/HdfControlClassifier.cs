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

namespace Waypoint.Core.Scans;

/// <summary>
/// The ONE rule that turns an HDF control's <c>results[].status</c> rows (plus its
/// <c>impact</c>) into a <see cref="ComponentFindingStatuses"/> value -- issue #1144
/// round 2.
///
/// Both surfaces that classify HDF controls call this and nothing else:
/// <see cref="HdfFindingsParser"/> (the persisted per-finding rows behind
/// <c>GET /runs/{id}/component-results/summary</c>) and <see cref="HdfSeverityCounter"/>
/// (the CAT preview behind <c>GET /runs/{id}/artifacts</c>). Before this existed the
/// two carried near-duplicate predicates that drifted: the counter treated only the
/// literal string <c>error</c> as an execution error and every unrecognized status as
/// OPEN, so an ordinary control with a <c>passed</c> + <c>skipped</c> result pair, or
/// one carrying a status string this reader does not know, read as
/// <c>execution_error</c> on the summary but inflated <c>cat_i/ii/iii_open</c> on the
/// artifacts preview. There is now no second copy of the rule to drift from.
///
/// The rule, worst-of first: any <c>error</c> row wins (InSpec's
/// resource-raised-an-exception outcome); then any <c>failed</c> row; then an
/// all-<c>passed</c> control passed; then an all-<c>skipped</c> control is split by
/// <c>impact</c> per issue #1124 (impact <c>0.0</c> is the profile's own
/// not-applicable decision, anything else -- including a missing/malformed impact,
/// never assumed to mean "does not apply" -- is an applicable control that could not
/// execute). Every remaining shape -- an unrecognized status string, a mix this reader
/// does not specifically recognize -- is <see cref="ComponentFindingStatuses.ExecutionError"/>,
/// never silently guessed into a clean or an open verdict ("never a fabricated clean
/// result", and equally never a fabricated failure).
/// </summary>
public static class HdfControlClassifier
{
	public const string PassedStatus = "passed";
	public const string FailedStatus = "failed";
	public const string SkippedStatus = "skipped";
	public const string ErrorStatus = "error";

	/// <summary>
	/// Classifies one control from its result statuses. An EMPTY
	/// <paramref name="resultStatuses"/> (no results array, or one with no usable rows)
	/// is the "never ran" shape: <see cref="ComponentFindingStatuses.NotReviewed"/>,
	/// exactly once -- epic #726 §6's "applicable control that cannot execute remains
	/// present exactly once as Not_Reviewed".
	/// </summary>
	public static string Classify(IReadOnlyList<string?> resultStatuses, double? impact)
	{
		ArgumentNullException.ThrowIfNull(resultStatuses);

		if (resultStatuses.Count == 0)
		{
			return ComponentFindingStatuses.NotReviewed;
		}

		if (resultStatuses.Any(s => string.Equals(s, ErrorStatus, StringComparison.Ordinal)))
		{
			return ComponentFindingStatuses.ExecutionError;
		}

		if (resultStatuses.Any(s => string.Equals(s, FailedStatus, StringComparison.Ordinal)))
		{
			return ComponentFindingStatuses.Failed;
		}

		if (resultStatuses.All(s => string.Equals(s, PassedStatus, StringComparison.Ordinal)))
		{
			return ComponentFindingStatuses.Passed;
		}

		if (resultStatuses.All(s => string.Equals(s, SkippedStatus, StringComparison.Ordinal)))
		{
			return impact is 0.0 ? ComponentFindingStatuses.NotApplicable : ComponentFindingStatuses.NotReviewed;
		}

		// Any other shape (unknown status string, or a mix this reader does not
		// specifically recognize) is uncountable-as-clean AND uncountable-as-open:
		// report ExecutionError rather than guessing.
		return ComponentFindingStatuses.ExecutionError;
	}
}
