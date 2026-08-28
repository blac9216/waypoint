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
/// Issue #594 (epic #577): the outcome of a single <c>POST /runs/{id}/purge</c> call --
/// <see cref="Waypoint.Infrastructure.Runs.RunPurgeService.PurgeRunAsync"/> maps each
/// case to a specific HTTP status in <c>RunsController</c>, mirroring how
/// <see cref="Waypoint.Core.Jobs.CredentialSwapOutcome"/> already does this for
/// <c>resume-blocked</c>.
/// </summary>
public enum RunPurgeOutcome
{
	/// <summary>No such run.</summary>
	RunNotFound,

	/// <summary>
	/// The run exists but is not in a terminal state (<c>completed</c>,
	/// <c>completed_with_failures</c>, or <c>aborted</c>) -- 409, run left untouched.
	/// </summary>
	RunNotTerminal,

	/// <summary>
	/// The run was already fully purged (a prior call's tombstone exists) -- treated as
	/// a clean idempotent no-op, not an error.
	/// </summary>
	AlreadyPurged,

	/// <summary>
	/// The database-side phase committed (or was already done) and the artifact-deletion
	/// job was enqueued or is still in flight -- the caller should poll
	/// <c>GET /runs/{id}/purge</c> for terminal status.
	/// </summary>
	InProgress,

	/// <summary>
	/// Both phases (database projections, artifact files) are confirmed complete and the
	/// tombstone has been written. Terminal success.
	/// </summary>
	Completed,

	/// <summary>
	/// The artifact-deletion job reported at least one file it could not delete --
	/// retryable by calling purge again. The database phase, if not already done, is
	/// still committed; nothing already deleted is re-deleted on retry.
	/// </summary>
	Failed,

	/// <summary>
	/// Issue #784: the run carries an active Admin retention hold -- 409, and nothing
	/// further is deleted on this call.
	///
	/// What a hold does and does not guarantee, stated exactly (the boundary matters,
	/// because a purge is not atomic -- see <see cref="Waypoint.Infrastructure.Runs.RunPurgeService"/>'s
	/// two-phase contract):
	/// <list type="bullet">
	/// <item>A hold placed BEFORE a purge starts is fully honoured: no evidence row and
	/// no artifact file is ever deleted, and no tombstone is written. This is the
	/// normal case and the one issue #784 AC3 is about.</item>
	/// <item>A hold placed while a purge is ALREADY IN FLIGHT halts it; it cannot roll
	/// it back. Whatever the database phase already committed stays deleted -- a hold
	/// is not an undo. What the hold does guarantee is that no FURTHER deletion and no
	/// completion happens: <see cref="Waypoint.Infrastructure.Runs.RunRetentionHoldService.PlaceHoldAsync"/>
	/// cancels the enqueued artifact-deletion job at hold time (a still-queued job
	/// moves to <c>cancelled</c>, so no runner ever claims it; an already-claimed one
	/// gets <c>cancel_requested</c> and is cooperatively cancelled by the dispatcher's
	/// heartbeat), <see cref="Waypoint.Infrastructure.Runs.RunPurgeService.PurgeRunAsync"/>
	/// refuses, and <see cref="Waypoint.Infrastructure.Runs.RunPurgeService.FinalizePendingAsync"/>
	/// -- the background finalize sweep's entry point -- refuses too, so the run is
	/// never tombstoned and <c>runs.purged_at</c> is never set.</item>
	/// <item>The halted purge stays VISIBLE rather than being silently abandoned: the
	/// <c>run_purges</c> row survives, so <c>GET /runs/{id}/purge</c> keeps reporting
	/// the partially-purged state instead of presenting it as either untouched or
	/// completed. Nothing clears that row on its own -- removing the hold
	/// (<c>DELETE /runs/{id}/retention-hold</c>) and re-POSTing <c>purge</c> is the
	/// only thing that resumes and finalizes it.</item>
	/// <item>A purge that already COMPLETED is unaffected: the tombstone check runs
	/// first and returns <see cref="AlreadyPurged"/>. There is nothing left to hold.</item>
	/// </list>
	/// </summary>
	Held,
}

/// <summary>Return shape for <see cref="Waypoint.Infrastructure.Runs.RunPurgeService.PurgeRunAsync"/>.</summary>
public sealed record RunPurgeResult(
	RunPurgeOutcome Outcome,
	RunPurgeStatus? Status = null);

/// <summary>
/// The durable purge status for one run -- what both <c>POST /runs/{id}/purge</c> (on
/// every outcome except <see cref="RunPurgeOutcome.RunNotFound"/>/<see cref="RunPurgeOutcome.RunNotTerminal"/>)
/// and <c>GET /runs/{id}/purge</c> return, projected from <c>run_purges</c> (in
/// flight) or <c>run_purge_tombstones</c> (once complete).
/// </summary>
public sealed record RunPurgeStatus(
	Guid RunId,
	string RequestedBy,
	DateTimeOffset RequestedAt,
	string PriorState,
	bool DbPhaseDone,
	string ArtifactsPhase,
	int ArtifactsTotal,
	int ArtifactsDeleted,
	string? LastError,
	DateTimeOffset? CompletedAt);

/// <summary>
/// Non-secret append-only audit record read back from <c>run_purge_tombstones</c> --
/// what <c>GET /runs/{id}/purge</c> returns once a purge has fully completed.
/// </summary>
public sealed record RunPurgeTombstone(
	Guid Id,
	Guid RunId,
	string RunType,
	string PriorState,
	string Actor,
	string Outcome,
	string DetailJson,
	DateTimeOffset OccurredAt);
