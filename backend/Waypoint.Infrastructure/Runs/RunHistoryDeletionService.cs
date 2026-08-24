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

using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #592 (epic #588, last child): Admin-only, audited, idempotent deletion of a
/// TERMINAL run's operational records -- kept structurally separate from #594's
/// compliance-domain purge (<see cref="RunPurgeService"/>). Epic #588's Design
/// section: "Deleting operational history must not implicitly delete domain state;
/// destructive domain cleanup is explicit and separately authorized," and, in the
/// other direction, generic history cleanup must DEFER to that domain purge when
/// compliance-owned artifacts are involved -- see <see cref="DeleteHistoryAsync"/>'s
/// compliance gate below, which is this class's central design decision.
///
/// What "deletion" means here (see migration 0046's header comment for the full
/// rationale): <c>runs</c>/<c>jobs</c> rows and the <c>job_events</c> ledger are
/// NEVER deleted -- the same structural reason 0042 already established for purge
/// (job_events is append-only-by-trigger and FK'd to jobs with no cascade action).
/// <c>runs.history_deleted_at</c> marks the row's operational record deleted in
/// place; <see cref="IRunHistoryDeletionRepository.CompleteAsync"/> also nulls any
/// <c>schedules.last_run_id</c> reference in the same transaction (AC (b)) and
/// writes the <c>run_history_deletion_tombstones</c> audit row (AC (d)).
/// </summary>
public sealed class RunHistoryDeletionService
{
	/// <summary>The three terminal <c>runs.state</c> values (same set <see cref="RunPurgeService"/> uses).</summary>
	private static readonly HashSet<string> TerminalRunStates = new(StringComparer.Ordinal)
	{
		"completed", "completed_with_failures", "aborted",
	};

	/// <summary>
	/// Job families whose durable outputs are compliance-owned (docs/domain-model.md's
	/// "Durable output owner" table) -- the only two run types <see cref="RunPurgeService"/>
	/// (issue #594) is scoped to. A run of either type must be purged
	/// (<c>runs.purged_at IS NOT NULL</c>) before its operational history may be
	/// deleted, per epic #588's "generic cleanup DEFERS to domain purge" design.
	/// </summary>
	private static readonly HashSet<string> ComplianceOwnedRunTypes = new(StringComparer.Ordinal)
	{
		"scan", "remediate",
	};

	private readonly IJobControlRepository _jobs;
	private readonly IRunHistoryDeletionRepository _deletions;

	public RunHistoryDeletionService(
		IJobControlRepository jobs,
		IRunHistoryDeletionRepository deletions)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(deletions);

		_jobs = jobs;
		_deletions = deletions;
	}

	/// <summary>
	/// Returns the current history-deletion tombstone for a run, or <c>null</c> if its
	/// operational history has never been deleted. Backs <c>GET /runs/{id}/history</c>.
	/// </summary>
	public async Task<RunHistoryDeletionTombstone?> GetStatusAsync(Guid runId, CancellationToken cancellationToken) =>
		await _deletions.GetTombstoneAsync(runId, cancellationToken).ConfigureAwait(false);

	/// <summary>
	/// Deletes (or confirms already-deleted) operational history for
	/// <paramref name="runId"/>. See this class's doc comment for the compliance-purge
	/// gate and what "deletion" means.
	/// </summary>
	public async Task<RunHistoryDeletionResult> DeleteHistoryAsync(Guid runId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		RunHistoryDeletionTombstone? existing = await _deletions.GetTombstoneAsync(runId, cancellationToken).ConfigureAwait(false);
		if (existing is not null)
		{
			return new RunHistoryDeletionResult(RunHistoryDeletionOutcome.AlreadyDeleted, existing);
		}

		RunSummary? run = await _jobs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			return new RunHistoryDeletionResult(RunHistoryDeletionOutcome.RunNotFound);
		}

		if (!TerminalRunStates.Contains(run.State))
		{
			return new RunHistoryDeletionResult(RunHistoryDeletionOutcome.RunNotTerminal);
		}

		if (ComplianceOwnedRunTypes.Contains(run.RunType))
		{
			bool purged = await _deletions.IsPurgedAsync(runId, cancellationToken).ConfigureAwait(false);
			if (!purged)
			{
				return new RunHistoryDeletionResult(RunHistoryDeletionOutcome.RequiresDomainPurgeFirst);
			}
		}

		RunHistoryDeletionTombstone tombstone = await _deletions.CompleteAsync(
			runId, run.RunType, actor, priorState: run.State, cancellationToken).ConfigureAwait(false);
		return new RunHistoryDeletionResult(RunHistoryDeletionOutcome.Completed, tombstone);
	}
}
