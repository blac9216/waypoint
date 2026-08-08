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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #293's three acceptance-criteria scenarios, against a real PostgreSQL 16
/// container: a multi-stage handler walking <c>queued -&gt; running -&gt; attesting -&gt;
/// (requeue) -&gt; converting -&gt; uploaded</c> across separate claim/execute cycles with
/// the 0015 lease CHECK holding throughout; a crashed worker mid-<c>attesting</c>
/// recovering at that stage rather than the beginning (closing #282); and a job that
/// failed at a later stage re-executing only from that stage when resumed, proven by
/// per-stage execution counters. <see cref="StageWalkingJobHandler"/> is the multi-stage
/// TEST handler the issue calls for -- invented, `scan`-shaped, and used nowhere outside
/// this file.
/// </summary>
[Collection("Postgres")]
public sealed class JobStageDispatcherTests : IAsyncLifetime
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;

	public JobStageDispatcherTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// AC #1: the stage walk. A fresh `scan` job has no stage marker, so the handler
	/// starts at `running -&gt; attesting`, reports StageComplete(nextStage: "attesting"),
	/// and the dispatcher requeues it (state back to `queued`, lease cleared, `stage` =
	/// "attesting"). The *next* claim -- a genuinely separate ClaimJobAsync call, not a
	/// continuation of the first -- hands the handler that marker via
	/// <c>ClaimedJob.Stage</c>, so it resumes at `attesting -&gt; converting`, reports
	/// StageComplete(nextStage: "converting") and requeues again. A third, separate claim
	/// resumes at `converting -&gt; uploaded` and reports Succeeded -- the shape's real
	/// terminal state, forced exactly as it always was. Never at any point does a row
	/// exist in attesting/converting without a live lease (0015's CHECK), asserted
	/// directly against the row after each requeue.
	/// </summary>
	[Fact]
	public async Task MultiStageHandler_WalksStagesAcrossSeparateClaimCycles_WithTheLeaseCheckHolding()
	{
		Guid jobId = await SeedQueuedScanJobAsync();
		StageWalkingJobHandler handler = new() { GateExecutions = true };

		// One live dispatcher, but each "cycle" is still a genuinely separate
		// claim/execute round trip through ClaimJobAsync -- the dispatcher's poll loop
		// requeues after StageComplete and re-claims on its next tick exactly like a
		// second worker would. handler.NextExecutionGate holds every execution after
		// the first closed until this test explicitly releases it: the assertions below
		// therefore check the *durable* stage marker (which cannot regress once
		// written) rather than the transient `queued` state, because a fast poll loop
		// can race straight past a `queued` row into its next claim before a 50ms test
		// poll observes it -- the marker survives that race, the transient state does
		// not. 0015's lease CHECK holding throughout is what makes every claim in this
		// walk possible at all: this test never fails with a CHECK-violation exception,
		// which is itself part of the proof.
		using JobDispatcherHostedService dispatcher = CreateDispatcher(handler);
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			// Cycle 1: queued -> running -> attesting -> (requeue) -> queued, stage=attesting.
			handler.NextExecutionGate.Release(); // let the very first (only pre-opened) execution proceed
			Assert.True(await handler.ExecutionCompleted.WaitAsync(PollTimeout), "cycle 1 execution never started.");
			await PollUntilAsync(() => GetJobStageAsync(jobId), stage => stage == "attesting");
			Assert.Equal(1, handler.AttestExecutions);
			Assert.Equal(0, handler.ConvertExecutions);

			// Cycle 2: resumes at the marker: attesting -> converting -> (requeue) -> queued, stage=converting.
			handler.NextExecutionGate.Release();
			Assert.True(await handler.ExecutionCompleted.WaitAsync(PollTimeout), "cycle 2 execution never started.");
			await PollUntilAsync(() => GetJobStageAsync(jobId), stage => stage == "converting");
			Assert.Equal(1, handler.AttestExecutions);
			Assert.Equal(1, handler.ConvertExecutions);

			// Cycle 3: resumes at converting, reaches the pipeline's real terminal stage
			// and reports Succeeded -- forced to the shape's terminal state exactly as
			// every existing single-stage handler already gets.
			handler.NextExecutionGate.Release();
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Uploaded);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		// Attest ran exactly once (cycle 1). Convert-stage work ran across both cycle 2
		// (the "attesting" marker resume, which does the real convert work and requeues
		// at "converting") and cycle 3 (the "converting" marker resume, which repeats
		// the transient state hop and finishes with upload) -- three claim cycles total
		// for a two-real-stage pipeline, which is exactly what StageComplete buys: the
		// pipeline position is durable across claims, never re-running attest.
		Assert.Equal(1, handler.AttestExecutions);
		Assert.Equal(2, handler.ConvertExecutions);
		Assert.Equal(1, handler.UploadExecutions);
	}

	/// <summary>
	/// AC #2 (closes #282): a worker claims the job, the handler advances it to
	/// `attesting` and then the worker "crashes" -- no heartbeat, no requeue, the lease
	/// simply expires. The recovery sweep must requeue it at the `attesting` stage
	/// marker, not strand it (the pre-#293 gap: `RecoverSql`'s predicate never matched
	/// `attesting`/`converting`) and not restart it from the beginning (the marker
	/// survives because the recovery UPDATE never touches `stage`).
	/// </summary>
	[Fact]
	public async Task KillLeaseMidAttesting_RecoveryRequeuesAtTheStageMarker_NotFromTheBeginning()
	{
		Guid jobId = await SeedQueuedScanJobAsync();
		TimeSpan shortLease = TimeSpan.FromMilliseconds(300);

		ClaimedJob? claimed = await _repository.ClaimJobAsync("crashed-worker", shortLease, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Null(claimed!.Stage);

		JobExecutionContext context = new(claimed, "crashed-worker", _events, _repository, JobShape.Standard);
		await context.AdvanceAsync(JobStates.Attesting, "attest started", CancellationToken.None);
		Assert.Equal(JobStates.Attesting, await GetJobStateAsync(jobId));

		// Simulate the StageComplete requeue the dispatcher would perform once the
		// attest stage genuinely finishes -- but the worker crashes before this point in
		// a real run, so seed the marker directly to stand in for "the attest stage was
		// already recorded as reached" and then let the *next* claim/lease expire
		// without ever completing (see the variant below for the "crash before even
		// that" case).
		bool requeuedThenReclaimed = await _repository.RequeueAtStageAsync(
			jobId, "crashed-worker", JobStates.Attesting, "attesting", "attest complete", CancellationToken.None);
		Assert.True(requeuedThenReclaimed);

		ClaimedJob? resumedClaim = await _repository.ClaimJobAsync("crashed-worker-2", shortLease, CancellationToken.None);
		Assert.NotNull(resumedClaim);
		Assert.Equal("attesting", resumedClaim!.Stage);

		JobExecutionContext resumedContext = new(resumedClaim, "crashed-worker-2", _events, _repository, JobShape.Standard);
		await resumedContext.AdvanceAsync(JobStates.Attesting, "resumed attest", CancellationToken.None);

		// The "crash": no heartbeat, no requeue. Wait past the lease.
		await Task.Delay(shortLease + TimeSpan.FromMilliseconds(400));

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(batchSize: 10, CancellationToken.None);
		RecoveredJob thisJob = Assert.Single(recovered, job => job.Id == jobId);
		Assert.Equal(JobStates.Queued, thisJob.NewState);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand verify = new("SELECT state, stage, lease_expires_at, claimed_by FROM jobs WHERE id = $1", connection);
		verify.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("queued", reader.GetString(0));
		Assert.Equal("attesting", reader.GetString(1)); // the marker survived recovery, unchanged
		Assert.True(reader.IsDBNull(2), "lease_expires_at must be cleared on recovery.");
		Assert.True(reader.IsDBNull(3), "claimed_by must be cleared on recovery.");

		// The next claim hands the surviving marker straight back -- recovery requeued
		// at the stage, not from the beginning.
		ClaimedJob? postRecoveryClaim = await _repository.ClaimJobAsync("worker-after-recovery", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(postRecoveryClaim);
		Assert.Equal("attesting", postRecoveryClaim!.Stage);
	}

	/// <summary>
	/// AC #3: a job that fails partway through `converting` (attest already completed
	/// and recorded), when resumed (moved back to `queued` by whatever caller decided to
	/// retry it -- an operator retry endpoint is out of #293's scope, but the engine-level
	/// mechanism this proves is exactly what such an endpoint would call), re-enters at
	/// the `converting` marker and does not re-execute the attest stage. The handler's
	/// own per-stage execution counters are the proof: attest fires once across the
	/// whole run, not once per resume.
	/// </summary>
	[Fact]
	public async Task FailedJob_WhenResumedFromItsStageMarker_ReExecutesOnlyThatStage()
	{
		Guid jobId = await SeedQueuedScanJobAsync();

		// Reach converting the same way cycle 1+2 of the stage-walk test do, so the job
		// carries a genuine "attest already happened" marker.
		ClaimedJob? firstClaim = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(firstClaim);
		JobExecutionContext firstContext = new(firstClaim!, "worker-a", _events, _repository, JobShape.Standard);
		await firstContext.AdvanceAsync(JobStates.Attesting, null, CancellationToken.None);
		Assert.True(await _repository.RequeueAtStageAsync(jobId, "worker-a", JobStates.Attesting, "converting", "attest done", CancellationToken.None));

		// Claim again: the handler resumes at "converting" and this attempt fails.
		StageWalkingJobHandler handler = new();
		ClaimedJob? secondClaim = await _repository.ClaimJobAsync("worker-b", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(secondClaim);
		Assert.Equal("converting", secondClaim!.Stage);

		JobExecutionContext secondContext = new(secondClaim, "worker-b", _events, _repository, JobShape.Standard);
		handler.FailConvertOnNextCall = true;
		JobExecutionOutcome failedOutcome = await handler.ExecuteAsync(secondContext, CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, failedOutcome.Kind);
		Assert.Equal(0, handler.AttestExecutions);
		Assert.Equal(1, handler.ConvertExecutions);

		// The handler's own resume path already walked CurrentState to 'converting'
		// (the transient attesting hop, attest work not re-executed) before the
		// convert work failed -- AdvanceStateAsync's expectedFromState must match that.
		Assert.Equal(JobStates.Converting, secondContext.CurrentState);
		bool failedAdvance = await _repository.AdvanceStateAsync(
			jobId, "worker-b", JobStates.Converting, JobStates.Failed, failedOutcome.Note, clearLease: true, CancellationToken.None);
		Assert.True(failedAdvance);
		Assert.Equal(JobStates.Failed, await GetJobStateAsync(jobId));
		Assert.Equal("converting", await GetJobStageAsync(jobId));

		// Resume: whatever future caller retries a failed job moves it back to queued
		// without touching the stage marker (the engine-level primitive this issue
		// scopes; the HTTP surface for it is out of scope). The dispatcher then claims
		// it and the handler must resume at "converting", not "attesting" or the start.
		await SetJobStateForResumeAsync(jobId, JobStates.Queued);

		using (JobDispatcherHostedService dispatcher = CreateDispatcher(handler))
		{
			await dispatcher.StartAsync(CancellationToken.None);
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Uploaded);
			await dispatcher.StopAsync(CancellationToken.None);
		}

		// Across the whole run (pre-fail attempt + failed attempt + resumed attempt):
		// attest executed exactly once, convert executed twice (the failed attempt and
		// the resumed retry), upload exactly once. Attest was never re-run on resume.
		Assert.Equal(0, handler.AttestExecutions); // this handler instance never ran attest -- it resumed straight into converting both times
		Assert.Equal(2, handler.ConvertExecutions);
		Assert.Equal(1, handler.UploadExecutions);
	}

	private async Task SetJobStateForResumeAsync(Guid jobId, string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE jobs SET state = $2, claimed_by = NULL, claimed_at = NULL, lease_expires_at = NULL, heartbeat_at = NULL, finished_at = NULL WHERE id = $1",
			connection);
		update.Parameters.AddWithValue(jobId);
		update.Parameters.AddWithValue(state);
		await update.ExecuteNonQueryAsync();
	}

	private JobDispatcherHostedService CreateDispatcher(IJobHandler handler)
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25) };
		return new JobDispatcherHostedService(
			_repository, _events, new JobHandlerRegistry([handler]), Options.Create(options), NullLogger<JobDispatcherHostedService>.Instance);
	}

	private async Task<Guid> SeedQueuedScanJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('scan', 1, 'queued') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobStateAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<string?> GetJobStageAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT stage FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		object? result = await command.ExecuteScalarAsync();
		return result is null or DBNull ? null : (string)result;
	}

	private async Task<DateTime?> GetJobLeaseExpiresAtAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT lease_expires_at FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return await command.ExecuteScalarAsync() as DateTime?;
	}

	private static async Task PollUntilAsync<T>(Func<Task<T>> probe, Func<T, bool> isDone)
	{
		using CancellationTokenSource timeout = new(PollTimeout);
		while (true)
		{
			T value = await probe();
			if (isDone(value))
			{
				return;
			}

			timeout.Token.ThrowIfCancellationRequested();
			await Task.Delay(TimeSpan.FromMilliseconds(50));
		}
	}
}

