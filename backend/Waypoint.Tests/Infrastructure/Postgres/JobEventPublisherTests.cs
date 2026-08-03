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

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves <see cref="JobEventPublisher"/>'s two documented contracts against a real
/// PostgreSQL 16 container: an ordinary emit is a genuinely short, standalone write
/// (never inside a caller's transaction), and a write stuck behind
/// <c>trg_job_events_assign_seq</c>'s ordering lock (issue #117) fails on its own
/// short, explicitly configured budget rather than inheriting Npgsql's 30s default
/// (issue #108's lesson applied here) -- and does so without throwing out of
/// <see cref="IJobEventPublisher.EmitAsync"/>, per that interface's "best-effort,
/// never throws" contract.
/// </summary>
[Collection("Postgres")]
public sealed class JobEventPublisherTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	public JobEventPublisherTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task EmitAsync_OrdinaryCase_WritesTheRow()
	{
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, NullLogger<JobEventPublisher>.Instance);
		Guid jobId = await SeedJobAsync();

		await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, """{"line":"hello"}""", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT event_type, payload ->> 'line' FROM job_events WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(JobEventTypes.JobLog, reader.GetString(0));
		Assert.Equal("hello", reader.GetString(1));
	}

	/// <summary>
	/// The command-timeout proof: another session holds
	/// <c>trg_job_events_assign_seq</c>'s advisory lock open (an uncommitted emit,
	/// exactly the "emit-and-then-keep-working" anti-pattern the schema's doc comments
	/// warn against) well past this publisher's configured budget. The emit must give up
	/// close to that budget -- nowhere near Npgsql's 30s default -- and must not throw
	/// out of <see cref="IJobEventPublisher.EmitAsync"/> (it logs and swallows).
	/// </summary>
	[Fact]
	public async Task EmitAsync_BlockedByLockContention_GivesUpNearItsOwnBudget_NotTheThirtySecondDefault()
	{
		const int budgetSeconds = 2;
		JobEventPublisher publisher = new(_fixture.ConnectionString, budgetSeconds, NullLogger<JobEventPublisher>.Instance);
		Guid jobId = await SeedJobAsync();

		await using NpgsqlConnection lockHolder = new(_fixture.ConnectionString);
		await lockHolder.OpenAsync();
		await using NpgsqlTransaction lockTransaction = await lockHolder.BeginTransactionAsync();

		await using (NpgsqlCommand takeLock = new("SELECT pg_advisory_xact_lock(875190002)", lockHolder, lockTransaction))
		{
			await takeLock.ExecuteNonQueryAsync();
		}

		try
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, "{}", CancellationToken.None);
			stopwatch.Stop();

			// Generous slack around the budget (Npgsql's own timeout granularity, plus
			// this process's scheduling jitter) while still being nowhere near the 30s
			// default -- the whole point of the assertion.
			Assert.True(
				stopwatch.Elapsed < TimeSpan.FromSeconds(budgetSeconds + 5),
				$"Emit blocked by lock contention took {stopwatch.Elapsed}, expected to give up near its {budgetSeconds}s budget, well under Npgsql's 30s default.");
			Assert.True(
				stopwatch.Elapsed >= TimeSpan.FromSeconds(budgetSeconds - 1),
				$"Emit returned after only {stopwatch.Elapsed}, suspiciously fast for a command that should have been blocked on the lock for ~{budgetSeconds}s.");
		}
		finally
		{
			await lockTransaction.RollbackAsync();
		}

		// No row: the blocked INSERT never completed, and EmitAsync must not have thrown.
		await using NpgsqlConnection verifyConnection = new(_fixture.ConnectionString);
		await verifyConnection.OpenAsync();
		await using NpgsqlCommand countCommand = new("SELECT count(*) FROM job_events WHERE job_id = $1", verifyConnection);
		countCommand.Parameters.AddWithValue(jobId);
		Assert.Equal(0L, (long)(await countCommand.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task EmitAsync_AfterLockIsReleased_SucceedsNormally()
	{
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, NullLogger<JobEventPublisher>.Instance);
		Guid jobId = await SeedJobAsync();

		await using (NpgsqlConnection lockHolder = new(_fixture.ConnectionString))
		{
			await lockHolder.OpenAsync();
			await using NpgsqlTransaction lockTransaction = await lockHolder.BeginTransactionAsync();
			await using (NpgsqlCommand takeLock = new("SELECT pg_advisory_xact_lock(875190002)", lockHolder, lockTransaction))
			{
				await takeLock.ExecuteNonQueryAsync();
			}

			await lockTransaction.CommitAsync();
		}

		await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, "{}", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM job_events WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	private async Task<Guid> SeedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('download', 1, 'queued') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}
}
