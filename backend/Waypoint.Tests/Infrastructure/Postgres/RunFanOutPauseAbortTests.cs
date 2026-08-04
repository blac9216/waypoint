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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the ADR-0008 run controls at the repository layer: fan-out creates one job
/// per spec under the run, pause is visible to a claimant before it executes anything,
/// and abort marks queued jobs terminal immediately while reporting in-flight jobs for
/// cooperative cancellation.
/// </summary>
[Collection("Postgres")]
public sealed class RunFanOutPauseAbortTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private CapturingLogger<JobQueueRepository> _logger = null!;

	public RunFanOutPauseAbortTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_logger = new CapturingLogger<JobQueueRepository>();
		_repository = new JobQueueRepository(_fixture.ConnectionString, _logger);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task FanOutJobsAsync_CreatesOneJobPerSpec_AndMarksTheRunRunning()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", credentialId: null, initiatedBy: "tester", CancellationToken.None);

		JobSpec[] specs =
		[
			new JobSpec("download", 1, TargetName: "artifact-a"),
			new JobSpec("download", 1, TargetName: "artifact-b"),
			new JobSpec("download", 1, TargetName: "artifact-c")
		];

		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, specs, "tester", CancellationToken.None);
		Assert.Equal(3, jobIds.Count);
		Assert.Equal(3, jobIds.Distinct().Count());
		Assert.Contains(_logger.EntriesAt(LogLevel.Information), entry => entry.Message.Contains("Fanned out 3", StringComparison.Ordinal));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand countJobs = new("SELECT count(*) FROM jobs WHERE run_id = $1 AND state = 'queued'", connection))
		{
			countJobs.Parameters.AddWithValue(runId);
			Assert.Equal(3L, (long)(await countJobs.ExecuteScalarAsync())!);
		}

		await using NpgsqlCommand runState = new("SELECT state FROM runs WHERE id = $1", connection);
		runState.Parameters.AddWithValue(runId);
		Assert.Equal("running", (string)(await runState.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task FanOutJobsAsync_UnknownRun_Throws()
	{
		JobSpec[] specs = [new JobSpec("download", 1)];
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _repository.FanOutJobsAsync(Guid.NewGuid(), specs, null, CancellationToken.None));
	}

	/// <summary>
	/// "Pause stops dispatch, in-flight finish": this is the claim-then-release contract
	/// the dispatcher relies on (see <c>JobDispatcherHostedService</c>'s doc comment) --
	/// pausing never changes what <see cref="JobQueueRepository.ClaimJobAsync"/> itself
	/// claims, it only tells the caller the claim must be given back.
	/// </summary>
	[Fact]
	public async Task PausedRun_IsVisibleToAClaimantBeforeExecution()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None);

		Assert.True(await _repository.PauseRunAsync(runId, CancellationToken.None));

		ClaimedJob? claimed = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(runId, claimed!.RunId);

		RunQueueState? state = await _repository.GetRunQueueStateAsync(runId, CancellationToken.None);
		Assert.NotNull(state);
		Assert.True(state!.Paused);

		// The dispatcher's actual behavior on seeing this: release the claim.
		Assert.True(await _repository.ReleaseClaimAsync(claimed.Id, "worker-a", CancellationToken.None));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand verify = new("SELECT state, attempt_count FROM jobs WHERE id = $1", connection);
		verify.Parameters.AddWithValue(claimed.Id);
		await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("queued", reader.GetString(0));
		Assert.Equal(0, reader.GetInt32(1));

		Assert.True(await _repository.ResumeRunAsync(runId, CancellationToken.None));
		RunQueueState? resumed = await _repository.GetRunQueueStateAsync(runId, CancellationToken.None);
		Assert.False(resumed!.Paused);
	}

	[Fact]
	public async Task AbortRunAsync_CancelsQueuedJobs_AndReportsInFlightOnesSeparately()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		JobSpec[] specs =
		[
			new JobSpec("download", 1, TargetName: "a"),
			new JobSpec("download", 1, TargetName: "b"),
			new JobSpec("download", 1, TargetName: "c")
		];
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, specs, "tester", CancellationToken.None);

		// One of the three gets claimed (simulating "in flight"); the other two stay queued.
		ClaimedJob? inFlight = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(inFlight);

		AbortRunResult result = await _repository.AbortRunAsync(runId, CancellationToken.None);

		Assert.Equal(2, result.CancelledJobIds.Count);
		Assert.DoesNotContain(inFlight!.Id, result.CancelledJobIds);
		Assert.Equal([inFlight.Id], result.InFlightJobIds);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand runState = new("SELECT state FROM runs WHERE id = $1", connection))
		{
			runState.Parameters.AddWithValue(runId);
			Assert.Equal("aborted", (string)(await runState.ExecuteScalarAsync())!);
		}

		foreach (Guid jobId in jobIds.Where(id => id != inFlight.Id))
		{
			await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection);
			jobState.Parameters.AddWithValue(jobId);
			Assert.Equal("cancelled", (string)(await jobState.ExecuteScalarAsync())!);
		}

		// The in-flight job is left alone by the DB write -- SQL cannot stop work
		// already executing in-process; it is still 'running' and still owned.
		await using NpgsqlCommand stillRunning = new("SELECT state FROM jobs WHERE id = $1", connection);
		stillRunning.Parameters.AddWithValue(inFlight.Id);
		Assert.Equal("running", (string)(await stillRunning.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task AbortThenExpiredLease_NeverExecutesOnASecondDispatcher()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		ClaimedJob claimed = Assert.IsType<ClaimedJob>(await _repository.ClaimJobAsync("dead-worker", TimeSpan.FromMilliseconds(100), CancellationToken.None));
		Assert.Equal(jobId, claimed.Id);

		AbortRunResult abort = await _repository.AbortRunAsync(runId, CancellationToken.None);
		Assert.Contains(jobId, abort.InFlightJobIds);
		await Task.Delay(TimeSpan.FromMilliseconds(200));
		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(10, CancellationToken.None);
		Assert.Contains(recovered, job => job.Id == jobId && job.NewState == JobStates.Cancelled);

		int calls = 0;
		FakeJobHandler handler = new("download", (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(JobExecutionOutcome.Succeeded()); });
		JobEventPublisher events = new(_fixture.ConnectionString, 5, NullLogger<JobEventPublisher>.Instance);
		JobDispatcherHostedService dispatcher = new(
			_repository,
			events,
			new JobHandlerRegistry([handler]),
			Options.Create(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25) }),
			new CapturingLogger<JobDispatcherHostedService>());
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(250));
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(0, calls);
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		Assert.Equal(JobStates.Cancelled, (string)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task AbortRunAsync_UnknownRun_IsANoOp()
	{
		AbortRunResult result = await _repository.AbortRunAsync(Guid.NewGuid(), CancellationToken.None);
		Assert.Empty(result.CancelledJobIds);
		Assert.Empty(result.InFlightJobIds);
	}
}