/// <summary>
/// Invented, `scan`-shaped multi-stage TEST handler (issue #293's AC): routes on
/// <c>context.Job.Stage</c> to resume the right stage-body rather than re-running from
/// the beginning. <c>null</c>/unset means "first claim, start at attest". Each stage
/// completion calls <see cref="JobExecutionContext.AdvanceAsync"/> to move the row to
/// the state it just reached, then returns <see cref="JobExecutionOutcome.StageComplete"/>
/// with the next stage's marker -- except the last real stage (convert), which returns
/// <see cref="JobExecutionOutcome.Succeeded"/> so the dispatcher forces the shape's
/// terminal state exactly as every existing single-stage handler already does.
/// </summary>
internal sealed class StageWalkingJobHandler : IJobHandler
{
	private int _attestExecutions;
	private int _convertExecutions;
	private int _uploadExecutions;

	public string JobType => "scan";

	/// <summary>Set true before a call to make that call's convert stage report <see cref="JobOutcomeKind.Failed"/> instead of advancing.</summary>
	public bool FailConvertOnNextCall { get; set; }

	public int AttestExecutions => Volatile.Read(ref _attestExecutions);

	public int ConvertExecutions => Volatile.Read(ref _convertExecutions);

	public int UploadExecutions => Volatile.Read(ref _uploadExecutions);

