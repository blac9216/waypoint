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
/// at hold time -- see <see cref="RunPurgeOutcome.Held"/> for the exact, and
/// deliberately limited, mid-purge guarantee this buys.
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
	/// Issue #784: the third enforcement point for a hold, alongside
	/// <see cref="RunPurgeService.PurgeRunAsync"/> (fresh/resumed API call) and
	/// <see cref="RunPurgeService.FinalizePendingAsync"/> (background finalize sweep).
	/// Those two stop the API process from advancing a purge, but neither stops an
	/// artifact-deletion job that <see cref="RunPurgeService"/> ALREADY enqueued: that
	/// job runs on the compliance-runner, which has no grant on
	/// <c>run_retention_holds</c> (migration 0075) and therefore cannot re-check the
	/// hold when it claims the job. The hold is honoured at claim time by cancelling
	/// the job here instead, using the same
	/// <see cref="IJobControlRepository.CancelJobAsync"/> primitive
	/// <c>DELETE /downloads/{id}</c> already uses: a still-queued job moves straight to
	/// <c>cancelled</c> in one DB-authoritative statement, so no runner ever claims it;
	/// an already-claimed one gets <c>cancel_requested</c> and the dispatcher's
	/// heartbeat cooperatively cancels the handler mid-flight.
	///
	/// Best-effort by design, and honestly so: this cannot un-delete a file the handler
	/// already removed, nor the database rows the purge's first phase already committed.
	/// What it does guarantee is that no FURTHER deletion happens and that the purge is
	/// never completed while the hold stands -- the tombstone is withheld by
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
