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
/// Storage for <c>run_purges</c> (durable retryable purge lifecycle) and
/// <c>run_purge_tombstones</c> (append-only completion record) -- migration 0042,
/// issue #594. Split from <see cref="Waypoint.Core.Jobs.IJobControlRepository"/> rather
/// than folded into it: purge is a compliance-domain operation layered on top of the
/// generic job/run engine, exactly the same "distinct concern, own repository" choice
/// <c>AttestationSnapshotRepository</c> and <c>Waypoint.Core.Secrets.IRunSecretStore</c>
/// already made for their own compliance-owned tables.
/// </summary>
public interface IRunPurgeRepository
{
	/// <summary>The in-flight <c>run_purges</c> row for a run, or <c>null</c> if purge was never requested.</summary>
	Task<RunPurgeStatus?> GetStatusAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Reverse lookup: the target run id whose <c>run_purges.artifact_job_id</c> equals
	/// <paramref name="artifactJobId"/>, or <c>null</c> if none matches. Used only by
	/// <c>PurgeJobHandler</c> (compliance-runner) to resolve which run its own
	/// <see cref="Jobs.JobExecutionContext.Job"/> id's outcome belongs to -- every other
	/// caller already knows the target run id directly.
	/// </summary>
	Task<Guid?> FindRunIdByArtifactJobIdAsync(Guid artifactJobId, CancellationToken cancellationToken);

	/// <summary>The completed tombstone for a run, or <c>null</c> if purge never completed.</summary>
	Task<RunPurgeTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #1013: run ids of every <c>run_purges</c> row whose two phases are both
	/// durably done (<c>db_phase_done AND artifacts_phase = 'done'</c>) but which has
	/// not been finalized (the row still exists -- finalization deletes it). These are
	/// exactly the purges stuck one step short of their tombstone because the async
	/// artifact-purge job completed after the operator's original request returned.
	/// Consumed by the API-side <c>RunPurgeFinalizeHostedService</c> sweep; a
	/// <c>failed</c> artifacts phase is deliberately NOT selected -- that state is an
	/// operator-retryable failure, not a pending finalization.
	/// </summary>
	Task<IReadOnlyList<Guid>> ListPendingFinalizeRunIdsAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Creates the <c>run_purges</c> row for a newly-requested purge. Idempotent by
	/// primary key: if a row already exists (a prior partially-completed attempt) this
	/// leaves it untouched and returns the existing row rather than overwriting its
	/// progress -- <see cref="Waypoint.Infrastructure.Runs.RunPurgeService"/> always
	/// calls <see cref="GetStatusAsync"/> first and only calls this when no row exists.
	/// </summary>
	Task<RunPurgeStatus> CreateAsync(Guid runId, string requestedBy, string priorState, CancellationToken cancellationToken);

	/// <summary>
	/// Marks the API-side synchronous phase (attestation_snapshots delete, defensive
	/// run_secrets delete, schedules.last_run_id null) as committed.
	/// </summary>
	Task MarkDbPhaseDoneAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>Records which compliance-runner job was enqueued to delete this run's artifact files, and flips the phase to <c>running</c>.</summary>
	Task MarkArtifactJobEnqueuedAsync(Guid runId, Guid jobId, int artifactsTotal, CancellationToken cancellationToken);

	/// <summary>
	/// Reports the artifact-deletion job's own outcome back into durable state (called
	/// by the compliance-runner's <c>PurgeJobHandler</c> under its own least-privilege
	/// grant, migration 0042). <paramref name="succeeded"/> false leaves the row
	/// retryable (<c>artifacts_phase = 'failed'</c>) rather than clearing progress
	/// already made.
	/// </summary>
	Task ReportArtifactOutcomeAsync(Guid runId, bool succeeded, int artifactsDeleted, string? lastError, CancellationToken cancellationToken);

	/// <summary>
	/// Deletes the (now fully-completed) <c>run_purges</c> row and writes the
	/// <c>run_purge_tombstones</c> row in one transaction, then stamps
	/// <c>runs.purged_at</c>. The row is removed rather than left around as completed
	/// bookkeeping -- the tombstone is the durable historical record from this point on
	/// (see migration 0042's design-decision comment); <c>run_purges</c> existing only
	/// means "purge in flight or needs retry".
	/// </summary>
	Task<RunPurgeTombstone> CompleteAsync(Guid runId, string runType, string actor, string priorState, int artifactsDeleted, CancellationToken cancellationToken);
}