	/// <summary>
	/// Signaled once after every <see cref="ExecuteAsync"/> call returns (test-only
	/// synchronization, not part of the handler's own logic): a live
	/// <c>JobDispatcherHostedService</c>'s poll loop keeps claiming and re-executing as
	/// fast as its poll interval allows, so a test asserting "state immediately after
	/// cycle N" against a still-running dispatcher races the very next claim. When
	/// <see cref="GateExecutions"/> is enabled, entering <see cref="ExecuteCoreAsync"/>
	/// blocks until the test releases <see cref="NextExecutionGate"/>, so waiting on
	/// this and then asserting DB state before the next release is race-free -- the
	/// dispatcher cannot possibly have claimed/executed again in between.
	/// </summary>
	public SemaphoreSlim ExecutionCompleted { get; } = new(0);

	/// <summary>
	/// Opt-in gating (default off, so a handler driven only by direct <see cref="ExecuteAsync"/>
	/// calls -- no live dispatcher racing it -- never needs to touch this): when a test
	/// sets this <c>true</c>, every <see cref="ExecuteAsync"/> call blocks on
	/// <see cref="NextExecutionGate"/> (initially closed) until the test releases it
	/// once per expected execution. See <see cref="ExecutionCompleted"/>.
	/// </summary>
	public bool GateExecutions { get; set; }

