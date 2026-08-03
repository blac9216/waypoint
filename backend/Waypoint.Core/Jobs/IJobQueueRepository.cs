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

namespace Waypoint.Core.Jobs;

/// <summary>
/// Every write the job engine makes against <c>runs</c>/<c>jobs</c>. One implementation
/// (<c>Waypoint.Infrastructure.Jobs.JobQueueRepository</c>, plain Npgsql -- see
/// <c>NpgsqlSchemaMigrator</c>'s doc comment for why this codebase does not use an ORM
/// for job-queue access). Every method here is a single, short statement or a single
/// short transaction -- events are never emitted from inside these methods (see
/// <see cref="IJobEventPublisher"/>'s "emit last" contract); the caller emits after a
/// method here has already returned successfully, on its own connection.
/// </summary>
public interface IJobQueueRepository
{
	/// <summary>
	/// Atomically claims the highest-priority, oldest queued job and stamps its lease in
	/// the same statement -- the single-statement claim ADR-0008/#107 requires so
	/// <c>state='running'</c> and a non-NULL <c>lease_expires_at</c> can never diverge.
	/// The predicate/order/lock clause (<c>WHERE state = 'queued' ORDER BY priority,
	/// created_at FOR UPDATE SKIP LOCKED</c>) is byte-identical to the query
	/// <c>JobsQueueClaimTests</c> (issue #4) already proved never double-claims under 32
	/// concurrent processes x 40 attempts against 500 jobs -- do not change that clause
	/// here without re-running that proof (see <c>JobQueueClaimConcurrencyTests</c> for
	/// this method's own version of it). Returns <c>null</c> if the queue is empty.
	/// </summary>
	Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>Extends a claimed job's lease. Only succeeds while the job is still owned by <paramref name="workerId"/> and in an active state.</summary>
	Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>
	/// Moves a claimed job from <paramref name="expectedFromState"/> to
	/// <paramref name="toState"/>. Fails (returns <c>false</c>) if the row is no longer
	/// in <paramref name="expectedFromState"/> or no longer owned by
	/// <paramref name="workerId"/> -- e.g. a concurrent abort already moved it. Callers
	/// validate the transition against <see cref="JobStateMachine"/> before calling;
	/// this method does not re-derive the job's shape. <paramref name="clearLease"/>
	/// must be <c>true</c> for every transition out of an active state (<c>running</c>,
	/// <c>attesting</c>, <c>converting</c>) to a terminal one (the CHECK constraint
	/// requires it for <c>running</c>; this keeps the other active states consistent
	/// with the same discipline) and <c>false</c> for a same-tier transition (e.g.
	/// <c>running -&gt; attesting</c>) that keeps the lease alive.
	/// </summary>
	Task<bool> AdvanceStateAsync(
		Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken);

	/// <summary>
	/// Recovers up to <paramref name="batchSize"/> jobs whose lease has expired
	/// (<c>state = 'running' AND lease_expires_at &lt; now()</c>, the exact predicate
	/// <c>idx_jobs_lease_recovery</c> serves): requeues a job with attempts remaining,
	/// fails one that has exhausted <c>max_attempts</c>. Uses <c>FOR UPDATE SKIP LOCKED</c>
	/// so two concurrent sweeps never recover the same row twice.
	/// </summary>
	Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken);

	/// <summary>Creates a new run in <c>pending</c> state. Returns its id.</summary>
	Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken);

	/// <summary>
	/// The ADR-0008 "Run -&gt; Jobs fan-out": inserts one <c>jobs</c> row per
	/// <paramref name="specs"/> entry under <paramref name="runId"/> and marks the run
	/// <c>running</c>, all in one transaction. Returns the created job ids in the same
	/// order as <paramref name="specs"/>.
	/// </summary>
	Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken);

	/// <summary>Sets <c>runs.paused = true</c>. Dispatch of that run's jobs stops; in-flight jobs are left to finish (ADR-0008).</summary>
	Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>Clears <c>runs.paused</c>.</summary>
	Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>The run's current pause/blocked flags, or <c>null</c> if the run does not exist.</summary>
	Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// The run's <c>state</c> column (<c>pending</c>/<c>running</c>/<c>completed</c>/
	/// <c>completed_with_failures</c>/<c>aborted</c>), or <c>null</c> if it does not
	/// exist. Polled by an in-flight job's heartbeat loop so a job hosted by a
	/// *different* dispatcher instance than the one that called
	/// <see cref="AbortRunAsync"/> still notices the abort and self-cancels -- DB-side
	/// cancellation of queued jobs is instant and instance-independent, but cooperative
	/// cancellation of in-process work can only ever be as fast as the next heartbeat
	/// tick on the instance actually hosting it.
	/// </summary>
	Task<string?> GetRunStateAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Puts a job this process just claimed back into <c>queued</c>, clearing the
	/// lease/claim fields -- used when a claim turns out to belong to a paused or
	/// blocked run (see <c>JobDispatcherHostedService</c>'s claim-then-check step). This
	/// intentionally does not change <see cref="ClaimJobAsync"/>'s predicate; it is a
	/// second statement after a successful claim, not a filter added to the claim
	/// itself.
	/// </summary>
	Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken);

	/// <summary>
	/// Marks a run <c>aborted</c> and every one of its still-queued/blocked jobs
	/// <c>cancelled</c>. Returns the ids of jobs actually in flight
	/// (<c>running</c>/<c>attesting</c>/<c>converting</c>) at the moment of the abort so
	/// the caller can request cooperative cancellation on them -- SQL alone cannot stop
	/// work already executing in-process.
	/// </summary>
	Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Call after completing a job with <see cref="JobOutcomeKind.AuthFailed"/> and a
	/// non-null credential. Checks whether the credential's <paramref name="threshold"/>
	/// most recent jobs are now all <c>auth-failed</c> (via
	/// <c>idx_jobs_credential_recent</c>); if so, blocks every run that has a
	/// still-queued job against that credential and moves those jobs to
	/// <c>blocked</c>, atomically. Returns the empty result if the threshold was not
	/// tripped.
	/// </summary>
	Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken);
}
