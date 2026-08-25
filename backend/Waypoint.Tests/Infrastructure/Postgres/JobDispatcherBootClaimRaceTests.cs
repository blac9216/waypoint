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
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #654 regression test: on a fresh stack, the download runner's first
/// <see cref="JobQueueRepository.ClaimJobAsync"/> can race migration/grant
/// application and hit a genuine <c>42501: permission denied for table jobs</c> --
/// reproduced here for real (not a fake exception) by connecting the dispatcher's
/// repository as the actual least-privilege <c>waypoint_download_runner</c> role
/// with its <c>jobs</c> table grant temporarily revoked, then granting it back mid-run
/// to simulate the backend's migrator finishing. Before the fix, this was already
/// self-healing (the dispatcher's claim loop retries on any exception) but logged at
/// Error every cycle; this test's job is to prove recovery happens with zero operator
/// action and within the dedicated boot-race backoff, not to prove logging levels
/// (those are exercised by inspecting <see cref="CapturingLogger{T}"/> as a secondary
/// assertion).
/// </summary>
[Collection("Postgres")]
public sealed class JobDispatcherBootClaimRaceTests : IAsyncLifetime
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _ownerRepository = null!;
	private JobEventPublisher _events = null!;
	private string _downloadRunnerConnectionString = string.Empty;

	public JobDispatcherBootClaimRaceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_ownerRepository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, new Waypoint.Core.Logging.InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);

		// Same fixed test-role password convention as RunnerRoleGrantDriftTests:
		// PostgresFixture.CreateRunnerRolesAsync provisions both roles with "waypoint_test".
		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// Simulates the exact race #654 describes: the download runner's connection has
	/// no SELECT/UPDATE grant on <c>jobs</c> yet (as if migration 0025 has not applied),
	/// so its first claim attempt hits a genuine 42501. Granting the missing
	/// privileges mid-test simulates the backend's migrator finishing shortly after.
	/// The dispatcher must recover and claim the seeded job on its own, without a
	/// restart, within the dedicated boot-race backoff -- no operator action.
	/// </summary>
	[Fact]
	public async Task FirstClaimRacesGrants_RecoversOnceGrantsLandWithoutOperatorAction()
	{
		await RevokeJobsGrantAsync();

		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded("ok")));

		JobQueueRepository runnerRepository = new(_downloadRunnerConnectionString, NullLogger<JobQueueRepository>.Instance);
		CapturingLogger<JobDispatcherHostedService> dispatcherLogger = new();

		JobDispatcherHostedService dispatcher = new(
			runnerRepository, _ownerRepository, _events, new JobHandlerRegistry([handler]),
			Options.Create(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }),
			dispatcherLogger);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			// Give the dispatcher a couple of boot-race retry cycles to actually hit
			// and log the 42501 before the grant lands -- otherwise the test could
			// pass even if the gate/backoff path were never exercised.
			await Task.Delay(TimeSpan.FromMilliseconds(800));

			await GrantJobsGrantAsync();

			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Done);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(JobStates.Done, await GetJobStateAsync(jobId));
	}

	/// <summary>
	/// A 42501 on <c>jobs</c> that never resolves (grants never applied) must still be
	/// retried indefinitely -- the boot-race gate is a quieter log/backoff path, not a
	/// giving-up path. The dispatcher must remain alive and keep retrying rather than
	/// crash the hosted service.
	/// </summary>
	[Fact]
	public async Task FirstClaimRacesGrants_KeepsRetryingWhileGrantsRemainMissing()
	{
		await RevokeJobsGrantAsync();

		Guid jobId = await SeedQueuedJobAsync("download");
		FakeJobHandler handler = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded("ok")));

		JobQueueRepository runnerRepository = new(_downloadRunnerConnectionString, NullLogger<JobQueueRepository>.Instance);
		CapturingLogger<JobDispatcherHostedService> dispatcherLogger = new();

		JobDispatcherHostedService dispatcher = new(
			runnerRepository, _ownerRepository, _events, new JobHandlerRegistry([handler]),
			Options.Create(new JobEngineOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50) }),
			dispatcherLogger);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(2));

			// Still queued -- the dispatcher never got past 42501 -- but the job did
			// not get lost or the dispatcher crash; it is still trying.
			Assert.Equal(JobStates.Queued, await GetJobStateAsync(jobId));

			await GrantJobsGrantAsync();
			await PollUntilAsync(() => GetJobStateAsync(jobId), state => state == JobStates.Done);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	private async Task RevokeJobsGrantAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand revoke = new("REVOKE SELECT, INSERT, UPDATE ON jobs FROM waypoint_download_runner", connection);
		await revoke.ExecuteNonQueryAsync();
	}

	private async Task GrantJobsGrantAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand grant = new("GRANT SELECT, INSERT, UPDATE ON jobs TO waypoint_download_runner", connection);
		await grant.ExecuteNonQueryAsync();
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
