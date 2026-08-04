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
/// End-to-end proof that <see cref="JobDispatcherHostedService"/> actually wires the
/// pieces <see cref="JobQueueRepositoryClaimTests"/>, <see cref="JobLeaseRecoveryTests"/>,
/// and <see cref="RunFanOutPauseAbortTests"/> prove individually into one working loop,
/// against a real PostgreSQL 16 container: claim -&gt; execute -&gt; complete, the
/// no-handler and handler-exception failure paths, heartbeat keeping a longer job's
/// lease alive, a paused run's job never executing, and abort cooperatively cancelling
/// in-flight work.
/// </summary>
[Collection("Postgres")]
public sealed class JobDispatcherHostedServiceTests : IAsyncLifetime
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;
	private CapturingLogger<JobDispatcherHostedService> _dispatcherLogger = null!;

	public JobDispatcherHostedServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, NullLogger<JobEventPublisher>.Instance);
		_dispatcherLogger = new CapturingLogger<JobDispatcherHostedService>();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private JobDispatcherHostedService CreateDispatcher(JobEngineOptions? options, params IJobHandler[] handlers)
	{
		JobEngineOptions effective = options ?? new JobEngineOptions { Enabled = true };
		return new JobDispatcherHostedService(
			_repository, _events, new JobHandlerRegistry(handlers), Options.Create(effective), _dispatcherLogger);
	}

	[Fact]
	public async Task SuccessfulJob_IsClaimedExecutedAndCompleted()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded("ok")));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "done");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("done", await GetJobStateAsync(jobId));
	}

	[Fact]
	public async Task NoHandlerRegistered_JobFailsWithAClearNote()
	{
		Guid jobId = await SeedQueuedJobAsync("catalog-index");

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) });

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "failed");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		string? note = await GetJobNoteAsync(jobId);
		Assert.Contains("No handler registered", note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HandlerThrows_JobFailsRatherThanCrashingTheDispatcher()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => throw new InvalidOperationException("boom"));

		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "failed");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		string? note = await GetJobNoteAsync(jobId);
		Assert.Contains("boom", note, StringComparison.Ordinal);
	}

	/// <summary>A job that outlives its lease is kept alive by the heartbeat loop rather than being clawed back by recovery mid-execution.</summary>
	[Fact]
	public async Task LongRunningJob_HeartbeatKeepsItAlive_CompletesNormally()
	{
		Guid jobId = await SeedQueuedJobAsync("download");
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			await release.Task.WaitAsync(ct);
			return JobExecutionOutcome.Succeeded();
		});

		JobEngineOptions options = new()
		{
			Enabled = true,
			PollInterval = TimeSpan.FromMilliseconds(50),
			LeaseDuration = TimeSpan.FromSeconds(1),
			HeartbeatInterval = TimeSpan.FromMilliseconds(200)
		};
		JobDispatcherHostedService dispatcher = CreateDispatcher(options, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "running");

			// Outlive the 1s lease by a comfortable margin, relying entirely on the
			// 200ms heartbeat cadence to keep it claimed.
			await Task.Delay(TimeSpan.FromSeconds(3));
			Assert.Equal("running", await GetJobStateAsync(jobId));

			release.SetResult();
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == "done");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task PausedRun_ReleasesClaimsUntilResume_ThenExecutes()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		Assert.True(await _repository.PauseRunAsync(runId, CancellationToken.None));
		int calls = 0;
		FakeJobHandler handler = new("download", (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(JobExecutionOutcome.Succeeded()); });
		JobDispatcherHostedService dispatcher = CreateDispatcher(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25) }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(250));
			Assert.Equal(0, Volatile.Read(ref calls));
			Assert.Equal(JobStates.Queued, await GetJobStateAsync(jobId));
			Assert.True(await dispatcher.ResumeRunAsync(runId, CancellationToken.None));
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Done);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task AbortRun_CancelsLocallyRunningHandlerAndJob()
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);
		Guid jobId = Assert.Single(await _repository.FanOutJobsAsync(runId, [new JobSpec("download", 1)], "tester", CancellationToken.None));
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeJobHandler handler = new("download", async (_, ct) =>
		{
			entered.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return JobExecutionOutcome.Succeeded();
		});
		JobDispatcherHostedService dispatcher = CreateDispatcher(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25) }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(PollTimeout);
			await dispatcher.AbortRunAsync(runId, CancellationToken.None);
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Cancelled);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task ThirdAuthFailure_BlocksRemainingQueueAndEmitsOperatorEvents()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("download", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalAuthFailureAsync(runId, credentialId);
		await SeedTerminalAuthFailureAsync(runId, credentialId);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec("download", 1, CredentialId: credentialId), new JobSpec("download", 2, CredentialId: credentialId)],
			"tester",
			CancellationToken.None);
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.AuthFailed("rejected")));
		JobDispatcherHostedService dispatcher = CreateDispatcher(
			new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(25), MaxConcurrency = 1, ConsecutiveAuthFailureThreshold = 3 }, handler);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilAsync(() => GetRunBlockedAsync(runId), blocked => blocked);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(JobStates.AuthFailed, await GetJobStateAsync(jobIds[0]));
		Assert.Equal(JobStates.Blocked, await GetJobStateAsync(jobIds[1]));
		Assert.Contains(_dispatcherLogger.Entries, entry => entry.Message.Contains("queue halted", StringComparison.Ordinal));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.QueueState, runId));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.SystemNotice, null));
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO credentials (name, credential_type) VALUES ($1, 'service') RETURNING id", connection);
		command.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task SeedTerminalAuthFailureAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id, finished_at) VALUES ($1, 'download', 1, 'auth-failed', $2, now())", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(credentialId);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> EventTypeExistsAsync(string eventType, Guid? runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT EXISTS (SELECT 1 FROM job_events WHERE event_type = $1 AND run_id IS NOT DISTINCT FROM $2)", connection);
		command.Parameters.AddWithValue(eventType);
		command.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedQueuedJobAsync(string jobType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ($1, 1, 'queued') RETURNING id", connection);
		insert.Parameters.AddWithValue(jobType);
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

	private async Task<string?> GetJobNoteAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT note FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (string?)await command.ExecuteScalarAsync();
	}

	private async Task<bool> GetRunBlockedAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT blocked FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(runId);
		return (bool)(await command.ExecuteScalarAsync())!;
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
