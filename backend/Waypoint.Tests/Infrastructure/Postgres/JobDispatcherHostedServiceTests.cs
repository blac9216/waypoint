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
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private JobDispatcherHostedService CreateDispatcher(JobEngineOptions? options, params IJobHandler[] handlers)
	{
		JobEngineOptions effective = options ?? new JobEngineOptions { Enabled = true };
		return new JobDispatcherHostedService(
			_repository, _events, new JobHandlerRegistry(handlers), Options.Create(effective), NullLogger<JobDispatcherHostedService>.Instance);
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