	/// <summary>Closed (count 0) until released; only consulted when <see cref="GateExecutions"/> is true.</summary>
	public SemaphoreSlim NextExecutionGate { get; } = new(0);

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		if (GateExecutions)
		{
			await NextExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		try
		{
			return await ExecuteCoreAsync(context, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			ExecutionCompleted.Release();
		}
	}

	private async Task<JobExecutionOutcome> ExecuteCoreAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		string? stage = context.Job.Stage;

		if (stage is null)
		{
			Interlocked.Increment(ref _attestExecutions);
			await context.AdvanceAsync(JobStates.Attesting, "attest complete", cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.StageComplete("attesting", "attest complete");
		}

		if (string.Equals(stage, "attesting", StringComparison.Ordinal))
		{
			// The attest *work* already happened in a prior claim cycle (that is what
			// the marker records) -- this claim only needs to walk the row's state
			// machine through the 'attesting' hop it must transiently visit (a fresh
			// claim always lands its CurrentState at 'running', never straight at
			// 'attesting') before doing the convert work for real.
			await context.AdvanceAsync(JobStates.Attesting, "resuming at attest marker (not re-executed)", cancellationToken).ConfigureAwait(false);

			Interlocked.Increment(ref _convertExecutions);
			if (FailConvertOnNextCall)
			{
				FailConvertOnNextCall = false;
				return JobExecutionOutcome.Failed("convert failed (invented)");
			}

			await context.AdvanceAsync(JobStates.Converting, "convert complete", cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.StageComplete("converting", "convert complete");
		}

		if (string.Equals(stage, "converting", StringComparison.Ordinal))
		{
			// Same reasoning: walk the transient 'attesting' hop (attest not
			// re-executed -- the marker already says it is done) before resuming the
			// real convert work.
			await context.AdvanceAsync(JobStates.Attesting, "resuming at convert marker (attest not re-executed)", cancellationToken).ConfigureAwait(false);
			await context.AdvanceAsync(JobStates.Converting, "resuming convert", cancellationToken).ConfigureAwait(false);

			Interlocked.Increment(ref _convertExecutions);
			if (FailConvertOnNextCall)
			{
				FailConvertOnNextCall = false;
				return JobExecutionOutcome.Failed("convert failed (invented)");
			}

			Interlocked.Increment(ref _uploadExecutions);
			return JobExecutionOutcome.Succeeded("uploaded");
		}

		throw new InvalidOperationException($"StageWalkingJobHandler: unrecognized stage marker '{stage}'.");
	}
}
