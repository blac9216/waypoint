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
/// Issue #784 (epic #726): Admin-only place/remove of a retention hold on a terminal
/// compliance run's evidence graph. Validation lives here (run existence, terminal
/// state, compliance run type); the actual insert/delete + audit write is
/// <see cref="IRunRetentionHoldRepository"/>'s job, mirroring the
/// <see cref="RunPurgeService"/>/<see cref="IRunPurgeRepository"/> split.
///
/// Placing a hold is not only a database write: it must also STOP a purge that is
/// already in flight, because the artifact-deletion job may already be sitting in the
/// queue by the time the hold lands and the compliance-runner cannot re-check the hold
/// itself (migration 0075 withholds every grant on <c>run_retention_holds</c> from both
/// runner roles, deliberately). <see cref="PlaceHoldAsync"/> therefore cancels that job
/// at hold time. That cancel is fully effective only while the job is still QUEUED; once
/// a runner has claimed it, cancellation is cooperative and best-effort -- see
/// <see cref="RunPurgeOutcome.Held"/> for the exact, and deliberately limited, mid-purge
/// guarantee, case by case.
/// </summary>
public sealed class RunRetentionHoldService
{
	private readonly IJobControlRepository _jobs;
	private readonly IRunRetentionHoldRepository _holds;
	private readonly IRunPurgeRepository _purges;

	public RunRetentionHoldService(IJobControlRepository jobs, IRunRetentionHoldRepository holds, IRunPurgeRepository purges)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(holds);
		ArgumentNullException.ThrowIfNull(purges);

		_jobs = jobs;
		_holds = holds;
		_purges = purges;
	}

	public Task<RunRetentionHold?> GetHoldAsync(Guid runId, CancellationToken cancellationToken) =>
		_holds.GetAsync(runId, cancellationToken);

	/// <summary>
	/// Places a hold on a completed compliance run (AC1: "An Admin can place a
	/// reasoned retention hold on a completed scan run"). Rejects a non-existent run,
	/// a non-terminal run, and a non-compliance run type before ever touching the
	/// repository -- <paramref name="reason"/>'s own non-blank requirement is enforced
	/// by the repository/table CHECK, but the caller (RunsController) also validates
	/// it before calling so a blank reason never reaches here.
	/// </summary>
	public async Task<PlaceRetentionHoldResult> PlaceHoldAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		RunSummary? run = await _jobs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			return new PlaceRetentionHoldResult(PlaceRetentionHoldOutcome.RunNotFound);
		}

		if (!RunLifecycle.ComplianceRunTypes.Contains(run.RunType))
		{
			return new PlaceRetentionHoldResult(PlaceRetentionHoldOutcome.UnsupportedRunType);
		}

		if (!RunLifecycle.TerminalRunStates.Contains(run.State))
		{
			return new PlaceRetentionHoldResult(PlaceRetentionHoldOutcome.RunNotTerminal);
		}

		bool inserted = await _holds.TryInsertAsync(runId, reason, actor, cancellationToken).ConfigureAwait(false);
		if (inserted)
		{
			await HaltInFlightArtifactDeletionAsync(runId, cancellationToken).ConfigureAwait(false);
		}

		RunRetentionHold? hold = await _holds.GetAsync(runId, cancellationToken).ConfigureAwait(false);
		return new PlaceRetentionHoldResult(inserted ? PlaceRetentionHoldOutcome.Placed : PlaceRetentionHoldOutcome.AlreadyHeld, hold);
	}

	/// <summary>
	/// Removes an active hold (AC4: "after which normal retention eligibility
	/// resumes" -- nothing else to do here, since <see cref="RunPurgeService.PurgeRunAsync"/>
	/// re-checks <see cref="IRunRetentionHoldRepository.GetAsync"/> fresh on every
	/// call rather than caching hold state anywhere).
	/// </summary>
	public async Task<RemoveRetentionHoldResult> RemoveHoldAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		RunSummary? run = await _jobs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			return new RemoveRetentionHoldResult(RemoveRetentionHoldOutcome.RunNotFound);
		}

		bool removed = await _holds.TryRemoveAsync(runId, reason, actor, cancellationToken).ConfigureAwait(false);
		return new RemoveRetentionHoldResult(removed ? RemoveRetentionHoldOutcome.Removed : RemoveRetentionHoldOutcome.NotHeld);
	}

	/// <summary>
	/// Issue #784: the runner-side enforcement point for a hold, alongside the three
	/// API-side ones -- <see cref="RunPurgeService.PurgeRunAsync"/>'s entry check
	/// (fresh/resumed API call), <see cref="RunPurgeService"/>'s pre-enqueue re-check
	/// (immediately before the artifact job is created), and
	/// <see cref="RunPurgeService.FinalizePendingAsync"/> (background finalize sweep).
	/// Those three stop the API process from advancing a purge, but neither stops an
	/// artifact-deletion job that <see cref="RunPurgeService"/> ALREADY enqueued: that
	/// job runs on the compliance-runner, which has no grant on
	/// <c>run_retention_holds</c> (migration 0075) and therefore cannot re-check the
	/// hold when it claims the job. The hold is honoured at claim time by cancelling
	/// the job here instead, using the same
	/// <see cref="IJobControlRepository.CancelJobAsync"/> primitive
	/// <c>DELETE /downloads/{id}</c> already uses.
	///
	/// Effective in full only for a job that is still QUEUED: that case moves to
	/// <c>cancelled</c> in one DB-authoritative statement, so no runner ever claims it
	/// and no artifact file is deleted. Once a runner has CLAIMED the job this is
	/// best-effort, and honestly so: the cancel records <c>cancel_requested</c>, the
	/// dispatcher cancels the handler's token at its next heartbeat tick, and
	/// <c>PurgeJobHandler</c> stops at its next per-target-job checkpoint -- files
	/// deleted before that point, possibly all of them, are already gone and cannot be
	/// restored. Nor can this un-delete the database rows the purge's first phase
	/// already committed.
	///
	/// The window BEFORE the job exists is closed elsewhere rather than here: a hold
	/// landing while the purge's database phase is still running finds no
	/// <c>artifact_job_id</c> to cancel, so <see cref="RunPurgeService"/> re-reads the
	/// hold immediately before enqueueing and refuses instead -- otherwise this method
	/// would silently cancel nothing and the in-flight purge would enqueue the deletion
	/// job after the hold was already in force.
	///
	/// What holds unconditionally, in every case: the purge is never COMPLETED while the
	/// hold stands -- the tombstone is withheld by
	/// <see cref="RunPurgeService.FinalizePendingAsync"/>'s own hold check, so the
	/// partially-purged state stays visible via <c>GET /runs/{id}/purge</c> rather than
	/// being reported as a finished purge. The outcome of the cancel is deliberately
	/// not surfaced to the caller: <see cref="PlaceRetentionHoldOutcome.Placed"/> means
	/// "the hold is in force from now on", which is true regardless of whether there
	/// was a job to cancel, whether it was still queued, or whether it had already run.
	/// </summary>
	private async Task HaltInFlightArtifactDeletionAsync(Guid runId, CancellationToken cancellationToken)
	{
		Guid? artifactJobId = await _purges.GetArtifactJobIdAsync(runId, cancellationToken).ConfigureAwait(false);
		if (artifactJobId is null)
		{
			return;
		}

		await _jobs.CancelJobAsync(artifactJobId.Value, cancellationToken).ConfigureAwait(false);
	}
}
