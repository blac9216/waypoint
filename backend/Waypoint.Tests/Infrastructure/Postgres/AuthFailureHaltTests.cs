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
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the ADR-0008 consecutive-auth-failure queue halt (default threshold 3): the
/// Nth-in-a-row <c>auth-failed</c> job against one credential blocks that credential's
/// still-queued jobs (and every run that had one), while a run of failures broken up by
/// a success never trips it -- the boundary that shows "consecutive" is actually
/// enforced, not just "N total".
/// </summary>
[Collection("Postgres")]
public sealed class AuthFailureHaltTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public AuthFailureHaltTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task ThreeConsecutiveAuthFailures_BlocksTheRunAndItsQueuedJobs()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);

		// stillQueuedJobId is seeded oldest, on purpose: "3 most recent" (created_at
		// DESC) must land on exactly the two auth-failed history rows plus the job about
		// to become the third -- if the still-queued row fell inside that recency
		// window instead, it would break the "consecutive" condition itself and the test
		// would pass for the wrong reason (a queued job isn't 'auth-failed' either).
		Guid stillQueuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		Guid thirdFailureJobId = await SeedQueuedJobAsync(runId, credentialId);

		await TransitionToAuthFailedAsync(thirdFailureJobId);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);

		Assert.Contains(runId, result.BlockedRunIds);
		Assert.Contains(stillQueuedJobId, result.BlockedJobIds);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand runBlocked = new("SELECT blocked, blocked_reason FROM runs WHERE id = $1", connection))
		{
			runBlocked.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await runBlocked.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.True(reader.GetBoolean(0));
			Assert.False(reader.IsDBNull(1));
		}

		await using NpgsqlCommand jobBlocked = new("SELECT state FROM jobs WHERE id = $1", connection);
		jobBlocked.Parameters.AddWithValue(stillQueuedJobId);
		Assert.Equal("blocked", (string)(await jobBlocked.ExecuteScalarAsync())!);
	}

	/// <summary>The boundary that proves this can fail: two failures, one success, then
	/// a third failure is NOT three *consecutive* failures -- the halt must not trip.</summary>
	[Fact]
	public async Task FailuresInterruptedBySuccess_DoNotTripTheHalt()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);

		// Same "seed the queued job oldest" reasoning as the trip-the-halt test above --
		// here the interrupting 'done' success is what should break the "recent 3" run,
		// not an incidentally-included queued row.
		Guid queuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.Done);
		Guid thirdJobId = await SeedQueuedJobAsync(runId, credentialId);

		await TransitionToAuthFailedAsync(thirdJobId);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);

		Assert.Empty(result.BlockedRunIds);
		Assert.Empty(result.BlockedJobIds);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection);
		jobState.Parameters.AddWithValue(queuedJobId);
		Assert.Equal("queued", (string)(await jobState.ExecuteScalarAsync())!);
	}

	/// <summary>Fewer than the threshold's worth of history for a credential can never trip the halt, no matter their state.</summary>
	[Fact]
	public async Task FewerJobsThanThreshold_NeverTrips()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Empty(result.BlockedRunIds);
		Assert.Empty(result.BlockedJobIds);
	}

	/// <summary>A second, redundant call after the halt already tripped finds nothing left to block -- idempotent under a duplicate/concurrent caller.</summary>
	[Fact]
	public async Task CallingTwice_TheSecondCallIsANoOp()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid queuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		Guid thirdJobId = await SeedQueuedJobAsync(runId, credentialId);
		await TransitionToAuthFailedAsync(thirdJobId);

		AuthFailureHaltResult first = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Contains(queuedJobId, first.BlockedJobIds);

		AuthFailureHaltResult second = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Empty(second.BlockedRunIds);
		Assert.Empty(second.BlockedJobIds);
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'service') RETURNING id", connection);
		insert.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task SeedTerminalJobAsync(Guid runId, Guid credentialId, string terminalState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, credential_id, finished_at)
			VALUES ($1, 'scan', 1, $2, $3, now())
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(terminalState);
		insert.Parameters.AddWithValue(credentialId);
		await insert.ExecuteNonQueryAsync();

		// created_at ordering matters for "N most recent" -- force a tiny, deterministic gap.
		await Task.Delay(TimeSpan.FromMilliseconds(5));
	}

	private async Task<Guid> SeedQueuedJobAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id) VALUES ($1, 'scan', 1, 'queued', $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(credentialId);
		Guid id = (Guid)(await insert.ExecuteScalarAsync())!;
		await Task.Delay(TimeSpan.FromMilliseconds(5));
		return id;
	}

	/// <summary>
	/// Directly claims <paramref name="jobId"/> by id (bypassing
	/// <see cref="JobQueueRepository.ClaimJobAsync"/>'s global "next in priority order"
	/// pick, which would otherwise contend with the other queued jobs these tests seed
	/// alongside it) and then advances it to <c>auth-failed</c> through the real
	/// repository method under test.
	/// </summary>
	private async Task TransitionToAuthFailedAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand claim = new(
			"""
			UPDATE jobs SET
				state = 'running', claimed_by = 'worker-a', claimed_at = now(),
				lease_expires_at = now() + interval '5 minutes', heartbeat_at = now(), attempt_count = attempt_count + 1
			WHERE id = $1
			""", connection);
		claim.Parameters.AddWithValue(jobId);
		await claim.ExecuteNonQueryAsync();

		bool advanced = await _repository.AdvanceStateAsync(
			jobId, "worker-a", JobStates.Running, JobStates.AuthFailed, "credential rejected", clearLease: true, CancellationToken.None);
		Assert.True(advanced);
	}
}
