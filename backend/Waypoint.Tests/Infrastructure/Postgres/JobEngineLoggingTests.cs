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

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Closes the <c>[LoggerMessage]</c> coverage gap PR #126 recorded as inherent
/// "dilution", and asserts the log lines themselves while doing it.
///
/// The gap is real and it is closable. A generated log method opens with
/// <c>if (!logger.IsEnabled(level)) return;</c>; <c>NullLogger</c> answers false, so on
/// every existing test the generated body stops at line one. Swapping in
/// <see cref="CapturingLogger{T}"/>, which answers true, executes the rest -- and lets
/// the message be checked rather than merely reached. A background service's log line is
/// the only thing an operator sees; "it compiled" is a weak guarantee for it.
///
/// The measured effect is recorded in this PR's body: the two generated partials went
/// from 6/24 covered lines to fully covered, which is what took the slice back above
/// <c>main</c>'s line rate.
/// </summary>
[Collection("Postgres")]
public sealed class JobEngineLoggingTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	public JobEngineLoggingTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// A successful claim logs at Debug, naming the job, the worker that took it and the
	/// lease it was granted. Those three are exactly what an operator needs to answer
	/// "who has this job and until when" without querying the table.
	/// </summary>
	[Fact]
	public async Task ClaimingAJob_LogsTheJobWorkerAndLease()
	{
		CapturingLogger<JobQueueRepository> logger = new();
		JobQueueRepository repository = new(_fixture.ConnectionString, logger);
		await SeedQueuedJobAsync();

		ClaimedJob? claimed = await repository.ClaimJobAsync("worker-logs", TimeSpan.FromMinutes(7), CancellationToken.None);
		Assert.NotNull(claimed);

		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Debug);
		Assert.Contains(claimed.Id.ToString(), entry.Message, StringComparison.Ordinal);
		Assert.Contains("worker-logs", entry.Message, StringComparison.Ordinal);
		Assert.Contains(TimeSpan.FromMinutes(7).ToString(null, CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);
	}

	/// <summary>An empty queue must not log a claim it did not make.</summary>
	[Fact]
	public async Task ClaimingFromAnEmptyQueue_LogsNothing()
	{
		CapturingLogger<JobQueueRepository> logger = new();
		JobQueueRepository repository = new(_fixture.ConnectionString, logger);

		Assert.Null(await repository.ClaimJobAsync("worker-logs", TimeSpan.FromMinutes(5), CancellationToken.None));
		Assert.Empty(logger.Entries);
	}

	/// <summary>
	/// A failed emit is logged and swallowed -- the event stream is best-effort
	/// observability, never the record of truth. Provoked with a payload that is not
	/// valid JSON, which the <c>jsonb</c> cast rejects. The assertion is that the caller
	/// is *not* thrown at, and that the failure is nonetheless visible at Error with
	/// enough scope to find it.
	/// </summary>
	[Fact]
	public async Task AFailedEmit_IsLoggedAtErrorAndDoesNotThrow()
	{
		CapturingLogger<JobEventPublisher> logger = new();
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), logger);
		Guid jobId = await SeedQueuedJobAsync();

		await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, "this is not json", CancellationToken.None);

		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Error);
		Assert.Contains(JobEventTypes.JobLog, entry.Message, StringComparison.Ordinal);
		Assert.Contains(jobId.ToString(), entry.Message, StringComparison.Ordinal);
		Assert.NotNull(entry.Exception);
	}

	/// <summary>A successful emit logs nothing -- the error path is not on the happy path.</summary>
	[Fact]
	public async Task ASuccessfulEmit_LogsNothing()
	{
		CapturingLogger<JobEventPublisher> logger = new();
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), logger);
		Guid jobId = await SeedQueuedJobAsync();

		await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, """{"line":"hello"}""", CancellationToken.None);

		Assert.Empty(logger.Entries);
	}

	/// <summary>
	/// The capturing logger has to be able to fail, like anything else here. Driving the
	/// same claim through <c>NullLogger</c> -- the fake this file exists to replace --
	/// must capture nothing, which is the demonstration that <c>IsEnabled</c> is what
	/// gates the generated body rather than something incidental about the test.
	/// </summary>
	[Fact]
	public async Task NullLoggerShortCircuitsTheGeneratedBody_WhichIsWhyThisFileExists()
	{
		CapturingLogger<JobQueueRepository> capturing = new();
		await SeedQueuedJobAsync();
		await new JobQueueRepository(_fixture.ConnectionString, capturing)
			.ClaimJobAsync("worker-capturing", TimeSpan.FromMinutes(5), CancellationToken.None);

		DisabledLogger<JobQueueRepository> disabled = new();
		await SeedQueuedJobAsync();
		await new JobQueueRepository(_fixture.ConnectionString, disabled)
			.ClaimJobAsync("worker-disabled", TimeSpan.FromMinutes(5), CancellationToken.None);

		Assert.NotEmpty(capturing.Entries);
		Assert.Equal(0, disabled.LogCallCount);
	}

	/// <summary>
	/// The <c>EventCommandTimeoutSeconds</c> budget is the entire content of this PR's
	/// #108/#117 argument, and until now it was a number in a config class that nothing
	/// exercised. This holds <c>trg_job_events_assign_seq</c>'s advisory lock
	/// (<c>pg_advisory_xact_lock(875190002)</c>, from 0001) open in another transaction,
	/// so the emit blocks on exactly the contention #117 measured, and asserts three
	/// things: the caller is not thrown at, the wait is bounded by the configured budget
	/// rather than Npgsql's inherited 30s default, and the log line names lock contention
	/// instead of surfacing a bare timeout an operator would have to decode.
	/// </summary>
	[Fact]
	public async Task AnEmitBlockedOnTheOrderingLock_TimesOutOnBudgetAndNamesTheContention()
	{
		CapturingLogger<JobEventPublisher> logger = new();
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 1, new InPlaySecretRedactor(), logger);
		Guid jobId = await SeedQueuedJobAsync();

		await using NpgsqlConnection blocker = new(_fixture.ConnectionString);
		await blocker.OpenAsync();
		await using NpgsqlTransaction holdingTransaction = await blocker.BeginTransactionAsync();

		await using (NpgsqlCommand takeLock = new("SELECT pg_advisory_xact_lock(875190002)", blocker, holdingTransaction))
		{
			await takeLock.ExecuteNonQueryAsync();
		}

		long startedAt = Environment.TickCount64;
		await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, "{}", CancellationToken.None);
		long elapsedMs = Environment.TickCount64 - startedAt;

		await holdingTransaction.RollbackAsync();

		// Bounded by the 1s budget, not by Npgsql's 30s default. The upper bound is loose
		// on purpose -- the assertion is "the budget governs", not a stopwatch reading.
		Assert.True(elapsedMs < 15_000, $"Emit took {elapsedMs}ms; the configured 1s command timeout did not govern the wait.");

		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Error);
		Assert.Contains("lock contention", entry.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("trg_job_events_assign_seq", entry.Message, StringComparison.Ordinal);

		// Best-effort means best-effort: the event is dropped, and nothing else is.
		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM job_events WHERE job_id = $1", verify);
		count.Parameters.AddWithValue(jobId);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// The control for the test above: with the lock free, the same emit succeeds and
	/// logs nothing. Without this, "the emit was dropped" would also be satisfied by a
	/// publisher that never worked at all.
	/// </summary>
	[Fact]
	public async Task WithTheOrderingLockFree_TheSameEmitSucceeds()
	{
		CapturingLogger<JobEventPublisher> logger = new();
		JobEventPublisher publisher = new(_fixture.ConnectionString, commandTimeoutSeconds: 1, new InPlaySecretRedactor(), logger);
		Guid jobId = await SeedQueuedJobAsync();

		await publisher.EmitAsync(JobEventTypes.JobLog, jobId, null, "{}", CancellationToken.None);

		Assert.Empty(logger.Entries);

		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM job_events WHERE job_id = $1", verify);
		count.Parameters.AddWithValue(jobId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
	}

	private async Task<Guid> SeedQueuedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('download', 1, 'queued') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>Stands in for <c>NullLogger</c>: reports disabled, and counts any Log call that reaches it anyway.</summary>
	private sealed class DisabledLogger<T> : ILogger<T>
	{
		public int LogCallCount { get; private set; }

		public IDisposable BeginScope<TState>(TState state)
			where TState : notnull => NullLogger.Instance.BeginScope(state);

		public bool IsEnabled(LogLevel logLevel) => false;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			LogCallCount++;
	}
}
