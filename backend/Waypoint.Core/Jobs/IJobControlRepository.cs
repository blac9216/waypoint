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
/// The enqueue/control/query surface ADR-0013 keeps on the ASP.NET control plane:
/// <c>Waypoint.Api</c> creates runs, fans out jobs, serves queries, and performs
/// operator-authorized control mutations (pause/resume/abort/cancel/retry/credential
/// swap) through this interface alone -- it never claims a job, advances execution
/// state, or touches a lease. See <see cref="IJobRunnerRepository"/> for the
/// claim/lease/state/recovery surface ADR-0014 assigns to the executing runner.
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
/// Run creation, fan-out, pause/abort and the auth-failure halt land with #130.
/// </summary>
public interface IJobControlRepository
{
	/// <summary>
	/// The run state and queue flags, or null when it does not exist. See
	/// <see cref="IJobRunnerRepository.GetRunQueueStateAsync"/> for the same primitive
	/// on the runner's claim/heartbeat path -- one implementation backs both.
	/// </summary>
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
	/// Filtered, keyset-cursor-paged run summaries for <c>GET /runs/history</c> (issue
	/// #708/#689) -- the terminal-run browsing surface for the global Jobs History
	/// mode. Newest-first (<c>ORDER BY created_at DESC, id DESC</c>), same tie-break as
	/// <see cref="ListRunsAsync"/> and the same ordering <c>RunHistoryCursor</c>
	/// (Waypoint.Api.Contracts) wraps. Unlike <see cref="ListRunsAsync"/>'s offset paging, this never re-derives a
	/// full-table <c>COUNT(*)</c> -- see <see cref="RunHistoryPage.HasMore"/>.
	/// </summary>
	Task<RunHistoryPage> ListRunHistoryAsync(RunHistoryQuery query, CancellationToken cancellationToken);

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

	/// <summary>
	/// Creates a pending run and returns its identifier. <paramref name="scheduleId"/> is
	/// null for every operator-initiated run (the overwhelming majority of call sites);
	/// only <see cref="Waypoint.Infrastructure.Scheduling.ScheduleDispatchService"/>
	/// passes a non-null value, stamping <c>runs.schedule_id</c> (FK'd to
	/// <c>schedules</c>, migration 0032, issue #515) so <c>GET /runs</c>/<c>GET
	/// /runs/{id}</c> can answer "which schedule produced this run" directly, without
	/// joining through <c>schedules.last_run_id</c> (which only ever points at the most
	/// recent run a schedule produced).
	/// </summary>
	Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null);

	/// <summary>
	/// Atomically creates all jobs for a run and marks the run running. A spec whose
	/// credential is queue-halted (see
	/// <see cref="IJobRunnerRepository.CheckConsecutiveAuthFailuresAsync"/>) is created
	/// <c>blocked</c> rather than <c>queued</c> -- enforced by migration 0005's trigger,
	/// not by this method -- and the run itself is blocked with the halt reason.
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
	/// observes that flag on its next tick via
	/// <see cref="IJobRunnerRepository.IsCancelRequestedAsync"/>, alongside the existing
	/// run-scoped <c>runs.state='aborted'</c> check, and cooperatively cancels the
	/// in-flight handler -- see <c>JobDispatcherHostedService.RunHeartbeatLoopAsync</c>.
	/// A terminal job is left untouched and returns
	/// <see cref="JobCancelOutcome.NotCancellable"/>. Returns
	/// <see cref="JobCancelOutcome.NotFound"/> when no such job exists.
	/// </summary>
	Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #297: the operator-facing counterpart to ADR-0012 §5's engine-level
	/// resume primitive. Moves a <c>failed</c> job back to <c>queued</c> WITHOUT
	/// touching <c>jobs.stage</c> -- the next claim's <c>RETURNING stage</c> hands the
	/// marker straight back (<see cref="ClaimedJob.Stage"/>), so the handler resumes at
	/// the last-reached stage rather than restarting the pipeline, exactly like
	/// <see cref="IJobRunnerRepository.RequeueAtStageAsync"/>'s engine-driven requeue and
	/// the lease-recovery sweep's mid-pipeline recovery. Scoped to <c>failed</c> only
	/// (see <see cref="JobRetryOutcome"/>): NOT <c>auth-failed</c> (issue #146/#295's
	/// credential-swap-resume path exists precisely because retrying without swapping
	/// the bad credential would just re-fail) and NOT <c>cancelled</c> (a deliberate
	/// operator action -- silently re-queueing it is wrong; the operator starts a new
	/// run instead). This is a manual override, not the auto-retry/lease-recovery path:
	/// it does not increment <c>attempt_count</c> and is never blocked by the
	/// <c>max_attempts</c> cap that governs automatic retries -- an explicit human
	/// action is not subject to the automatic-retry budget. Resets lease/claim columns
	/// the same way <see cref="IJobRunnerRepository.RequeueAtStageAsync"/> does, and
	/// records an <c>audit_log</c> row (<c>event_type = 'job.retried'</c>) carrying the
	/// actor, job id and run id -- "no audit, no retry" mirroring every other
	/// run-control action in this repository.
	/// </summary>
	Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #757: audited bulk cancel over an explicit, already-bounded job id set
	/// (the caller -- <c>RunsController.BulkCancelJobs</c> -- resolves a filter to ids
	/// via <see cref="IComponentJobRepository.ResolveJobIdsAsync"/> and enforces the
	/// bound BEFORE this method ever runs; this method itself does not re-check a
	/// count limit, matching "bounded" being a request-shaping concern, not a
	/// storage-layer one). Every id is scoped to <paramref name="runId"/> -- an id from
	/// a different run reports <see cref="JobCancelOutcome.NotFound"/> rather than
	/// being cancelled under the wrong run's authority, the same "job must belong to
	/// this run" rule <c>RunsController.RetryJob</c> already enforces for the singular
	/// action. Each job is cancelled independently through the exact same state-gated
	/// transaction <see cref="CancelJobAsync"/> uses (one conflict does not block or
	/// roll back the others), so the result is an honest per-item outcome list, never
	/// a fake all-or-nothing transaction. Writes exactly one summary
	/// <c>audit_log</c> row (<c>event_type = 'job.bulk_cancelled'</c>) carrying the
	/// actor, run id, resolved id count, and the per-outcome tally -- "no audit, no
	/// bulk action" mirrors every other run-control primitive in this repository.
	/// </summary>
	Task<BulkJobActionResult<JobCancelOutcome>> BulkCancelJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #757: audited bulk retry, the bulk counterpart to <see cref="RetryJobAsync"/>
	/// with the identical per-item semantics (scoped to <c>failed</c> jobs only,
	/// <c>jobs.stage</c> preserved, not subject to the automatic-retry budget) and the
	/// same run-scoping/bounding contract as <see cref="BulkCancelJobsAsync"/>. Writes
	/// one summary <c>audit_log</c> row (<c>event_type = 'job.bulk_retried'</c>)
	/// instead of one row per job -- <see cref="RetryJobAsync"/>'s own per-job
	/// <c>job.retried</c> audit row is NOT also written for jobs retried this way, so a
	/// bulk retry of N jobs produces exactly one audit entry, not N+1.
	/// </summary>
	Task<BulkJobActionResult<JobRetryOutcome>> BulkRetryJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Clears <c>credentials.queue_halted</c> and transitions that credential's
	/// <c>blocked</c> jobs back to <c>queued</c> (and their runs unblocked) in one
	/// transaction, serialized against the halt's <c>FOR UPDATE</c> idiom. This is the
	/// operator-driven inverse of
	/// <see cref="IJobRunnerRepository.CheckConsecutiveAuthFailuresAsync"/>.
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
/// Issue #406: the outcome of the run-completion check a terminal job write triggers.
/// <see cref="RunId"/> and <see cref="State"/> (<c>"completed"</c> or
/// <c>"completed_with_failures"</c>) identify what changed; <see cref="FailedJobCount"/>
/// is the run's failure-terminal (<c>failed</c>/<c>auth-failed</c>/<c>cancelled</c>) job
/// count at the moment it completed, carried into the emitted <c>run.progress</c> event.
/// </summary>
public sealed record RunCompletionResult(Guid RunId, string State, int FailedJobCount);

