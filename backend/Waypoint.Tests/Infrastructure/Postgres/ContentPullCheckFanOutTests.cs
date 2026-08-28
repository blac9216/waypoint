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

using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1016 (epic #726), owner decision 2026-08-28: real-Postgres coverage of the
/// two new pieces the content-check fan-out/reconcile architecture adds to the queue
/// layer -- <see cref="JobQueueRepository.FanOutAdditionalJobsAsync"/> (adding a second
/// wave of jobs to an ALREADY-RUNNING run, the counterpart to
/// <see cref="IJobControlRepository.FanOutJobsAsync"/>'s pending-only guard) and
/// <see cref="ContentPullCheckFanOutRepository"/> (migration 0073's linkage/result
/// tables). Also proves the capacity/admission claim path treats <c>content-check</c>
/// exactly like any other queued job (concurrent claimability), and that cancelling the
/// owning run absorbs outstanding content-check jobs through the SAME
/// <see cref="IJobControlRepository.AbortRunAsync"/> mechanism every other job type
/// already uses -- no new cancellation code exists for this job type, which is the
/// point: reuse, not a parallel mechanism.
/// </summary>
[Collection("Postgres")]
public sealed class ContentPullCheckFanOutTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private ContentPullCheckFanOutRepository _checkFanOut = null!;

	public ContentPullCheckFanOutTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_checkFanOut = new ContentPullCheckFanOutRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>Seeds a real 'content-pull' run with its single job already running (mirrors what the dispatcher would have done by the time ContentPullJobHandler calls FanOutAdditionalJobsAsync mid-execution).</summary>
	private async Task<(Guid RunId, Guid ContentPullJobId)> SeedRunningContentPullAsync()
	{
		Guid runId = await _jobs.CreateRunAsync("content-pull", "{}", credentialId: null, initiatedBy: "admin@example.internal", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, [new JobSpec("content-pull", Priority: 1, TargetName: "compliance-content")], createdBy: "admin@example.internal", CancellationToken.None);
		Guid contentPullJobId = jobIds[0];

		// Claim it so the run/job are both genuinely 'running', matching the real
		// window ContentPullJobHandler.ExecuteAsync runs inside.
		ClaimedJob? claimed = await _jobs.ClaimJobAsync("worker-1", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(contentPullJobId, claimed!.Id);

		return (runId, contentPullJobId);
	}

	[Fact]
	public async Task FanOutAdditionalJobsAsync_OnRunningRun_InsertsQueuedJobs()
	{
		(Guid runId, _) = await SeedRunningContentPullAsync();

		JobSpec[] specs = [
			new JobSpec("content-check", Priority: 1, TargetName: "compliance-content-check"),
			new JobSpec("content-check", Priority: 1, TargetName: "compliance-content-check"),
		];
		IReadOnlyList<Guid> checkJobIds = await _jobs.FanOutAdditionalJobsAsync(runId, specs, "admin@example.internal", CancellationToken.None);

		Assert.Equal(2, checkJobIds.Count);
		Assert.Equal(2, checkJobIds.Distinct().Count());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state, job_type, run_id FROM jobs WHERE id = ANY($1)", connection);
		command.Parameters.AddWithValue(checkJobIds.ToArray());
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		int rowCount = 0;
		while (await reader.ReadAsync())
		{
			rowCount++;
			Assert.Equal("queued", reader.GetString(0));
			Assert.Equal("content-check", reader.GetString(1));
			Assert.Equal(runId, reader.GetGuid(2));
		}

		Assert.Equal(2, rowCount);
	}

	[Fact]
	public async Task FanOutAdditionalJobsAsync_RunNotRunning_Throws()
	{
		// A run still 'pending' (never fanned out at all) is not 'running' -- the guard
		// this method adds specifically to prevent adding jobs to a run that has not
		// started, already finished, or was aborted out from under the caller.
		Guid pendingRunId = await _jobs.CreateRunAsync("content-pull", "{}", credentialId: null, initiatedBy: "admin@example.internal", CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			_jobs.FanOutAdditionalJobsAsync(pendingRunId, [new JobSpec("content-check", 1)], "admin@example.internal", CancellationToken.None));
	}

	[Fact]
	public async Task FannedOutContentCheckJobs_AreConcurrentlyClaimable()
	{
		(Guid runId, _) = await SeedRunningContentPullAsync();
		JobSpec[] specs = [.. Enumerable.Range(0, 5).Select(_ => new JobSpec("content-check", Priority: 1, TargetName: "compliance-content-check"))];
		await _jobs.FanOutAdditionalJobsAsync(runId, specs, "admin@example.internal", CancellationToken.None);

		// The capacity/admission claim path (JobQueueRepository.ClaimJobAsync, the same
		// FOR UPDATE SKIP LOCKED statement every other job type uses) treats
		// content-check as an ordinary claimable job type -- structural proof of "N
		// check jobs exist and can be claimed concurrently", not a wall-clock timing
		// assertion (per the capacity tests' own idiom).
		Task<ClaimedJob?>[] claims = [.. Enumerable.Range(0, 5)
			.Select(i => _jobs.ClaimJobAsync($"worker-check-{i}", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None))];
		ClaimedJob?[] claimed = await Task.WhenAll(claims);

		Guid[] distinctIds = [.. claimed.Where(j => j is not null).Select(j => j!.Id).Distinct()];
		Assert.Equal(5, distinctIds.Length);
		Assert.All(claimed, job => Assert.Equal("content-check", job!.JobType));
	}

	[Fact]
	public async Task RecordFanOutAsync_ThenGetFanOutForCheckJobAsync_RoundTripsProfileDirectories()
	{
		(Guid runId, Guid contentPullJobId) = await SeedRunningContentPullAsync();
		IReadOnlyList<Guid> checkJobIds = await _jobs.FanOutAdditionalJobsAsync(runId, [new JobSpec("content-check", 1)], "admin@example.internal", CancellationToken.None);
		Guid checkJobId = checkJobIds[0];

		ContentCheckProfileDirectory[] chunk =
		[
			new("vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", "/content/vsphere/vcenter"),
			new("vsphere/8.0.3/v2r3-stig/inspec/baseline/esxi", "/content/vsphere/esxi"),
		];
		await _checkFanOut.RecordFanOutAsync(runId, contentPullJobId, checkJobId, "commitABC123", chunk, CancellationToken.None);

		ContentPullCheckFanOut? fanOut = await _checkFanOut.GetFanOutForCheckJobAsync(checkJobId, CancellationToken.None);
		Assert.NotNull(fanOut);
		Assert.Equal(runId, fanOut!.RunId);
		Assert.Equal(contentPullJobId, fanOut.ContentPullJobId);
		Assert.Equal("commitABC123", fanOut.SourceCommit);
		Assert.Equal(2, fanOut.ProfileDirectories.Count);
		Assert.Contains(fanOut.ProfileDirectories, p => p.ProfileKey == "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter" && p.ProfileDirectory == "/content/vsphere/vcenter");
	}

	[Fact]
	public async Task GetReconcileReadinessAsync_ReflectsRealJobStateTransitions()
	{
		(Guid runId, Guid contentPullJobId) = await SeedRunningContentPullAsync();
		IReadOnlyList<Guid> checkJobIds = await _jobs.FanOutAdditionalJobsAsync(
			runId, [new JobSpec("content-check", 1), new JobSpec("content-check", 1)], "admin@example.internal", CancellationToken.None);

		foreach (Guid checkJobId in checkJobIds)
		{
			await _checkFanOut.RecordFanOutAsync(runId, contentPullJobId, checkJobId, "commitReady", [new ContentCheckProfileDirectory("p", "/p")], CancellationToken.None);
		}

		ContentPullCheckReconcileReadiness notYet = await _checkFanOut.GetReconcileReadinessAsync(contentPullJobId, CancellationToken.None);
		Assert.False(notYet.AllTerminal);
		Assert.Equal(2, notYet.TotalCheckJobs);

		// Claim and terminalize both real check jobs through the real claim/advance path.
		ClaimedJob? first = await _jobs.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		ClaimedJob? second = await _jobs.ClaimJobAsync("worker-b", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		Assert.NotNull(first);
		Assert.NotNull(second);
		await _jobs.AdvanceStateAsync(first!.Id, "worker-a", "running", "done", note: null, clearLease: true, CancellationToken.None);
		await _jobs.AdvanceStateAsync(second!.Id, "worker-b", "running", "failed", note: "invented fixture failure", clearLease: true, CancellationToken.None);

		ContentPullCheckReconcileReadiness ready = await _checkFanOut.GetReconcileReadinessAsync(contentPullJobId, CancellationToken.None);
		Assert.True(ready.AllTerminal);
		Assert.Equal(2, ready.TotalCheckJobs);
		Assert.Equal(1, ready.FailedCheckJobs);
	}

	/// <summary>
	/// Cancellation proof (item 3 of the fan-out scope): cancelling the pull's owning
	/// run cancels outstanding content-check jobs through the SAME
	/// <see cref="IJobControlRepository.AbortRunAsync"/> path every other job type
	/// already uses -- content-check needed zero new cancellation code because it is an
	/// ordinary job on an ordinary run.
	/// </summary>
	[Fact]
	public async Task AbortRun_CancelsOutstandingContentCheckJobs_NoPoisonedState()
	{
		(Guid runId, _) = await SeedRunningContentPullAsync();
		await _jobs.FanOutAdditionalJobsAsync(
			runId, [new JobSpec("content-check", 1), new JobSpec("content-check", 1)], "admin@example.internal", CancellationToken.None);

		AbortRunResult result = await _jobs.AbortRunAsync(runId, CancellationToken.None);
		Assert.True(result.CancelledJobIds.Count >= 2, "expected both still-queued content-check jobs to be cancelled by the run abort.");

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM jobs WHERE run_id = $1 AND job_type = 'content-check'", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			Assert.Equal("cancelled", reader.GetString(0));
		}

		// Re-claiming after abort must find nothing left claimable for this run --
		// no poisoned/stuck-queued row survives the abort.
		ClaimedJob? afterAbort = await _jobs.ClaimJobAsync("worker-after-abort", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		Assert.Null(afterAbort);
	}
}
