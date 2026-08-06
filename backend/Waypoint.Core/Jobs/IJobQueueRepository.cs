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
	/// All jobs belonging to a run, ordered by priority then created_at.
	/// </summary>
	Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken);

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
}

/// <summary>Run-level fields needed after a global job claim.</summary>
public sealed record RunQueueState(string State, bool Paused, bool Blocked, string? BlockedReason);

/// <summary>The database effects of aborting a run.</summary>
public sealed record AbortRunResult(IReadOnlyList<Guid> CancelledJobIds, IReadOnlyList<Guid> InFlightJobIds);

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
