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

namespace Waypoint.Core.ComplianceContent;

/// <summary>One profile directory a fanned-out <c>content-check</c> job must run <c>inspec check</c> against.</summary>
public sealed record ContentCheckProfileDirectory(string ProfileKey, string ProfileDirectory);

/// <summary>
/// One durably recorded per-profile <c>inspec check</c> outcome (migration 0073's
/// <c>content_pull_check_results</c>) -- the cross-process form of the in-memory
/// <see cref="Waypoint.Core.ComplianceContent.SemanticImport.VendorContentEntry"/> a
/// single content-pull invocation used to build up itself before issue #1016 moved the
/// check phase onto its own fanned-out jobs.
/// </summary>
public sealed record ContentCheckResultRecord(
	string ProfileKey,
	string? RawYaml,
	bool HasControlsDirectory,
	bool HasFilesDirectory,
	IReadOnlyList<string> ControlFileNames,
	bool InspecCheckRan,
	bool InspecCheckPassed,
	string? InspecCheckDetail);

/// <summary>
/// One <c>content_pull_checks</c> row -- one fanned-out chunk job for one content-pull.
/// <see cref="CheckJobId"/> is null for exactly one shape: the zero-chunk MARKER row a
/// pull that enumerated zero profiles records (see
/// <see cref="IContentPullCheckFanOutRepository.RecordEmptyFanOutAsync"/>) so the
/// reconcile sweep still discovers and completes it -- its
/// <see cref="ProfileDirectories"/> is always empty for that row (schema-enforced,
/// migration 0073's marker-shape CHECK).
/// </summary>
public sealed record ContentPullCheckFanOut(
	Guid Id,
	Guid RunId,
	Guid ContentPullJobId,
	Guid? CheckJobId,
	string SourceCommit,
	IReadOnlyList<ContentCheckProfileDirectory> ProfileDirectories,
	string Status);

/// <summary>Whether every chunk job fanned out for a content-pull has reached a terminal job state, and how many failed.</summary>
public sealed record ContentPullCheckReconcileReadiness(bool AllTerminal, int TotalCheckJobs, int FailedCheckJobs);

/// <summary>
/// Issue #1016 (epic #726), owner decision 2026-08-28: storage for the content-check
/// fan-out/reconcile records that let <c>ContentPullJobHandler</c> hand its bounded
/// `inspec check` phase to the ordinary job queue (ADR-0020 capacity pool) instead of
/// running every chunk itself in one long-lived invocation. A content-pull job writes
/// one <see cref="ContentPullCheckFanOut"/> row per chunk job it enqueues; each
/// <c>content-check</c> job writes its own <see cref="ContentCheckResultRecord"/> rows
/// as it finishes; the reconcile sweep (<c>ContentPullReconcileHostedService</c>,
/// hosted in the COMPLIANCE-RUNNER process because revision staging touches the
/// content working tree only that process mounts -- structurally mirrors
/// <c>RunPurgeFinalizeHostedService</c>'s "nobody in-band can resolve
/// who-else's-job-finished, so a periodic sweep does" shape) reads both back once
/// every chunk job for a pull is terminal and performs the SAME atomic
/// staging/promotion <c>RunSemanticImportAsync</c> always has.
/// </summary>
public interface IContentPullCheckFanOutRepository
{
	/// <summary>
	/// Records one fanned-out chunk job's linkage (called by <c>ContentPullJobHandler</c>
	/// in the compliance-runner, immediately after <c>IJobRunnerRepository</c>'s
	/// fan-out-onto-an-already-running-run insert for the same job id).
	/// </summary>
	Task RecordFanOutAsync(
		Guid runId, Guid contentPullJobId, Guid checkJobId, string sourceCommit,
		IReadOnlyList<ContentCheckProfileDirectory> profileDirectories, CancellationToken cancellationToken);

	/// <summary>
	/// Records the zero-chunk MARKER row for a successful pull that enumerated zero
	/// executable profiles (PR #1017 review round 1, finding 2): the reconcile sweep
	/// discovers work solely through <c>content_pull_checks</c>, so a pull with no
	/// check jobs still needs one row -- <c>check_job_id NULL</c>, empty chunk -- or
	/// its pull history/staging would never be recorded (issue #40's "every attempt
	/// recorded" invariant). Readiness for a marker-only pull is trivially "all
	/// terminal" (there are no check jobs to wait for).
	/// </summary>
	Task RecordEmptyFanOutAsync(Guid runId, Guid contentPullJobId, string sourceCommit, CancellationToken cancellationToken);

	/// <summary>The profile-directory chunk a specific claimed <c>content-check</c> job must run -- read by <c>ContentCheckJobHandler</c>.</summary>
	Task<ContentPullCheckFanOut?> GetFanOutForCheckJobAsync(Guid checkJobId, CancellationToken cancellationToken);

	/// <summary>Persists one profile's durable check outcome -- written by <c>ContentCheckJobHandler</c> as each profile in its chunk finishes.</summary>
	Task RecordCheckResultAsync(Guid checkJobId, ContentCheckResultRecord result, CancellationToken cancellationToken);

	/// <summary>Every pending (not yet reconciled) content-pull job id that has at least one fanned-out chunk row -- the reconcile sweep's worklist.</summary>
	Task<IReadOnlyList<Guid>> ListPendingReconcileContentPullJobIdsAsync(CancellationToken cancellationToken);

	/// <summary>Every fan-out row recorded for one content-pull job, in fan-out order.</summary>
	Task<IReadOnlyList<ContentPullCheckFanOut>> ListFanOutsForContentPullJobAsync(Guid contentPullJobId, CancellationToken cancellationToken);

	/// <summary>
	/// Whether every chunk job fanned out for <paramref name="contentPullJobId"/> has
	/// reached one of <see cref="Waypoint.Core.Jobs.JobTerminalStates"/> yet, and how
	/// many landed on a failure terminal -- the reconcile sweep only acts once this is
	/// true (mirrors <c>JobQueueRepository.TryCompleteRunAsync</c>'s own
	/// "remaining == 0" gate, applied to the check-job subset of a pull's run instead
	/// of the whole run).
	/// </summary>
	Task<ContentPullCheckReconcileReadiness> GetReconcileReadinessAsync(Guid contentPullJobId, CancellationToken cancellationToken);

	/// <summary>Every durably recorded check result for the given chunk jobs -- read back by the reconcile step to rebuild the full profile list.</summary>
	Task<IReadOnlyList<ContentCheckResultRecord>> ListCheckResultsAsync(IReadOnlyList<Guid> checkJobIds, CancellationToken cancellationToken);

	/// <summary>Marks every fan-out row for a content-pull job <c>reconciled</c> -- called once the reconcile step's own atomic staging/promotion has committed.</summary>
	Task MarkReconciledAsync(Guid contentPullJobId, CancellationToken cancellationToken);
}
