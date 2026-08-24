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
/// Issue #592 (epic #588, last child): the outcome of a single <c>DELETE
/// /runs/{id}/history</c> call -- <see cref="Waypoint.Infrastructure.Runs.RunHistoryDeletionService.DeleteHistoryAsync"/>
/// maps each case to a specific HTTP status in <c>RunsController</c>, the same shape
/// <see cref="RunPurgeOutcome"/> already established for <c>/runs/{id}/purge</c>.
/// </summary>
public enum RunHistoryDeletionOutcome
{
	/// <summary>No such run.</summary>
	RunNotFound,

	/// <summary>
	/// The run exists but is not in a terminal state (<c>completed</c>,
	/// <c>completed_with_failures</c>, or <c>aborted</c>) -- 409, run left untouched.
	/// </summary>
	RunNotTerminal,

	/// <summary>
	/// The run is compliance-owned (<c>scan</c>/<c>remediate</c>) and has not been
	/// purged of its domain artifacts yet (<c>runs.purged_at IS NULL</c>) -- 409,
	/// run left untouched. Epic #588's design: generic history deletion defers to
	/// the domain purge (issue #594, <c>RunPurgeService</c>) rather than deleting
	/// operational history out from under artifacts/attestations that still exist.
	/// The caller should invoke <c>POST /runs/{id}/purge</c> first (which itself
	/// tolerates being invoked before this endpoint, with no ordering requirement in
	/// the other direction).
	/// </summary>
	RequiresDomainPurgeFirst,

	/// <summary>
	/// The run's operational history was already deleted (a prior call's tombstone
	/// exists) -- treated as a clean idempotent no-op, not an error.
	/// </summary>
	AlreadyDeleted,

	/// <summary>Operational history deletion completed and the tombstone was written.</summary>
	Completed,
}

/// <summary>Return shape for <see cref="Waypoint.Infrastructure.Runs.RunHistoryDeletionService.DeleteHistoryAsync"/>.</summary>
public sealed record RunHistoryDeletionResult(
	RunHistoryDeletionOutcome Outcome,
	RunHistoryDeletionTombstone? Tombstone = null);

/// <summary>
/// Non-secret append-only audit record read back from
/// <c>run_history_deletion_tombstones</c> (migration 0046) -- what <c>GET
/// /runs/{id}/history</c> returns once a run's operational history has been deleted.
/// Deliberately a sibling of <see cref="RunPurgeTombstone"/>, not a shared shape --
/// see the migration's header comment for why.
/// </summary>
public sealed record RunHistoryDeletionTombstone(
	Guid Id,
	Guid RunId,
	string RunType,
	string PriorState,
	string Actor,
	string Outcome,
	string DetailJson,
	DateTimeOffset OccurredAt);
