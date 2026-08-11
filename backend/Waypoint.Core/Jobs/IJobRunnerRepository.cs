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
/// The claim/lease/state/recovery surface ADR-0014 assigns to the executing runner: the
/// dedicated <c>compliance-runner</c>/<c>download-runner</c> processes (and, until they
/// are split out, the in-process dispatcher/lease-recovery hosted services) are the only
/// callers of every method here. ADR-0013 keeps the ASP.NET control plane out of job
/// execution entirely, so nothing in this interface is called from a controller -- see
/// <see cref="IJobControlRepository"/> for the API's enqueue/control/query surface.
///
/// Issue #415 split this out of the former combined <c>IJobQueueRepository</c> along
/// that process boundary. One implementation
/// (<c>Waypoint.Infrastructure.Jobs.JobQueueRepository</c>, plain Npgsql -- see
/// <c>NpgsqlSchemaMigrator</c>'s doc comment for why this codebase does not use an ORM
/// for job-queue access) satisfies both focused interfaces today; the SQL, transaction
/// boundaries, locking order, and event-ordering discipline documented on each method
/// are unchanged from before the split. Events are never emitted from inside these
/// methods (see <see cref="IJobEventPublisher"/>'s "emit last" contract); the caller
/// emits after a method here has already returned successfully, on its own connection.
///
/// Claim/lease primitives landed in #128. Lease-expiry recovery and the minimum
/// run-state reads used by the dispatcher land in #129; run creation, fan-out,
/// pause/abort and the auth-failure halt land with #130.
/// </summary>
public interface IJobRunnerRepository
{
	/// <summary>
	/// Atomically claims the highest-priority, oldest queued job and stamps its lease in
	/// the same statement -- the single-statement claim ADR-0008/#107 requires so
	/// <c>state='running'</c> and a non-NULL <c>lease_expires_at</c> can never diverge.
	/// The predicate and the ordering/locking clause (<c>WHERE state = 'queued' ...
	/// ORDER BY priority, created_at FOR UPDATE SKIP LOCKED LIMIT 1</c>) are the same
	/// ones <c>JobsQueueClaimTests</c> (issue #4) proved never double-claim under real
	/// concurrency; that test's query additionally scopes itself to one run so it cannot
	/// race another test class's rows, so the two are not identical and this doc does not
	/// claim they are (<c>JobQueueClaimSqlParityTests</c> pins exactly the part that is
	/// shared). Do not change the clause without re-running the proof --
	/// <c>JobQueueRepositoryClaimTests</c> carries it forward against this method.
	/// Returns <c>null</c> if the queue is empty.
	/// </summary>
	Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>Extends a claimed job's lease. Only succeeds while the job is still owned by <paramref name="workerId"/> and in an active state.</summary>
	Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>
	/// True when <c>jobs.cancel_requested</c> is set for this job (see
	/// <see cref="IJobControlRepository.CancelJobAsync"/> on a running/attesting/converting
	/// job -- issue #234's per-job cooperative-cancel signal, the running-job counterpart
	/// to <see cref="GetRunQueueStateAsync"/>'s run-scoped <c>aborted</c> check). False
	/// (including for an unknown or terminal job id) is the safe default: the
	/// dispatcher's heartbeat loop calls this every tick alongside the lease renewal, so
	/// a false-negative merely delays cancellation to the next tick while a
	/// false-positive would wrongly cancel healthy work.
	/// </summary>
	Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken);

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
	/// Issue #293's stage-per-execution requeue: moves a claimed job from
	/// <paramref name="expectedFromState"/> back to <c>queued</c>, clearing the lease
	/// (same discipline as <see cref="AdvanceStateAsync"/>'s <c>clearLease: true</c>
	/// path) and durably recording <paramref name="stage"/> in <c>jobs.stage</c> so the
	/// next claim of this job hands that marker back to the handler via
	/// <see cref="ClaimedJob.Stage"/>. This is how a <see cref="JobOutcomeKind.StageComplete"/>
	/// outcome rests a multi-stage job between claim/execute cycles instead of forcing
	/// a shape-terminal state: <c>queued</c> stays the only claimable/unleased state
	/// (the ruling on #274), with the stage marker carrying the pipeline position that
	/// <c>state</c> alone no longer does once the row leaves <c>attesting</c>/<c>converting</c>.
	/// Fails (returns <c>false</c>) under the same race <see cref="AdvanceStateAsync"/>
	/// guards against -- the row is no longer <paramref name="expectedFromState"/> or no
	/// longer owned by <paramref name="workerId"/>.
	/// </summary>
	Task<bool> RequeueAtStageAsync(
		Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken);

	/// <summary>Recovers expired running leases, cancelling work under aborted runs.</summary>
	Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken);

	/// <summary>
	/// The run state and queue flags, or null when it does not exist. ADR-0014's runner
	/// reads this after every claim (and on each heartbeat tick) to observe paused/
	/// blocked/aborted state without owning any run-control mutation itself; see
	/// <see cref="IJobControlRepository.GetRunQueueStateAsync"/> for the same primitive
	/// on the API's query surface -- one implementation backs both.
	/// </summary>
	Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Puts a job this process just claimed back into <c>queued</c>, clearing the
	/// lease/claim fields -- used when a claim turns out to belong to a run that must
	/// not be dispatched (see the dispatcher's claim-then-check step, #129). This
	/// intentionally does not change <see cref="ClaimJobAsync"/>'s predicate; it is a
	/// second statement after a successful claim, not a filter added to the claim
	/// itself.
	/// </summary>
	Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken);

	/// <summary>
	/// Blocks queued work after the credential's most recent resolved outcomes are
	/// consecutive authentication failures, and durably halts the credential
	/// (<c>credentials.queue_halted</c>) so later fan-outs, requeues and releases for
	/// it are created/coerced <c>blocked</c> until an explicit unblock flow clears it.
	/// Called by the runner immediately after a job's terminal write lands it on
	/// <c>auth-failed</c> (<c>JobDispatcherHostedService.HandleAuthFailureAsync</c>) --
	/// the halt trip is a side effect of execution, not an operator action, which is
	/// why it lives on this interface rather than <see cref="IJobControlRepository"/>.
	/// </summary>
	Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #311: records a <c>scan</c> job's STIG Manager upload outcome
	/// (<see cref="JobUploadStatuses"/>) in <c>jobs.upload_status</c>/<c>upload_detail</c>
	/// -- deliberately independent of <see cref="AdvanceStateAsync"/>/<see cref="RequeueAtStageAsync"/>:
	/// this column is written after the job has already reached (or independently of)
	/// its state-machine terminal, and a retry rewrites it without touching
	/// <c>state</c>/<c>stage</c> at all. Always succeeds for an existing job id
	/// regardless of its current <c>state</c> -- there is no lease/ownership check here,
	/// matching a plain projection column rather than a queue-claim field. Written by
	/// the compliance runner's upload coordinator during/after scan execution.
	/// </summary>
	Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken);
}
