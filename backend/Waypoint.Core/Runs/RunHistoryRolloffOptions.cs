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

namespace Waypoint.Core.Runs;

/// <summary>
/// Configures <c>RunHistoryRolloffHostedService</c> (issue #708, epic #706) -- a
/// periodic sweep that calls the existing <c>RunHistoryDeletionService.DeleteHistoryAsync</c>
/// (issue #592) for terminal runs whose generic-deletion gate is already satisfied.
/// It never bypasses that service's compliance-purge gate: a <c>scan</c>/<c>remediate</c>
/// run is skipped by the sweep entirely (see <c>RunHistoryRolloffHostedService</c>'s doc
/// comment for the "windowed, not deleted" rationale), same as an already-deleted run.
///
/// <see cref="Enabled"/> defaults to <c>false</c> -- deliberately conservative. Epic
/// #706's Design section only commits to a *configurable* retention policy, and
/// deleting operational history (even the gate-respecting, non-compliance kind) is a
/// destructive, irreversible-in-place action (the tombstone records that it happened,
/// not what was in the row); an operator opts in once they have decided how long they
/// want non-compliance job history retained, rather than the appliance silently
/// discarding it on a default schedule the day this ships.
/// </summary>
public sealed class RunHistoryRolloffOptions
{
	public const string SectionName = "RunHistoryRolloff";

	/// <summary>Master switch. Off by default -- see this class's doc comment.</summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// A terminal run older than this (by <c>completed_at</c>, falling back to
	/// <c>created_at</c> for a terminal row with no <c>completed_at</c> -- see the
	/// sweep's query) becomes eligible for roll-off. Default 90 days: long enough to
	/// cover a typical incident-investigation window for operational (non-compliance)
	/// job history -- discovery/credential-test/download/catalog/content/transfer/
	/// update runs -- without the operator having tuned anything yet.
	/// </summary>
	public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(90);

	/// <summary>
	/// Optional cap on how many eligible runs' history to delete in a single sweep
	/// pass -- bounds one pass's write volume/lock time on a large backlog (e.g. the
	/// first sweep after enabling roll-off against months of accumulated history)
	/// rather than deleting an unbounded number of rows in one go. Default 500.
	/// </summary>
	public int MaxRunsPerSweep { get; set; } = 500;

	/// <summary>How often <c>RunHistoryRolloffHostedService</c> sweeps.</summary>
	public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);
}
