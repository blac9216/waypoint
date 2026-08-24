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
/// Storage for <c>runs.history_deleted_at</c> and the append-only
/// <c>run_history_deletion_tombstones</c> table -- migration 0046, issue #592. Split
/// from <see cref="Jobs.IJobControlRepository"/> for the same reason
/// <see cref="IRunPurgeRepository"/> is: a distinct lifecycle concern layered on top
/// of the generic job/run engine, not the engine itself.
/// </summary>
public interface IRunHistoryDeletionRepository
{
	/// <summary>The completed tombstone for a run, or <c>null</c> if its history was never deleted.</summary>
	Task<RunHistoryDeletionTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// True if <c>runs.purged_at IS NOT NULL</c> for this run -- the gate
	/// <see cref="Waypoint.Infrastructure.Runs.RunHistoryDeletionService.DeleteHistoryAsync"/>
	/// checks before deleting a compliance-owned (<c>scan</c>/<c>remediate</c>) run's
	/// operational history (epic #588: generic cleanup defers to the domain purge,
	/// issue #594). Kept on this repository (rather than a bare SQL query inline in
	/// the service, or widening <see cref="Jobs.RunSummary"/>'s shared 19-column
	/// projection for one boolean) so a fake can answer it in controller-level tests
	/// without a live Postgres connection.
	/// </summary>
	Task<bool> IsPurgedAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Sets <c>runs.history_deleted_at</c> (only if still <c>NULL</c>) and inserts the
	/// tombstone row in one transaction. Idempotent: if a tombstone already exists
	/// (a racing/duplicate call), the existing row is returned rather than
	/// double-written -- <c>run_history_deletion_tombstones_run_id_key</c>'s UNIQUE
	/// constraint backstops this at the database level, matching
	/// <see cref="IRunPurgeRepository.CompleteAsync"/>'s <c>ON CONFLICT DO NOTHING</c>
	/// idiom.
	/// </summary>
	Task<RunHistoryDeletionTombstone> CompleteAsync(
		Guid runId, string runType, string actor, string priorState, CancellationToken cancellationToken);
}