/// <summary>
/// The outcome of a single-job cancel (see <see cref="IJobControlRepository.CancelJobAsync"/>).
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
/// <see cref="IJobControlRepository.RetryJobAsync"/>). <see cref="Retried"/>: a
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

/// <summary>
/// One resolved job's outcome within an audited bulk action (issue #757) --
/// <see cref="Outcome"/> is the same per-item outcome enum the singular action
/// returns (<see cref="JobCancelOutcome"/>/<see cref="JobRetryOutcome"/>), so the
/// bulk and singular endpoints report identical vocabulary for identical results.
/// </summary>
public sealed record BulkJobItemResult<TOutcome>(Guid JobId, TOutcome Outcome) where TOutcome : struct, Enum;

/// <summary>
/// The full result of an audited bulk job action (issue #757): one entry per
/// resolved job id, in the same order the caller supplied them. Never collapses to a
/// single boolean -- a partial conflict (some jobs already terminal, some belonging
/// to a different run) is reported item-by-item, never as a fake all-or-nothing
/// success or failure.
/// </summary>
public sealed record BulkJobActionResult<TOutcome>(IReadOnlyList<BulkJobItemResult<TOutcome>> Items) where TOutcome : struct, Enum;

/// <summary>A page of run summaries plus the full collection's total row count (for <c>X-Total-Count</c>).</summary>
public sealed record RunListResult(IReadOnlyList<RunSummary> Items, int TotalCount);

/// <summary>
/// Filters + keyset cursor for <see cref="IJobControlRepository.ListRunHistoryAsync"/>
/// (issue #708/#689). All filters are optional allow-lists (null/empty means "no
/// filter"); <see cref="AfterCreatedAt"/>/<see cref="AfterId"/> is the decoded
/// <c>RunHistoryCursor</c> (Waypoint.Api.Contracts) keyset (both null for the first
/// page). <see cref="Since"/>/<see cref="Until"/> bound <c>created_at</c> inclusively.
/// </summary>
public sealed record RunHistoryQuery(
	IReadOnlyList<string>? States,
	IReadOnlyList<string>? RunTypes,
	DateTimeOffset? Since,
	DateTimeOffset? Until,
	DateTimeOffset? AfterCreatedAt,
	Guid? AfterId,
	int Limit);

/// <summary>
/// A page of <see cref="RunHistoryQuery"/> results. <see cref="HasMore"/> is true when
/// more matching rows exist past this page -- the repository fetches <c>Limit + 1</c>
/// rows to detect this without a second COUNT query (mirrors
/// <see cref="Waypoint.Core.Jobs.IJobEventHistoryReader"/>'s "never silently truncate"
/// contract).
/// </summary>
public sealed record RunHistoryPage(IReadOnlyList<RunSummary> Items, bool HasMore);

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
/// The failure modes <see cref="IJobControlRepository.SwapAndResumeBlockedCredentialAsync"/>
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
