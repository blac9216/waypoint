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
/// </summary>
public sealed class RunRetentionHoldService
{
	private readonly IJobControlRepository _jobs;
	private readonly IRunRetentionHoldRepository _holds;

	public RunRetentionHoldService(IJobControlRepository jobs, IRunRetentionHoldRepository holds)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(holds);

		_jobs = jobs;
		_holds = holds;
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
}
