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
///
/// Claim/lease primitives landed in #128. Lease-expiry recovery and the minimum
/// run-state reads used by the dispatcher land in #129; run creation, fan-out,
/// pause/abort and the auth-failure halt land with #130.
/// </summary>
public interface IJobQueueRepository
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
	/// True when <c>jobs.cancel_requested</c> is set for this job (see <see cref="CancelJobAsync"/>
	/// on a running/attesting/converting job -- issue #234's per-job cooperative-cancel
	/// signal, the running-job counterpart to <see cref="GetRunQueueStateAsync"/>'s
	/// run-scoped <c>aborted</c> check). False (including for an unknown or terminal job
	/// id) is the safe default: the dispatcher's heartbeat loop calls this every tick
	/// alongside the lease renewal, so a false-negative merely delays cancellation to the
	/// next tick while a false-positive would wrongly cancel healthy work.
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

	/// <summary>The run state and queue flags, or null when it does not exist.</summary>
	Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Full run summary for GET /runs/{id}, including per-job counts derived from the
	/// jobs table. Returns <c>null</c> when the run does not exist.
	/// </summary>
	Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Paginated run summaries for GET /runs, newest-first (<c>ORDER BY created_at
	/// DESC</c>) per docs/api-contract.md Conventions' <c>?limit/offset</c> pagination.
	/// Reuses the same per-job <c>FILTER</c> aggregation as <see cref="GetRunAsync"/>,
	/// grouped per run, plus the full collection's total row count for the caller's
	/// <c>X-Total-Count</c> response header.
	/// </summary>
	Task<RunListResult> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken);

	/// <summary>
	/// All jobs belonging to a run, ordered by priority then created_at.
	/// </summary>
	Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// A single job by id, or <c>null</c> when it does not exist -- the projection
	/// <c>GET /jobs/{id}/artifacts/{kind}</c> (issue #299) needs to confirm a job exists
	/// (and read its <see cref="JobSummary.TargetId"/>/<see cref="JobSummary.State"/>)
	/// without requiring the caller to already know its run.
	/// </summary>
	Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

	/// <summary>Creates a pending run and returns its identifier.</summary>
	Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken);

	/// <summary>
	/// Atomically creates all jobs for a run and marks the run running. A spec whose
	/// credential is queue-halted (see <see cref="CheckConsecutiveAuthFailuresAsync"/>)
	/// is created <c>blocked</c> rather than <c>queued</c> -- enforced by migration
	/// 0005's trigger, not by this method -- and the run itself is blocked with the
	/// halt reason.
	/// </summary>
	Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken);

	/// <summary>Pauses dispatch for a pending or running run; in-flight work continues.</summary>
	Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>Resumes dispatch for an existing non-terminal run.</summary>
	Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>Aborts a run and reports jobs requiring cooperative cancellation.</summary>
	Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken);

	/// <summary>
	/// Cancels a single job within a run without touching its siblings -- the per-job
	/// counterpart to <see cref="AbortRunAsync"/>, added for <c>DELETE /downloads/{id}</c>
	/// (issue #10) so cancelling one artifact's download in a multi-job download run
	/// never aborts the whole run. A <c>queued</c> or <c>blocked</c> job is moved to
	/// <c>cancelled</c> in a single statement (DB-authoritative, safe across workers).
	/// A job already <c>running</c>, <c>attesting</c> or <c>converting</c> sets
	/// <c>cancel_requested</c> instead (issue #234) and the method returns
	/// <see cref="JobCancelOutcome.CancelRequested"/>: the dispatcher's heartbeat loop
	/// observes that flag on its next tick, alongside the existing run-scoped
	/// <c>runs.state='aborted'</c> check, and cooperatively cancels the in-flight handler
	/// -- see <c>JobDispatcherHostedService.RunHeartbeatLoopAsync</c>. A terminal job is
	/// left untouched and returns <see cref="JobCancelOutcome.NotCancellable"/>. Returns
	/// <see cref="JobCancelOutcome.NotFound"/> when no such job exists.
	/// </summary>
	Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #297: the operator-facing counterpart to ADR-0012 §5's engine-level
	/// resume primitive. Moves a <c>failed</c> job back to <c>queued</c> WITHOUT
	/// touching <c>jobs.stage</c> -- the next claim's <c>RETURNING stage</c> hands the
	/// marker straight back (<see cref="ClaimedJob.Stage"/>), so the handler resumes at
	/// the last-reached stage rather than restarting the pipeline, exactly like
	/// <see cref="RequeueAtStageAsync"/>'s engine-driven requeue and the lease-recovery
	/// sweep's mid-pipeline recovery. Scoped to <c>failed</c> only (see
	/// <see cref="JobRetryOutcome"/>): NOT <c>auth-failed</c> (issue #146/#295's
	/// credential-swap-resume path exists precisely because retrying without swapping
	/// the bad credential would just re-fail) and NOT <c>cancelled</c> (a deliberate
	/// operator action -- silently re-queueing it is wrong; the operator starts a new
	/// run instead). This is a manual override, not the auto-retry/lease-recovery path:
	/// it does not increment <c>attempt_count</c> and is never blocked by the
	/// <c>max_attempts</c> cap that governs automatic retries -- an explicit human
	/// action is not subject to the automatic-retry budget. Resets lease/claim columns
	/// the same way <see cref="RequeueAtStageAsync"/> does, and records an
	/// <c>audit_log</c> row (<c>event_type = 'job.retried'</c>) carrying the actor,
	/// job id and run id -- "no audit, no retry" mirroring every other
	/// run-control action in this repository.
	/// </summary>
	Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Blocks queued work after the credential's most recent resolved outcomes are
	/// consecutive authentication failures, and durably halts the credential
	/// (<c>credentials.queue_halted</c>) so later fan-outs, requeues and releases for
	/// it are created/coerced <c>blocked</c> until an explicit unblock flow clears it.
	/// </summary>
	Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken);

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
	/// Clears <c>credentials.queue_halted</c> and transitions that credential's
	/// <c>blocked</c> jobs back to <c>queued</c> (and their runs unblocked) in one
	/// transaction, serialized against the halt's <c>FOR UPDATE</c> idiom. This is the
	/// inverse of <see cref="CheckConsecutiveAuthFailuresAsync"/>.
	/// </summary>
	Task<CredentialUnblockResult> UnblockCredentialAsync(Guid credentialId, string? reason, CancellationToken cancellationToken);

	/// <summary>
	/// <c>POST /runs/{id}/resume-blocked</c> (docs/api-contract.md, ADR-0008): unlike
	/// <see cref="UnblockCredentialAsync"/>'s same-credential retry, this reassigns the
	/// run's halted job set onto a <b>different, caller-supplied replacement
	/// credential</b> -- a true swap, not a retry. Scoped to exactly one run: only that
	/// run's <c>blocked</c> jobs move, never every job system-wide that happens to
	/// reference the same halted credential (a sibling run blocked by the same
	/// credential is untouched and still needs its own resume-blocked call). Under the
	/// same <c>FOR UPDATE</c> halt-lock discipline as <see cref="UnblockCredentialAsync"/>,
	/// this: (1) locks and reads the run's distinct halted credential(s) from its
	/// <c>blocked</c> jobs; (2) locks and validates the replacement credential; (3)
	/// updates <c>jobs.credential_id</c> old-&gt;new for that job set; (4) writes an
	/// <c>audit_log</c> row carrying both credential identities; (5) clears the block
	/// and moves those jobs back to <c>queued</c>, unblocking the run. See
	/// <see cref="CredentialSwapOutcome"/> for the failure modes the caller maps to
	/// HTTP status codes.
	/// </summary>
	Task<CredentialSwapResult> SwapAndResumeBlockedCredentialAsync(
		Guid runId, Guid replacementCredentialId, string actor, string? reason, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #311: records a <c>scan</c> job's STIG Manager upload outcome
	/// (<see cref="JobUploadStatuses"/>) in <c>jobs.upload_status</c>/<c>upload_detail</c>
	/// -- deliberately independent of <see cref="AdvanceStateAsync"/>/<see cref="RequeueAtStageAsync"/>:
	/// this column is written after the job has already reached (or independently of)
	/// its state-machine terminal, and a retry rewrites it without touching
	/// <c>state</c>/<c>stage</c> at all. Always succeeds for an existing job id
	/// regardless of its current <c>state</c> -- there is no lease/ownership check here,
	/// matching a plain projection column rather than a queue-claim field.
	/// </summary>
	Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken);
}

