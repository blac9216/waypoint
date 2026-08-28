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

using System.Text.Json;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.ConfigDocs;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #594 (epic #577): Admin-only, crash-safe purge orchestration for a terminal
/// compliance run and every compliance-owned projection/artifact it produced.
/// Deliberately a single callable entry point (<see cref="PurgeRunAsync"/>) so #592's
/// planned generic Job History cleanup can call directly into this service for a
/// compliance run rather than reimplementing any part of this deletion -- the epic's
/// "keep the service-layer contract clean enough for that" requirement.
///
/// API-side vs. runner-job split (the design question the issue calls out
/// explicitly): the API process mounts the scan-artifact volume read-only
/// (deploy/compose.yaml, "the backend never writes an artifact itself") -- it
/// structurally cannot delete a file on that volume. Everything this service does
/// synchronously is therefore database-only, using the SAME owner-privileged
/// connection every other controller-invoked service already has: deleting
/// <see cref="AttestationSnapshotRepository.DeleteForRunAsync"/> rows, a defensive
/// leftover <c>run_secrets</c> sweep (normally already gone --
/// <c>JobQueueRepository.TryCompleteRunAsync</c> deletes it on the run's own terminal
/// transition), and nulling any <c>schedules.last_run_id</c> reference (issue's Risk
/// note). Deleting the on-disk HDF/CKL files is enqueued as a <c>purge</c> job wrapped
/// in its own run (mirroring every other standalone job type's shape, e.g.
/// <c>tool-install</c>) -- <c>Waypoint.Infrastructure.Scans.PurgeJobHandler</c> in the
/// Waypoint.Infrastructure.Execution assembly executes it under
/// <c>JobCapabilities.Compliance</c>, against compliance-runner's own read-write mount,
/// and reports its outcome back into <c>run_purges</c> under its own least-privilege
/// grant (migration 0042) -- this service never claims/executes anything itself,
/// matching ADR-0013's control-plane-never-executes boundary.
///
/// Retry/idempotency: <see cref="PurgeRunAsync"/> is safe to call again at any point.
/// A run with an existing tombstone returns <see cref="RunPurgeOutcome.AlreadyPurged"/>
/// without touching anything. A run with an in-flight <c>run_purges</c> row resumes --
/// the database phase is only ever executed once (<c>db_phase_done</c> guards it) and
/// the artifact job is only re-enqueued when the prior one is not already
/// pending/running, so a retry after a partial filesystem failure re-attempts exactly
/// the artifact deletion, never re-deletes already-gone database rows or double-writes
/// the tombstone (<see cref="IRunPurgeRepository.CompleteAsync"/>'s own
/// <c>ON CONFLICT DO NOTHING</c>).
/// </summary>
public sealed class RunPurgeService
{
	/// <summary>The three terminal <c>runs.state</c> values a purge is allowed against (docs/api-contract.md's run state machine).</summary>
	private static readonly HashSet<string> TerminalRunStates = new(StringComparer.Ordinal)
	{
		"completed", "completed_with_failures", "aborted",
	};

	/// <summary>Same low-urgency tier as an ordinary artifact download / tool-install (issue #39's <c>ToolInstallPriority</c>) -- a purge must never starve a scan.</summary>
	private const short PurgePriority = 6;

	private readonly IJobControlRepository _jobs;
	private readonly IRunPurgeRepository _purges;
	private readonly AttestationSnapshotRepository _attestationSnapshots;
	private readonly string _connectionString;

