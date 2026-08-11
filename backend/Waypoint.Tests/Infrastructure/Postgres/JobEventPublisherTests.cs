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
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Runner.Jobs;
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
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
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
		JobEventPublisher publisher = new(_fixture.ConnectionString, budgetSeconds, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
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
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
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


	/// <summary>
	/// #128's own-connection acceptance criterion, which round 1 correctly pointed out had
	/// no test of its own shape. The two fixtures above that hold a transaction open are
	/// both *designed to block*, so neither could show an emit COMPLETING beside an open
	/// caller transaction -- and "did not deadlock" is a much weaker claim than "ran
	/// independently".
	///
	/// The caller here holds a genuine row lock on a committed <c>jobs</c> row for the whole
	/// emit, and then rolls back. Two things have to hold, and the second is the load-
	/// bearing one: the emit completes rather than waiting on the caller (so it is not on
	/// the caller's connection), and its row survives a rollback that undoes the caller's
	/// own write (so it was not in the caller's transaction). A publisher that quietly
	/// enlisted in an ambient transaction would pass the first and fail the second.
	///
	/// Emitted as <c>system.notice</c> on purpose: it is the one event type
	/// <c>job_events_scope_check</c> requires to carry neither a job_id nor a run_id, so
	/// nothing here takes a foreign-key lock on the row the caller is holding. That would
	/// block for reasons that have nothing to do with connection ownership and would make
	/// this test prove the opposite of what it says.
	/// </summary>
	[Fact]
	public async Task EmitAsync_WhileTheCallerHoldsAnOpenTransaction_CompletesAndSurvivesTheCallersRollback()
	{
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
		Guid jobId = await SeedJobAsync();
		string marker = $"own-connection-{Guid.NewGuid():N}";

		await using NpgsqlConnection caller = new(_fixture.ConnectionString);
		await caller.OpenAsync();
		await using NpgsqlTransaction callerTransaction = await caller.BeginTransactionAsync();

		// A real, uncommitted row lock held across the emit.
		await using (NpgsqlCommand update = new("UPDATE jobs SET note = $2 WHERE id = $1", caller, callerTransaction))
		{
			update.Parameters.AddWithValue(jobId);
			update.Parameters.AddWithValue("held by the caller");
			Assert.Equal(1, await update.ExecuteNonQueryAsync());
		}

		Task emit = publisher.EmitAsync(
			JobEventTypes.SystemNotice, null, null, $$"""{"marker":"{{marker}}"}""", CancellationToken.None);

		Task finished = await Task.WhenAny(emit, Task.Delay(TimeSpan.FromSeconds(20)));
		Assert.True(
			ReferenceEquals(finished, emit),
			"EmitAsync did not complete within 20s while the caller held an open transaction -- it is not running on its own connection.");
		await emit;

		await callerTransaction.RollbackAsync();

		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();

		// The caller's write is gone...
		await using (NpgsqlCommand note = new("SELECT note FROM jobs WHERE id = $1", verify))
		{
			note.Parameters.AddWithValue(jobId);
			Assert.Null(await note.ExecuteScalarAsync() as string);
		}

		// ...and the event is not, which is the half that distinguishes "own connection"
		// from "happened not to block this time".
		await using (NpgsqlCommand count = new("SELECT count(*) FROM job_events WHERE payload ->> 'marker' = $1", verify))
		{
			count.Parameters.AddWithValue(marker);
			Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
		}
	}

	/// <summary>
	/// Pins the measurement the publisher's structure rests on, so it cannot rot into
	/// folklore (the same reason
	/// <c>NullLoggerShortCircuitsTheGeneratedBody_WhichIsWhyThisFileExists</c> exists).
	///
	/// <see cref="JobEventPublisher"/> decides whether it is allowed to name lock
	/// contention from *which operation* failed rather than from the exception, because a
	/// connect that stalls and a command that times out are indistinguishable as exception
	/// objects. This asserts exactly that: the same type, the same
	/// <c>IsTransient</c> value, the same inner exception type, from a socket that never
	/// answers and from a real command timeout against this fixture's live server. If a
	/// future Npgsql makes them distinguishable, this fails and the comment gets re-read.
	/// </summary>
	[Fact]
	public async Task AStalledConnectAndACommandTimeout_AreIndistinguishableAsExceptions()
	{
		using TcpListener silent = new(IPAddress.Loopback, 0);
		silent.Start();
		int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;

		NpgsqlException connectFailure;
		try
		{
			await using NpgsqlConnection stalled = new(
				$"Host=127.0.0.1;Port={silentPort};Database=waypoint_test;Username=waypoint_test;Password=waypoint_test;Timeout=2");
			connectFailure = await Assert.ThrowsAsync<NpgsqlException>(() => stalled.OpenAsync());
		}
		finally
		{
			silent.Stop();
		}

		await using NpgsqlConnection live = new(_fixture.ConnectionString);
		await live.OpenAsync();
		await using NpgsqlCommand slow = new("SELECT pg_sleep(30)", live) { CommandTimeout = 1 };
		NpgsqlException commandTimeout = await Assert.ThrowsAsync<NpgsqlException>(() => slow.ExecuteNonQueryAsync());

		Assert.Equal(commandTimeout.GetType(), connectFailure.GetType());
		Assert.Equal(commandTimeout.IsTransient, connectFailure.IsTransient);
		Assert.Equal(commandTimeout.InnerException?.GetType(), connectFailure.InnerException?.GetType());
		Assert.IsType<TimeoutException>(connectFailure.InnerException);
		Assert.True(connectFailure.IsTransient, "Npgsql no longer marks an unreachable server transient; the comment in JobEventPublisher needs re-measuring.");
	}

	/// <summary>
	/// Cancellation is the one failure an emit must NOT swallow. Everything else is
	/// best-effort observability and is logged and dropped, but a cancelled token means
	/// the host is shutting down or the caller gave up, and turning that into a silent
	/// success would make a stopping dispatcher look like it completed its work.
	/// </summary>
	[Fact]
	public async Task EmitAsync_WithAnAlreadyCancelledToken_Rethrows()
	{
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => publisher.EmitAsync(JobEventTypes.JobLog, null, null, "{}", cts.Token));
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