/// <summary>
/// Run-level fields needed after a global job claim. <see cref="InitiatedBy"/> backs
/// the pause/resume/abort ownership check (issue #209): null means a system/scheduled
/// run with no recorded initiator. Trailing default keeps existing positional call
/// sites compiling.
/// </summary>
public sealed record RunQueueState(string State, bool Paused, bool Blocked, string? BlockedReason, string? InitiatedBy = null);

/// <summary>The database effects of aborting a run.</summary>
public sealed record AbortRunResult(IReadOnlyList<Guid> CancelledJobIds, IReadOnlyList<Guid> InFlightJobIds);

/// <summary>
/// The outcome of a single-job cancel (see <see cref="IJobQueueRepository.CancelJobAsync"/>).
/// <see cref="Cancelled"/>: a queued/blocked job was moved to <c>cancelled</c> immediately.
/// <see cref="CancelRequested"/>: a running/attesting/converting job had
/// <c>cancel_requested</c> set; it stops cooperatively at the dispatcher's next heartbeat
/// tick rather than immediately (issue #234). <see cref="NotCancellable"/>: the job exists
/// but is already terminal, so it was not touched. <see cref="NotFound"/>: no such job row.
/// </summary>
public enum JobCancelOutcome
{
	NotFound,
	Cancelled,
	NotCancellable,
	CancelRequested,
}

