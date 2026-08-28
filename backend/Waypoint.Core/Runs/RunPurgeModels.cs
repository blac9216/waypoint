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
	/// Issue #784: the run carries an active Admin retention hold -- 409, run left
	/// untouched. Checked before every purge attempt (fresh or resumed), so a hold
	/// placed mid-purge also blocks further progress. Remove the hold
	/// (<c>DELETE /runs/{id}/retention-hold</c>) to make the run purge-eligible again.
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