	public RunPurgeService(
		IJobControlRepository jobs,
		IRunPurgeRepository purges,
		AttestationSnapshotRepository attestationSnapshots,
		string connectionString)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(purges);
		ArgumentNullException.ThrowIfNull(attestationSnapshots);
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		_jobs = jobs;
		_purges = purges;
		_attestationSnapshots = attestationSnapshots;
		_connectionString = connectionString;
	}

	/// <summary>
	/// Returns the current purge status for a run: an in-flight <c>run_purges</c> row
	/// if one exists, else the completed tombstone if one exists, else <c>null</c> (no
	/// purge was ever requested). Backs <c>GET /runs/{id}/purge</c>.
	/// </summary>
	public async Task<RunPurgeResult?> GetStatusAsync(Guid runId, CancellationToken cancellationToken)
	{
		RunPurgeStatus? status = await _purges.GetStatusAsync(runId, cancellationToken).ConfigureAwait(false);
		if (status is not null)
		{
			return new RunPurgeResult(ClassifyInFlight(status), status);
		}

		RunPurgeTombstone? tombstone = await _purges.GetTombstoneAsync(runId, cancellationToken).ConfigureAwait(false);
		return tombstone is null ? null : new RunPurgeResult(RunPurgeOutcome.Completed, ToStatus(tombstone));
	}

	/// <summary>
	/// Requests (or resumes) a purge for <paramref name="runId"/>. See this class's doc
	/// comment for the full retry/idempotency contract.
	/// </summary>
	public async Task<RunPurgeResult> PurgeRunAsync(Guid runId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		RunPurgeTombstone? existingTombstone = await _purges.GetTombstoneAsync(runId, cancellationToken).ConfigureAwait(false);
		if (existingTombstone is not null)
		{
			return new RunPurgeResult(RunPurgeOutcome.AlreadyPurged, ToStatus(existingTombstone));
		}

		RunPurgeStatus? inFlight = await _purges.GetStatusAsync(runId, cancellationToken).ConfigureAwait(false);
		if (inFlight is not null)
		{
			return await ResumeAsync(inFlight, cancellationToken).ConfigureAwait(false);
		}

		RunSummary? run = await _jobs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			return new RunPurgeResult(RunPurgeOutcome.RunNotFound);
		}

		if (!TerminalRunStates.Contains(run.State))
		{
			return new RunPurgeResult(RunPurgeOutcome.RunNotTerminal);
		}

		RunPurgeStatus created = await _purges.CreateAsync(runId, actor, run.State, cancellationToken).ConfigureAwait(false);
		return await ResumeAsync(created, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #1013: drives finalization the instant the async artifact-purge job
	/// reports its own outcome, so a run with on-disk artifacts finalizes in the SAME
	/// operator action that started the purge rather than requiring a manual re-POST.
	/// <see cref="Waypoint.Infrastructure.Scans.PurgeJobHandler"/> (compliance-runner)
	/// calls this immediately after <see cref="IRunPurgeRepository.ReportArtifactOutcomeAsync"/>
	/// commits -- same process, same call stack, no separate reconciler/sweep needed.
	/// Re-reads status fresh from <c>run_purges</c> rather than trusting any
	/// caller-held copy, so it observes the <c>artifacts_phase = 'done'</c> the report
	/// just committed. A failed artifact report leaves <c>artifacts_phase = 'failed'</c>,
	/// which <see cref="ResumeAsync"/> re-enqueues rather than finalizes -- this class's
	/// retry contract is unchanged: only a genuinely successful artifact pass finalizes
	/// here, a failure still needs (and gets) a retryable re-POST. A missing
	/// <c>run_purges</c> row (defensive -- should not happen, since
	/// <see cref="IRunPurgeRepository.ReportArtifactOutcomeAsync"/> was just called for
	/// this same run id) is a silent no-op: the job's own outcome is already durably
	/// recorded either way.
	/// </summary>
	public async Task FinalizeAfterArtifactOutcomeAsync(Guid runId, CancellationToken cancellationToken)
	{
		RunPurgeStatus? status = await _purges.GetStatusAsync(runId, cancellationToken).ConfigureAwait(false);
		if (status is null)
		{
			return;
		}

		await ResumeAsync(status, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Advances an in-flight <c>run_purges</c> row by exactly one phase per call: the
	/// database phase if not yet done, then the artifact phase (enqueue if never
	/// started, or re-enqueue if the last attempt failed), then completion if both
	/// phases are already done.
	/// </summary>
	private async Task<RunPurgeResult> ResumeAsync(RunPurgeStatus status, CancellationToken cancellationToken)
	{
		if (!status.DbPhaseDone)
		{
			await RunDatabasePhaseAsync(status.RunId, cancellationToken).ConfigureAwait(false);
			await _purges.MarkDbPhaseDoneAsync(status.RunId, cancellationToken).ConfigureAwait(false);
			status = status with { DbPhaseDone = true };
		}

		if (status.ArtifactsPhase is "pending" or "failed")
		{
			IReadOnlyList<JobSummary> jobs = await _jobs.GetJobsForRunAsync(status.RunId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<Guid> scanJobIds = [.. jobs.Where(job => string.Equals(job.JobType, "scan", StringComparison.Ordinal)).Select(job => job.Id)];

			if (scanJobIds.Count == 0)
			{
				// Nothing to delete on disk (a discover/credential-test-only run never
				// produced an artifact) -- skip straight to done rather than enqueueing
				// a job with an empty inventory.
				await _purges.ReportArtifactOutcomeAsync(status.RunId, succeeded: true, artifactsDeleted: 0, lastError: null, cancellationToken).ConfigureAwait(false);
				status = status with { ArtifactsPhase = "done" };
			}
			else
			{
				string payload = JsonSerializer.Serialize(new { job_ids = scanJobIds.Select(id => id.ToString()).ToArray() });
				string purgeInitiator = $"purge:{status.RequestedBy}";
				Guid purgeRunId = await _jobs.CreateRunAsync("purge", "{}", credentialId: null, purgeInitiator, cancellationToken).ConfigureAwait(false);
				JobSpec spec = new("purge", PurgePriority, Payload: payload);
				IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(purgeRunId, [spec], purgeInitiator, cancellationToken).ConfigureAwait(false);
				await _purges.MarkArtifactJobEnqueuedAsync(status.RunId, jobIds[0], scanJobIds.Count, cancellationToken).ConfigureAwait(false);
				return new RunPurgeResult(RunPurgeOutcome.InProgress, status with { ArtifactsPhase = "running" });
			}
		}
		else if (status.ArtifactsPhase == "running")
		{
			return new RunPurgeResult(RunPurgeOutcome.InProgress, status);
		}

		// Both phases done -- finalize. RunType is re-read here (rather than threaded
		// through RunPurgeStatus, which is a purge-lifecycle projection, not a run
		// projection) because CompleteAsync/the tombstone need it and this is the one
		// call site that reaches completion.
		RunSummary? run = await _jobs.GetRunAsync(status.RunId, cancellationToken).ConfigureAwait(false);
		string runType = run?.RunType ?? "scan";
		RunPurgeTombstone tombstone = await _purges.CompleteAsync(
			status.RunId, runType, actor: status.RequestedBy, priorState: status.PriorState,
			artifactsDeleted: status.ArtifactsDeleted, cancellationToken).ConfigureAwait(false);
		return new RunPurgeResult(RunPurgeOutcome.Completed, ToStatus(tombstone));
	}

	/// <summary>
	/// Deletes the compliance-owned database projections this service has direct,
	/// owner-privileged access to (see class doc comment for why this list excludes
	/// artifact files and stops at what the API connection can actually reach):
	/// attestation_snapshots for the run, the migration 0066 evidence tables
	/// (component_results/component_result_findings/component_result_artifacts,
	/// upload_attempts) -- issue #745's stated "ADR-0019/retention wiring for the new
	/// tables (purge currently RESTRICTs)" remainder -- any leftover run_secrets row,
	/// and nulling schedules.last_run_id if this run was the referenced schedule's
	/// most recent.
	/// </summary>
	private async Task RunDatabasePhaseAsync(Guid runId, CancellationToken cancellationToken)
	{
		await _attestationSnapshots.DeleteForRunAsync(runId, cancellationToken).ConfigureAwait(false);

		IReadOnlyList<JobSummary> jobs = await _jobs.GetJobsForRunAsync(runId, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<Guid> jobIds = [.. jobs.Select(job => job.Id)];
		await DeleteComplianceEvidenceAsync(runId, jobIds, cancellationToken).ConfigureAwait(false);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand deleteSecret = new("DELETE FROM run_secrets WHERE run_id = $1", connection))
		{
			deleteSecret.Parameters.AddWithValue(runId);
			await deleteSecret.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand nullScheduleRef = new("UPDATE schedules SET last_run_id = NULL WHERE last_run_id = $1", connection))
		{
			nullScheduleRef.Parameters.AddWithValue(runId);
			await nullScheduleRef.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Migration 0066: deletes every component_results/component_result_findings/
	/// component_result_artifacts row for <paramref name="runId"/> (children first,
	/// the order that migration's append-only-trigger carve-out requires -- see its
	/// header) and every upload_attempts row for the run's own job ids, all inside
	/// one transaction with the two session-local GUCs (<c>waypoint.purge_run_id</c>,
	/// <c>waypoint.purge_job_ids</c>) the block-mutation triggers check, set via
	/// <c>SET LOCAL</c> so the exception cannot outlive this one statement batch or
	/// leak onto a pooled connection's next use -- same idiom
	/// <see cref="AttestationSnapshotRepository.DeleteForRunAsync"/> already
	/// established. A run with no component results/upload attempts (the common case
	/// for a non-scan-plan or legacy fan-out run) is a harmless no-op delete.
	/// </summary>
	private async Task DeleteComplianceEvidenceAsync(Guid runId, IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand setRunGuc = new("SELECT set_config('waypoint.purge_run_id', $1, true)", connection, transaction))
		{
			setRunGuc.Parameters.AddWithValue(runId.ToString());
			await setRunGuc.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		string jobIdsCsv = string.Join(',', jobIds);
		await using (NpgsqlCommand setJobsGuc = new("SELECT set_config('waypoint.purge_job_ids', $1, true)", connection, transaction))
		{
			setJobsGuc.Parameters.AddWithValue(jobIdsCsv);
			await setJobsGuc.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand deleteFindings = new(
			"DELETE FROM component_result_findings WHERE component_result_id IN (SELECT id FROM component_results WHERE run_id = $1)",
			connection, transaction))
		{
			deleteFindings.Parameters.AddWithValue(runId);
			await deleteFindings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand deleteArtifacts = new(
			"DELETE FROM component_result_artifacts WHERE component_result_id IN (SELECT id FROM component_results WHERE run_id = $1)",
			connection, transaction))
		{
			deleteArtifacts.Parameters.AddWithValue(runId);
			await deleteArtifacts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand deleteResults = new("DELETE FROM component_results WHERE run_id = $1", connection, transaction))
		{
			deleteResults.Parameters.AddWithValue(runId);
			await deleteResults.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (jobIds.Count > 0)
		{
			await using NpgsqlCommand deleteUploadAttempts = new(
				"DELETE FROM upload_attempts WHERE job_id = ANY($1)", connection, transaction);
			deleteUploadAttempts.Parameters.AddWithValue(jobIds.Select(id => id).ToArray());
			await deleteUploadAttempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private static RunPurgeOutcome ClassifyInFlight(RunPurgeStatus status) =>
		status.ArtifactsPhase == "failed" ? RunPurgeOutcome.Failed : RunPurgeOutcome.InProgress;

	private static RunPurgeStatus ToStatus(RunPurgeTombstone tombstone) => new(
		RunId: tombstone.RunId,
		RequestedBy: tombstone.Actor,
		RequestedAt: tombstone.OccurredAt,
		PriorState: tombstone.PriorState,
		DbPhaseDone: true,
		ArtifactsPhase: "done",
		ArtifactsTotal: 0,
		ArtifactsDeleted: 0,
		LastError: null,
		CompletedAt: tombstone.OccurredAt);
}