/// <summary>
/// The outcome of a single-job manual retry (see
/// <see cref="IJobQueueRepository.RetryJobAsync"/>). <see cref="Retried"/>: a
/// <c>failed</c> job was moved back to <c>queued</c> with <c>stage</c> preserved.
/// <see cref="NotFailed"/>: the job exists but is not <c>failed</c> (includes
/// <c>auth-failed</c> and <c>cancelled</c>, both deliberately excluded -- see the
/// interface doc comment) -- the caller maps this to 409. <see cref="NotFound"/>: no
/// such job row.
/// </summary>
public enum JobRetryOutcome
{
	NotFound,
	Retried,
	NotFailed,
}

/// <summary>A page of run summaries plus the full collection's total row count (for <c>X-Total-Count</c>).</summary>
public sealed record RunListResult(IReadOnlyList<RunSummary> Items, int TotalCount);

/// <summary>
/// The database effects of a consecutive-auth-failure check. <see cref="HaltTripped"/>
/// is true whenever the window condition held -- including with zero queued rows to
/// block (#147: the halt is SSE-visible even when it only flips the durable
/// credential state), and on a re-check against an already-halted credential.
/// </summary>
public sealed record AuthFailureHaltResult(bool HaltTripped, IReadOnlyList<Guid> BlockedRunIds, IReadOnlyList<Guid> BlockedJobIds);

/// <summary>
/// The database effects of unblocking a credential queue halt. <see cref="WasHalted"/>
/// is false when the credential was not halted (idempotent no-op).
/// </summary>
public sealed record CredentialUnblockResult(bool WasHalted, IReadOnlyList<Guid> UnblockedRunIds, IReadOnlyList<Guid> UnblockedJobIds);

/// <summary>
/// The failure modes <see cref="IJobQueueRepository.SwapAndResumeBlockedCredentialAsync"/>
/// can report -- the API layer maps each to a specific status code (see
/// <c>RunsController.ResumeBlockedRun</c>). <see cref="Swapped"/> is the only success
/// case.
/// </summary>
public enum CredentialSwapOutcome
{
	/// <summary>No such run.</summary>
	RunNotFound,

	/// <summary>The run exists but has no credential-halted <c>blocked</c> jobs to resume.</summary>
	RunNotHalted,

	/// <summary>
	/// The run's blocked jobs reference more than one distinct halted credential -- the
	/// single-replacement-credential contract has no unambiguous target. Not observed
	/// under the current fan-out shape (one credential per run), but the primitive does
	/// not assume it.
	/// </summary>
	AmbiguousHaltedCredential,

	/// <summary>No credential row with the supplied replacement id.</summary>
	ReplacementCredentialNotFound,

	/// <summary>The replacement credential is itself queue-halted -- swapping onto it would immediately re-block the run.</summary>
	ReplacementCredentialHalted,

	/// <summary>
	/// The replacement credential's <c>credential_type</c> does not match the halted
	/// credential's type -- e.g. swapping an <c>ssh</c> credential onto jobs that were
	/// authenticating with a <c>vcenter</c> credential.
	/// </summary>
	ReplacementCredentialTypeMismatch,

	/// <summary>The swap succeeded: <c>jobs.credential_id</c> moved old-&gt;new for the run's halted job set, the block cleared, and the jobs are queued again.</summary>
	Swapped,
}

/// <summary>
/// The outcome plus (on <see cref="CredentialSwapOutcome.Swapped"/>) the database
/// effects of a credential swap-and-resume. <see cref="OldCredentialId"/> and
/// <see cref="NewCredentialId"/> are populated on success only.
/// </summary>
public sealed record CredentialSwapResult(
	CredentialSwapOutcome Outcome,
	Guid? OldCredentialId,
	Guid? NewCredentialId,
	IReadOnlyList<Guid> ResumedJobIds);
