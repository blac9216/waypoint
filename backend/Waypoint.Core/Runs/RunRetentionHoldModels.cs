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
/// Issue #784 (epic #726): an Admin-only, audited retention hold on a terminal
/// compliance run's complete evidence graph -- see migration 0075's header for the
/// presence-based table shape this projects, and <see cref="Waypoint.Infrastructure.Runs.RunPurgeService"/>'s
/// updated <c>PurgeRunAsync</c> for the exclusion this exists to back.
/// </summary>
public sealed record RunRetentionHold(
	Guid RunId,
	string Reason,
	string PlacedBy,
	DateTimeOffset PlacedAt);

/// <summary>Outcome of a single "place a hold" request -- mirrors <see cref="RunPurgeOutcome"/>'s per-case HTTP mapping shape.</summary>
public enum PlaceRetentionHoldOutcome
{
	/// <summary>No such run.</summary>
	RunNotFound,

	/// <summary>
	/// The run is not a compliance-owned run type (<c>scan</c>/<c>remediate</c>) --
	/// a hold only ever protects a compliance evidence graph, matching the same
	/// <c>scan</c>/<c>remediate</c> classification <c>RunHistoryDeletionService</c>
	/// already uses for its <c>requires_domain_purge_first</c> gate.
	/// </summary>
	UnsupportedRunType,

	/// <summary>The run exists but is not yet terminal (completed/completed_with_failures/aborted).</summary>
	RunNotTerminal,

	/// <summary>The run already has an active hold -- placing again is a no-op, not an error.</summary>
	AlreadyHeld,

	/// <summary>Hold placed and audited.</summary>
	Placed,
}

/// <summary>Outcome of a single "remove a hold" request.</summary>
public enum RemoveRetentionHoldOutcome
{
	/// <summary>No such run.</summary>
	RunNotFound,

	/// <summary>The run has no active hold to remove.</summary>
	NotHeld,

	/// <summary>Hold removed and audited; normal retention eligibility resumes.</summary>
	Removed,
}

/// <summary>Return shape for <see cref="Waypoint.Infrastructure.Runs.RunRetentionHoldService.PlaceHoldAsync"/>.</summary>
public sealed record PlaceRetentionHoldResult(PlaceRetentionHoldOutcome Outcome, RunRetentionHold? Hold = null);

/// <summary>Return shape for <see cref="Waypoint.Infrastructure.Runs.RunRetentionHoldService.RemoveHoldAsync"/>.</summary>
public sealed record RemoveRetentionHoldResult(RemoveRetentionHoldOutcome Outcome);

/// <summary>
/// Data access over <c>run_retention_holds</c> (migration 0075). One implementation
/// (<c>Waypoint.Infrastructure.Runs.RunRetentionHoldRepository</c>, plain Npgsql),
/// registered the same way <see cref="IRunPurgeRepository"/> is -- both the API
/// (<c>RunsController</c>, via <see cref="Waypoint.Infrastructure.Runs.RunRetentionHoldService"/>)
/// and <see cref="Waypoint.Infrastructure.Runs.RunPurgeService"/>'s own exclusion
/// check need it.
///
/// Deliberately carries NO bulk "list every held run id" read. Issue #1062's sweep
/// excludes held runs in its OWN candidate query with a SQL anti-join
/// (<c>WHERE NOT EXISTS (SELECT 1 FROM run_retention_holds h WHERE h.run_id = r.id)</c>),
/// which needs no C# surface here and does not depend on the held set being small
/// enough to materialise in the API process; <see cref="Waypoint.Infrastructure.Runs.RunPurgeService.PurgeRunAsync"/>'s
/// refusal remains the backstop that makes the exclusion correct even if a candidate
/// query ever forgets the anti-join.
/// </summary>
public interface IRunRetentionHoldRepository
{
	/// <summary>Current hold for a run, or <c>null</c> if the run is not held. Backs the purge-exclusion check and <c>GET /runs/{id}/retention-hold</c>.</summary>
	Task<RunRetentionHold?> GetAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Inserts the hold row and the <c>retention_hold_placed</c> audit_log row in one
	/// transaction. <c>ON CONFLICT (run_id) DO NOTHING</c> -- caller (the service)
	/// re-reads via <see cref="GetAsync"/> to distinguish "just placed" from "already
	/// held" and report <see cref="PlaceRetentionHoldOutcome.AlreadyHeld"/> without double-auditing.
	/// </summary>
	Task<bool> TryInsertAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Deletes the hold row and inserts the <c>retention_hold_removed</c> audit_log row
	/// in one transaction. Returns <c>false</c> if no row existed to delete (caller
	/// reports <see cref="RemoveRetentionHoldOutcome.NotHeld"/>).
	/// </summary>
	Task<bool> TryRemoveAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken);
}
