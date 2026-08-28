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

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Scans;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #594 (epic #577): end-to-end coverage for <see cref="RunPurgeService"/>
/// against real Postgres -- terminal-only enforcement, full purge (database
/// projections gone, tombstone recorded with correct actor/time/prior-state/counts),
/// partial-failure retry (simulating the runner's own outcome report the way
/// <c>RunCompletionTests</c> drives <c>AdvanceStateAsync</c> directly rather than
/// running a real dispatcher loop), and idempotent re-invocation.
/// </summary>
[Collection("Postgres")]
public sealed class RunPurgeServiceTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _artifactRoot = Directory.CreateTempSubdirectory("waypoint-run-purge-service-artifacts-").FullName;
	private string _complianceRunnerConnectionString = string.Empty;
	private JobQueueRepository _jobs = null!;
	private RunPurgeRepository _purges = null!;
	private AttestationSnapshotRepository _attestationSnapshots = null!;
	private RunRetentionHoldRepository _retentionHolds = null!;
	private RunRetentionHoldService _holdService = null!;
	private RunPurgeService _service = null!;

	public RunPurgeServiceTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_purges = new RunPurgeRepository(_fixture.ConnectionString);
		_attestationSnapshots = new AttestationSnapshotRepository(_fixture.ConnectionString);
		_retentionHolds = new RunRetentionHoldRepository(_fixture.ConnectionString);
		_service = new RunPurgeService(_jobs, _purges, _attestationSnapshots, _retentionHolds, _fixture.ConnectionString);
		_holdService = new RunRetentionHoldService(_jobs, _retentionHolds, _purges);

		// Issue #1013 round-2: the artifact-purge handler must run under the REAL
		// least-privilege compliance-runner role, not the owner connection -- running
		// it as the owner is exactly how round 1's runner-side finalize call shipped
		// with a live 42501 (the role has no INSERT on run_purge_tombstones / DELETE
		// on run_purges, migration 0042's documented posture). Same fixed test-role
		// convention as RunnerRoleGrantDriftTests: PostgresFixture.CreateRunnerRolesAsync
		// provisions the role with "waypoint_test" against the owner host/port/db.
		NpgsqlConnectionStringBuilder runnerBuilder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_compliance_runner",
			Password = "waypoint_test",
		};
		_complianceRunnerConnectionString = runnerBuilder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		try
		{
			Directory.Delete(_artifactRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup only -- CI temp-dir sweep handles anything left over.
		}
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	/// <summary>
	/// Claims and hands the real, enqueued artifact-purge job to a real
	/// <see cref="PurgeJobHandler"/> -- the same handler compliance-runner runs,
	/// against the actual queued <c>purge</c> job row <see cref="RunPurgeService"/>
	/// created, rather than short-circuiting straight to
	/// <see cref="IRunPurgeRepository.ReportArtifactOutcomeAsync"/> the way the older
	/// tests in this file do. Issue #1013 round-2: the handler's repository runs under
	/// the REAL <c>waypoint_compliance_runner</c> role (migration 0042's grants), so a
	/// write the role is not granted fails here with 42501 exactly as it would live --
	/// the gap that let round 1's runner-side finalize call pass owner-connection tests
	/// while failing on the real stack. (The claim SELECT below stays on the owner
	/// connection: it simulates the dispatcher's claim machinery, which is not under
	/// test; every write the HANDLER makes goes through the runner-role repository.)
	/// </summary>
	private async Task<JobExecutionOutcome> RunArtifactPurgeJobAsync(Guid targetRunId, CancellationToken cancellationToken)
	{
		RunPurgeStatus? status = await _purges.GetStatusAsync(targetRunId, cancellationToken);
		Assert.NotNull(status);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync(cancellationToken);
		await using NpgsqlCommand claim = new(
			"SELECT id, run_id, job_type, target_id, target_name, credential_id, priority, payload::text, attempt_count, max_attempts FROM jobs WHERE job_type = 'purge' ORDER BY created_at DESC LIMIT 1",
			connection);
		await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync(cancellationToken);
		Assert.True(await reader.ReadAsync(cancellationToken));
		ClaimedJob job = new(
			Id: reader.GetGuid(0),
			RunId: reader.GetGuid(1),
			JobType: reader.GetString(2),
			TargetId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
			TargetName: reader.IsDBNull(4) ? null : reader.GetString(4),
			CredentialId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
			Priority: reader.GetInt16(6),
			Payload: reader.GetString(7),
			AttemptCount: reader.GetInt32(8),
			MaxAttempts: reader.GetInt32(9));
		await reader.CloseAsync();

		RunPurgeRepository runnerPurges = new(_complianceRunnerConnectionString);
		JobExecutionContext context = new(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository(_complianceRunnerConnectionString, NullLogger<JobQueueRepository>.Instance), JobShape.Simple);
		ScanOptions scanOptions = new() { ArtifactStorePath = _artifactRoot };
		PurgeJobHandler handler = new(runnerPurges, Options.Create(scanOptions), NullLogger<PurgeJobHandler>.Instance);
		return await handler.ExecuteAsync(context, cancellationToken);
	}

	/// <summary>
	/// One pass of the API-side finalization sweep
	/// (<see cref="RunPurgeFinalizeHostedService.SweepOnceAsync"/>) under the owner
	/// connection -- exactly what the API process's hosted service does in production
	/// on its own timer, never an operator re-POST. The e2e tests below drive this
	/// seam once deterministically instead of waiting out a real timer tick.
	/// </summary>
	private Task SweepPendingFinalizeAsync(CancellationToken cancellationToken)
	{
		RunPurgeFinalizeHostedService sweep = new(
			_service, _purges, Options.Create(new RunPurgeFinalizeOptions()),
			NullLogger<RunPurgeFinalizeHostedService>.Instance);
		return sweep.SweepOnceAsync(cancellationToken);
	}

	[Theory]
	[InlineData("completed")]
	[InlineData("completed_with_failures")]
	[InlineData("aborted")]
	public async Task PurgeRunAsync_TerminalRun_ProceedsPastTheGate(string terminalState)
	{
		Guid runId = await SeedRunAsync(terminalState);

		RunPurgeResult result = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);

		Assert.NotEqual(RunPurgeOutcome.RunNotTerminal, result.Outcome);
		Assert.NotEqual(RunPurgeOutcome.RunNotFound, result.Outcome);
	}

	[Theory]
	[InlineData("pending")]
	[InlineData("running")]
	public async Task PurgeRunAsync_NonTerminalRun_RejectedAndLeftIntact(string nonTerminalState)
	{
		Guid runId = await SeedRunAsync(nonTerminalState);

		RunPurgeResult result = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.RunNotTerminal, result.Outcome);
		Assert.Equal(nonTerminalState, await GetRunStateAsync(runId));
		Assert.Null(await _purges.GetStatusAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task PurgeRunAsync_UnknownRun_ReturnsRunNotFound()
	{
		RunPurgeResult result = await _service.PurgeRunAsync(Guid.NewGuid(), "admin-tester", CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.RunNotFound, result.Outcome);
	}

	[Fact]
	public async Task PurgeRunAsync_FullFlow_DeletesProjectionsAndRecordsTombstone()
	{
		Guid runId = await SeedRunAsync("completed");
		Guid scanJobId = await InsertScanJobAsync(runId);
		await InsertAttestationSnapshotAsync(runId, scanJobId);
		await InsertRunSecretAsync(runId);
		Guid scheduleId = await InsertScheduleReferencingRunAsync(runId);

		// Phase 1: kicks off the database phase and enqueues the artifact-deletion job.
		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// The database-side effects must be visible immediately -- they do not wait
		// on the artifact job.
		Assert.Empty(await _attestationSnapshots.ListForRunAsync(runId, CancellationToken.None));
		Assert.False(await RunSecretExistsAsync(runId));
		Assert.Null(await GetScheduleLastRunIdAsync(scheduleId));

		// Simulate the compliance-runner's PurgeJobHandler reporting success (the same
		// "drive the completion primitive directly" idiom RunCompletionTests uses
		// instead of running a real dispatcher loop).
		await _purges.ReportArtifactOutcomeAsync(runId, succeeded: true, artifactsDeleted: 3, lastError: null, CancellationToken.None);

		// Phase 2: re-invoking purge now finalizes.
		RunPurgeResult completed = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Completed, completed.Outcome);
		Assert.NotNull(completed.Status);
		Assert.Equal("completed", completed.Status!.PriorState);
		Assert.Equal("admin-tester", completed.Status.RequestedBy);
		Assert.True(completed.Status.DbPhaseDone);
		Assert.Equal("done", completed.Status.ArtifactsPhase);
		Assert.NotNull(completed.Status.CompletedAt);

		// The run_purges row is gone (only the tombstone remains as the historical record).
		Assert.Null(await _purges.GetStatusAsync(runId, CancellationToken.None));

		RunPurgeTombstone? tombstone = await _purges.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(tombstone);
		Assert.Equal(runId, tombstone!.RunId);
		Assert.Equal("admin-tester", tombstone.Actor);
		Assert.Equal("completed", tombstone.PriorState);
		Assert.Equal("completed", tombstone.Outcome);
		Assert.Contains("3", tombstone.DetailJson);

		Assert.NotNull(await GetRunPurgedAtAsync(runId));

		// Unrelated domain state must be untouched -- the run/job rows themselves stay
		// (design decision: runs/jobs are retained, not deleted).
		Assert.Equal("completed", await GetRunStateAsync(runId));
		Assert.True(await ScanJobRowExistsAsync(scanJobId));
	}

	/// <summary>
	/// Issue #1013 -- failing-test-first proof. Before the fix, this asserted the bug:
	/// a single <c>POST</c> plus the artifact job actually completing (via the real
	/// <see cref="PurgeJobHandler"/>, not a direct repository call) left
	/// <c>runs.purged_at</c> NULL, no tombstone, and <c>GET /runs/{id}/purge</c>
	/// (<see cref="RunPurgeService.GetStatusAsync"/>) reporting <see cref="RunPurgeOutcome.InProgress"/>
	/// forever -- exactly the round-8 live-proven stuck state, requiring a manual
	/// re-POST (<see cref="PurgeRunAsync_PartialFailureThenRetry_EventuallyCompletesWithoutDoubleReporting"/>
	/// and the older <see cref="PurgeRunAsync_FullFlow_DeletesProjectionsAndRecordsTombstone"/>
	/// simulate that same manual second call -- this test proves it is no longer
	/// necessary). This is now a passing regression test: the SAME operator action
	/// (one <see cref="RunPurgeService.PurgeRunAsync"/> call, the artifact job
	/// completing under its own steam AS the real runner role, then one automatic
	/// API-side <see cref="RunPurgeFinalizeHostedService"/> sweep pass) reaches
	/// <see cref="RunPurgeOutcome.Completed"/> with no second operator call at all --
	/// and without any runner-side write beyond migration 0042's granted
	/// column-limited UPDATE.
	/// </summary>
	[Fact]
	public async Task PurgeRunAsync_RunWithArtifacts_FinalizesInOneOperatorActionWithoutASecondPurgeCall()
	{
		Guid runId = await SeedRunAsync("completed");
		Guid scanJobId = await InsertScanJobAsync(runId);
		WriteArtifactFiles(scanJobId);

		// The one and only call into RunPurgeService this test makes.
		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// The artifact-purge job completing under its own steam AND under the real
		// waypoint_compliance_runner role -- exactly what compliance-runner's
		// JobDispatcherHostedService does in production, never a second call into
		// RunPurgeService/PurgeRunAsync.
		JobExecutionOutcome jobOutcome = await RunArtifactPurgeJobAsync(runId, CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, jobOutcome.Kind);

		Assert.False(File.Exists(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId)));
		Assert.False(File.Exists(ScanArtifactPaths.AttestedHdf(_artifactRoot, scanJobId)));
		Assert.False(File.Exists(ScanArtifactPaths.Ckl(_artifactRoot, scanJobId)));

		// The API-side finalization sweep firing on its own timer -- the automatic
		// mechanism, not an operator action.
		await SweepPendingFinalizeAsync(CancellationToken.None);

		// GET /runs/{id}/purge (RunPurgeService.GetStatusAsync) -- no POST issued again.
		RunPurgeResult? status = await _service.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(status);
		Assert.Equal(RunPurgeOutcome.Completed, status!.Outcome);
		Assert.NotNull(status.Status);
		Assert.Equal("done", status.Status!.ArtifactsPhase);

		Assert.Null(await _purges.GetStatusAsync(runId, CancellationToken.None));
		RunPurgeTombstone? tombstone = await _purges.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(tombstone);
		Assert.Equal("completed", tombstone!.Outcome);
		Assert.NotNull(await GetRunPurgedAtAsync(runId));

		// The evidence-table read surfaces (#961/#1010) must render honest-empty --
		// the component-result rows this run's evidence deletion (ADR-0019) removed
		// stay removed; this run never had any inserted (SeedRunAsync/InsertScanJobAsync
		// do not create component_results rows), so "honest empty" here means the
		// finalize path did not somehow resurrect or leave dangling evidence.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand countResults = new("SELECT COUNT(*) FROM component_results WHERE run_id = $1", connection);
		countResults.Parameters.AddWithValue(runId);
		Assert.Equal(0L, (long)(await countResults.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PurgeRunAsync_PartialFailureThenRetry_EventuallyCompletesWithoutDoubleReporting()
	{
		Guid runId = await SeedRunAsync("completed");
		await InsertScanJobAsync(runId);

		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// Simulate PurgeJobHandler reporting a partial failure.
		await _purges.ReportArtifactOutcomeAsync(runId, succeeded: false, artifactsDeleted: 1, lastError: "permission denied", CancellationToken.None);

		RunPurgeResult? failed = await _service.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(failed);
		Assert.Equal(RunPurgeOutcome.Failed, failed!.Outcome);

		// Retry: PurgeRunAsync must not redo the (already-committed) database phase --
		// db_phase_done stays true across the retry -- and must re-attempt the
		// artifact phase (a fresh enqueue) rather than getting stuck on 'failed'.
		RunPurgeResult retried = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, retried.Outcome);
		Assert.True(retried.Status!.DbPhaseDone);
		Assert.Equal("running", retried.Status.ArtifactsPhase);

		// This time the runner reports success.
		await _purges.ReportArtifactOutcomeAsync(runId, succeeded: true, artifactsDeleted: 3, lastError: null, CancellationToken.None);
		RunPurgeResult completed = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Completed, completed.Outcome);

		// Exactly one tombstone -- no double-write from the retry cycle.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT COUNT(*) FROM run_purge_tombstones WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #1013: pins the retry path end-to-end through the REAL
	/// <see cref="PurgeJobHandler"/> under the real runner role (not a direct
	/// repository report) for a genuine mid-phase failure -- the handler itself hits
	/// an undeletable file and reports failure, and the API-side finalization sweep
	/// (<see cref="RunPurgeFinalizeHostedService"/>) must NOT finalize that failed
	/// report; the purge must be left honestly <see cref="RunPurgeOutcome.Failed"/>,
	/// not silently completed. Only after a genuine operator retry (permissions
	/// fixed, purge re-POSTed) does the artifact job re-run, succeed, and the next
	/// sweep pass finalize.
	/// </summary>
	[Fact]
	public async Task PurgeRunAsync_RealHandlerMidPhaseFailure_LeavesHonestlyInProgressThenRetryCompletes()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Same POSIX-permission-shaped skip as PurgeJobHandlerTests -- this repo's
			// CI targets Linux containers (deploy/*, docs/testing.md).
			return;
		}

		Guid runId = await SeedRunAsync("completed");
		Guid scanJobId = await InsertScanJobAsync(runId);
		string rawHdfPath = ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId);
		File.WriteAllText(rawHdfPath, "{}");

		string lockedDirectory = Path.Combine(_artifactRoot, $"locked-{Guid.NewGuid():N}");
		Directory.CreateDirectory(lockedDirectory);

		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// Force the handler's own deletion to fail: lock the (test-specific, shared
		// ScanOptions.ArtifactStorePath) root itself down to read-only-execute so
		// File.Delete on the raw HDF underneath it throws.
		File.SetUnixFileMode(_artifactRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		try
		{
			JobExecutionOutcome failedJobOutcome = await RunArtifactPurgeJobAsync(runId, CancellationToken.None);
			Assert.Equal(JobOutcomeKind.Failed, failedJobOutcome.Kind);
		}
		finally
		{
			File.SetUnixFileMode(_artifactRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
		}

		// Honestly failed -- and the API-side sweep must NOT finalize (or touch) a
		// failed pass: ListPendingFinalizeRunIdsAsync selects artifacts_phase='done'
		// only, so a sweep tick here leaves the retryable failure fully intact.
		await SweepPendingFinalizeAsync(CancellationToken.None);
		RunPurgeResult? afterFailure = await _service.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(afterFailure);
		Assert.Equal(RunPurgeOutcome.Failed, afterFailure!.Outcome);
		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
		Assert.Null(await GetRunPurgedAtAsync(runId));

		// Retry: operator re-POSTs; RunPurgeService re-enqueues a fresh artifact job.
		RunPurgeResult retried = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, retried.Outcome);
		Assert.Equal("running", retried.Status!.ArtifactsPhase);

		// This time deletion succeeds (real runner role again) -- and the next
		// automatic sweep pass finalizes without any further operator call.
		JobExecutionOutcome retryJobOutcome = await RunArtifactPurgeJobAsync(runId, CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, retryJobOutcome.Kind);
		await SweepPendingFinalizeAsync(CancellationToken.None);

		RunPurgeResult? completed = await _service.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(completed);
		Assert.Equal(RunPurgeOutcome.Completed, completed!.Outcome);
		Assert.NotNull(await GetRunPurgedAtAsync(runId));

		RunPurgeTombstone? tombstone = await _purges.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(tombstone);
		Assert.Equal("completed", tombstone!.Outcome);
	}

	/// <summary>
	/// Issue #1013 round-2 (RunnerRoleGrantDriftTests pattern): migration 0042's
	/// documented posture -- "INSERT/DELETE stay API-only ... nothing runner-side ever
	/// removes a run_purges row" -- must HOLD, not silently drift. Round 1 of this fix
	/// called finalization from inside the runner process and shipped a live 42501
	/// precisely because every purge test ran under the owner connection; these two
	/// facts pin the boundary in both directions: the writes the runner is granted
	/// succeed (proven by the e2e tests above running the real handler as the real
	/// role), and the two finalization writes it is deliberately NOT granted are
	/// denied.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_InsertRunPurgeTombstone_IsDenied()
	{
		Guid runId = await SeedRunAsync("completed");

		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO run_purge_tombstones (run_id, run_type, prior_state, actor, outcome, detail)
			VALUES ($1, 'scan', 'completed', 'runner-should-not-be-able-to-do-this', 'completed', '{}'::jsonb)
			""", connection);
		insert.Parameters.AddWithValue(runId);

		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
	}

	/// <inheritdoc cref="ComplianceRunnerRole_InsertRunPurgeTombstone_IsDenied"/>
	[Fact]
	public async Task ComplianceRunnerRole_DeleteRunPurgesRow_IsDenied()
	{
		Guid runId = await SeedRunAsync("completed");
		await _purges.CreateAsync(runId, "admin-tester", "completed", CancellationToken.None);

		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM run_purges WHERE run_id = $1", connection);
		delete.Parameters.AddWithValue(runId);

		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

		// The row is untouched -- still visible, still finalizable API-side.
		Assert.NotNull(await _purges.GetStatusAsync(runId, CancellationToken.None));
	}

	/// <summary>
	/// Issue #784 round-2 finding 2 -- the resumed/finalize path. The hold refusal in
	/// <see cref="RunPurgeService.PurgeRunAsync"/> is NOT the only gate that matters,
	/// because <see cref="RunPurgeFinalizeHostedService"/> reaches the same resume
	/// logic through <see cref="RunPurgeService.FinalizePendingAsync"/> on a timer,
	/// with no operator involved. Without a hold check there, a run held while its
	/// purge was in flight would still be tombstoned and have <c>runs.purged_at</c>
	/// set by a background sweep -- the "partially preserved graph, then silently
	/// finished anyway" outcome #784 exists to prevent.
	///
	/// The semantics this pins, stated as the code actually behaves: the halt is not a
	/// rollback (the artifact files this test's real handler already deleted stay
	/// deleted, and the database phase stays committed), but nothing FURTHER happens
	/// and the purge is never presented as complete -- no tombstone, no
	/// <c>runs.purged_at</c>, and the <c>run_purges</c> row survives so
	/// <c>GET /runs/{id}/purge</c> keeps reporting the partially-purged state
	/// honestly. Removing the hold is the only thing that lets the next sweep pass
	/// finish the job.
	/// </summary>
	[Fact]
	public async Task FinalizePendingAsync_HeldRun_RefusesAndLeavesThePartiallyPurgedStateVisible()
	{
		Guid runId = await SeedRunAsync("completed");
		Guid scanJobId = await InsertScanJobAsync(runId);
		WriteArtifactFiles(scanJobId);

		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// The runner's artifact job completes under its own steam and reports done --
		// the exact state (db_phase_done + artifacts_phase = 'done') the background
		// finalize sweep selects on.
		JobExecutionOutcome jobOutcome = await RunArtifactPurgeJobAsync(runId, CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, jobOutcome.Kind);

		// The hold lands after all that -- the worst case for the refusal.
		PlaceRetentionHoldResult placed = await _holdService.PlaceHoldAsync(runId, "invented-legal-hold-reason", "admin-alice", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.Placed, placed.Outcome);

		// Both automatic paths must refuse: the sweep's own entry point, and the sweep.
		Assert.False(await _service.FinalizePendingAsync(runId, CancellationToken.None));
		await SweepPendingFinalizeAsync(CancellationToken.None);

		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
		Assert.Null(await GetRunPurgedAtAsync(runId));

		// Visible, not silently abandoned: the run_purges row survives, so the
		// partially-purged state is still readable rather than being reported as
		// either untouched or complete.
		RunPurgeStatus? stillInFlight = await _purges.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(stillInFlight);
		Assert.True(stillInFlight!.DbPhaseDone);
		Assert.Equal("done", stillInFlight.ArtifactsPhase);

		// An operator re-POST is refused for the same reason, so there is no way
		// around the hold at all while it stands.
		Assert.Equal(RunPurgeOutcome.Held, (await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None)).Outcome);

		// Removing the hold is the ONLY thing that clears the stuck row: the next
		// sweep pass finalizes with no other state change.
		Assert.Equal(RemoveRetentionHoldOutcome.Removed, (await _holdService.RemoveHoldAsync(runId, "invented-unhold-reason", "admin-alice", CancellationToken.None)).Outcome);
		await SweepPendingFinalizeAsync(CancellationToken.None);

		Assert.NotNull(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
		Assert.NotNull(await GetRunPurgedAtAsync(runId));
	}

	/// <summary>
	/// Issue #784 round-2 finding 2 -- the runner job at claim time. The
	/// compliance-runner has no grant on <c>run_retention_holds</c> (migration 0075),
	/// so an artifact-deletion job that is already sitting in the queue when a hold
	/// lands cannot re-check the hold itself. The hold is honoured by cancelling that
	/// job API-side instead
	/// (<c>RunRetentionHoldService.HaltInFlightArtifactDeletionAsync</c>): the job row
	/// moves to <c>cancelled</c>, so the dispatcher's claim query never selects it and
	/// the files on disk are never touched.
	/// </summary>
	[Fact]
	public async Task PlaceHoldAsync_WithArtifactJobStillQueued_CancelsItSoTheRunnerNeverClaimsIt()
	{
		Guid runId = await SeedRunAsync("completed");
		Guid scanJobId = await InsertScanJobAsync(runId);
		WriteArtifactFiles(scanJobId);

		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		Guid? artifactJobId = await _purges.GetArtifactJobIdAsync(runId, CancellationToken.None);
		Assert.NotNull(artifactJobId);
		Assert.Equal("queued", await GetJobStateAsync(artifactJobId!.Value));

		PlaceRetentionHoldResult placed = await _holdService.PlaceHoldAsync(runId, "invented-legal-hold-reason", "admin-alice", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.Placed, placed.Outcome);

		// The job is off the queue entirely -- no runner can claim it now.
		Assert.Equal("cancelled", await GetJobStateAsync(artifactJobId.Value));

		// And nothing on disk was deleted.
		Assert.True(File.Exists(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId)));
		Assert.True(File.Exists(ScanArtifactPaths.AttestedHdf(_artifactRoot, scanJobId)));
		Assert.True(File.Exists(ScanArtifactPaths.Ckl(_artifactRoot, scanJobId)));
	}

	private async Task<string> GetJobStateAsync(Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT state FROM jobs WHERE id = $1", c);
		q.Parameters.AddWithValue(jobId);
		return (string)(await q.ExecuteScalarAsync())!;
	}

	private void WriteArtifactFiles(Guid scanJobId)
	{
		File.WriteAllText(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId), "{}");
		File.WriteAllText(ScanArtifactPaths.AttestedHdf(_artifactRoot, scanJobId), "{}");
		File.WriteAllText(ScanArtifactPaths.Ckl(_artifactRoot, scanJobId), "<CHECKLIST/>");
	}

	[Fact]
	public async Task PurgeRunAsync_AlreadyPurged_IsAFastCleanNoOp()
	{
		Guid runId = await SeedRunAsync("aborted"); // no scan jobs -- exercises the "nothing to delete on disk" fast path

		RunPurgeResult first = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Completed, first.Outcome);

		RunPurgeResult second = await _service.PurgeRunAsync(runId, "someone-else", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.AlreadyPurged, second.Outcome);
		// The tombstone's actor/prior-state reflect the ORIGINAL purge, not the second caller.
		Assert.Equal("admin-tester", second.Status!.RequestedBy);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT COUNT(*) FROM run_purge_tombstones WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PurgeRunAsync_NoScanJobs_SkipsArtifactEnqueueAndCompletesDirectly()
	{
		// A discover/credential-test-only run never produced an artifact -- purge
		// must not enqueue an empty-inventory purge job, and must still complete.
		Guid runId = await SeedRunAsync("completed");
		await InsertNonScanJobAsync(runId, "credential-test");

		RunPurgeResult result = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.Completed, result.Outcome);
		Assert.Equal(0, result.Status!.ArtifactsDeleted);
	}

	[Fact]
	public async Task GetStatusAsync_NeverRequested_ReturnsNull()
	{
		Guid runId = await SeedRunAsync("completed");

		RunPurgeResult? result = await _service.GetStatusAsync(runId, CancellationToken.None);

		Assert.Null(result);
	}

	// -- seeding/reading helpers ---------------------------------------------------

	private async Task<Guid> SeedRunAsync(string state)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}', $1) RETURNING id", c);
		q.Parameters.AddWithValue(state);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertScanJobAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, 'scan', 1, 'done') RETURNING id", c);
		q.Parameters.AddWithValue(runId);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertNonScanJobAsync(Guid runId, string jobType)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, $2, 1, 'done') RETURNING id", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(jobType);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task InsertAttestationSnapshotAsync(Guid runId, Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO attestation_snapshots (run_id, job_id, target_id, profile, scope, applied, expired)
			VALUES ($1, $2, $3, 'vsphere-stig', 'target', true, false)
			""", c);
		q.Parameters.AddWithValue(runId);
		q.Parameters.AddWithValue(jobId);
		q.Parameters.AddWithValue(Guid.NewGuid());
		await q.ExecuteNonQueryAsync();
	}

	private async Task InsertRunSecretAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
			VALUES ($1, 'leftover-user', '\x00'::bytea, '\x00'::bytea, 'k', 'AES-256-GCM', now() + interval '1 hour')
			""", c);
		q.Parameters.AddWithValue(runId);
		await q.ExecuteNonQueryAsync();
	}

	private async Task<Guid> InsertScheduleReferencingRunAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new(
			"""
			INSERT INTO schedules (name, job_type, cron_expression, last_run_id, created_by)
			VALUES ($1, 'scan', '0 0 * * *', $2, 'tester')
			RETURNING id
			""", c);
		q.Parameters.AddWithValue($"purge-test-schedule-{Guid.NewGuid():N}");
		q.Parameters.AddWithValue(runId);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task<string> GetRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT state FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		return (string)(await q.ExecuteScalarAsync())!;
	}

	private async Task<DateTimeOffset?> GetRunPurgedAtAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT purged_at FROM runs WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		object? result = await q.ExecuteScalarAsync();
		return result switch
		{
			null or DBNull => null,
			DateTimeOffset dto => dto,
			DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
			_ => null,
		};
	}

	private async Task<bool> RunSecretExistsAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT EXISTS(SELECT 1 FROM run_secrets WHERE run_id = $1)", c);
		q.Parameters.AddWithValue(runId);
		return (bool)(await q.ExecuteScalarAsync())!;
	}

	private async Task<Guid?> GetScheduleLastRunIdAsync(Guid scheduleId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT last_run_id FROM schedules WHERE id = $1", c);
		q.Parameters.AddWithValue(scheduleId);
		object? result = await q.ExecuteScalarAsync();
		return result is Guid guid ? guid : null;
	}

	private async Task<bool> ScanJobRowExistsAsync(Guid jobId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("SELECT EXISTS(SELECT 1 FROM jobs WHERE id = $1)", c);
		q.Parameters.AddWithValue(jobId);
		return (bool)(await q.ExecuteScalarAsync())!;
	}
}
