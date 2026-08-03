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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the two properties <c>job_events</c> owes the SSE layer (issue #6), against a
/// real PostgreSQL 16 container:
///
/// <list type="number">
/// <item><description>
/// <b>Replay safety.</b> A reader that has observed <c>seq = N</c> must never afterwards
/// be able to miss a committed event with <c>seq &lt; N</c>. That is a statement about
/// <i>commit</i> order, not about the uniqueness of an identity column — see
/// <see cref="InterleavedCommits_ReaderAdvancingLastEventId_NeverMissesACommittedEvent"/>,
/// which fails against a plain <c>GENERATED ALWAYS AS IDENTITY</c> column because identity
/// values are handed out at <c>INSERT</c> time while rows become visible at <c>COMMIT</c>
/// time.
/// </description></item>
/// <item><description>
/// <b>Event scoping.</b> The six contract event types are not all job-scoped; a
/// <c>system.notice</c> has no job and no run at all. The scope tests pin which of
/// <c>job_id</c>/<c>run_id</c> each type requires.
/// </description></item>
/// </list>
/// </summary>
[Collection("Postgres")]
public sealed class JobEventsSeqTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	public JobEventsSeqTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
	}

	public Task DisposeAsync()
	{
		return Task.CompletedTask;
	}

	/// <summary>
	/// Necessary but nowhere near sufficient: uniqueness alone cannot fail while <c>seq</c>
	/// comes from a sequence, so this pins the cheap property and nothing more. The
	/// property that actually protects <c>Last-Event-ID</c> replay is asserted by the two
	/// interleaved-commit tests below.
	/// </summary>
	[Fact]
	public async Task ConcurrentInserts_ProduceUniqueSeqValues()
	{
		Guid jobId = await SeedJobAsync();
		const int writerCount = 12;
		const int eventsPerWriter = 25;

		IEnumerable<Task<long[]>> writers = Enumerable.Range(0, writerCount)
			.Select(_ => InsertEventsAsync(jobId, runId: null, eventsPerWriter));

		long[][] results = await Task.WhenAll(writers);
		long[] allSeqs = results.SelectMany(seqs => seqs).ToArray();

		Assert.Equal(writerCount * eventsPerWriter, allSeqs.Length);
		Assert.Equal(allSeqs.Length, allSeqs.Distinct().Count());
	}

	/// <summary>
	/// The minimal reproduction of the replay hole, and the reason the schema serializes
	/// <c>seq</c> assignment in commit order: a slow writer takes the lower <c>seq</c> and a
	/// fast writer commits first. A reader that advances <c>Last-Event-ID</c> to the highest
	/// <c>seq</c> it has seen — which is exactly what an SSE client does — then reconnects
	/// past the slow writer's event, permanently.
	/// </summary>
	[Fact]
	public async Task InterleavedCommits_ReaderAdvancingLastEventId_NeverMissesACommittedEvent()
	{
		Guid jobId = await SeedJobAsync();
		string marker = Guid.NewGuid().ToString("N");

		SortedSet<long> observed = [];
		long lastEventId = 0;

		await using NpgsqlConnection readerConnection = new(_fixture.ConnectionString);
		await readerConnection.OpenAsync();

		// The slow writer opens a transaction, inserts, and holds it open — committed
		// later, out of seq order relative to the fast writer below.
		await using NpgsqlConnection slowConnection = new(_fixture.ConnectionString);
		await slowConnection.OpenAsync();
		NpgsqlTransaction slowTransaction = await slowConnection.BeginTransactionAsync();
		long slowSeq = await InsertEventAsync(slowConnection, slowTransaction, jobId, marker);

		// The fast writer runs on its own task: against an unserialized schema it finishes
		// immediately with a *higher* seq while the slow writer is still uncommitted;
		// against the fixed schema it blocks until the slow writer commits. Both outcomes
		// must leave the reader whole, so the test must not assume either.
		Task<long> fastWriter = Task.Run(async () =>
		{
			await using NpgsqlConnection connection = new(_fixture.ConnectionString);
			await connection.OpenAsync();
			await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
			long seq = await InsertEventAsync(connection, transaction, jobId, marker);
			await transaction.CommitAsync();
			return seq;
		});

		// Give the fast writer its chance to commit ahead of the slow one, then poll.
		await Task.WhenAny(fastWriter, Task.Delay(TimeSpan.FromSeconds(2)));
		lastEventId = await PollAsync(readerConnection, marker, lastEventId, observed);

		await slowTransaction.CommitAsync();
		await slowTransaction.DisposeAsync();
		long fastSeq = await fastWriter;

		// The reconnect: replay from the Last-Event-ID the reader recorded, twice, so a
		// merely-slow commit cannot be mistaken for a lost one.
		lastEventId = await PollAsync(readerConnection, marker, lastEventId, observed);
		await Task.Delay(TimeSpan.FromMilliseconds(250));
		await PollAsync(readerConnection, marker, lastEventId, observed);

		Assert.True(
			observed.Contains(slowSeq) && observed.Contains(fastSeq),
			string.Create(
				CultureInfo.InvariantCulture,
				$"A reader that advanced Last-Event-ID must still see every committed event. slow seq={slowSeq}, fast seq={fastSeq}, observed=[{string.Join(",", observed)}]. A missing seq here is an event that is committed, durable, and permanently unreachable by any client that reconnects."));
	}

	/// <summary>
	/// The same guarantee under sustained load: writers hold their transactions open for
	/// different lengths of time, so assignment order and commit order diverge constantly,
	/// while a reader polls throughout and only ever advances to the highest <c>seq</c> it
	/// has seen. Nothing committed may be skipped.
	/// </summary>
	[Fact]
	public async Task ConcurrentWritersCommittingOutOfAssignmentOrder_ReaderNeverSkipsACommittedEvent()
	{
		Guid jobId = await SeedJobAsync();
		string marker = Guid.NewGuid().ToString("N");
		const int writerCount = 6;
		const int eventsPerWriter = 8;

		SortedSet<long> observed = [];
		using CancellationTokenSource readerStop = new();

		Task reader = Task.Run(async () =>
		{
			await using NpgsqlConnection connection = new(_fixture.ConnectionString);
			await connection.OpenAsync();
			long lastEventId = 0;

			while (!readerStop.IsCancellationRequested)
			{
				lastEventId = await PollAsync(connection, marker, lastEventId, observed);
				await Task.Delay(TimeSpan.FromMilliseconds(2));
			}

			// Final drain, from wherever Last-Event-ID ended up.
			for (int i = 0; i < 3; i++)
			{
				lastEventId = await PollAsync(connection, marker, lastEventId, observed);
				await Task.Delay(TimeSpan.FromMilliseconds(50));
			}
		});

		IEnumerable<Task> writers = Enumerable.Range(0, writerCount).Select(writerIndex => Task.Run(async () =>
		{
			await using NpgsqlConnection connection = new(_fixture.ConnectionString);
			await connection.OpenAsync();

			for (int i = 0; i < eventsPerWriter; i++)
			{
				await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
				await InsertEventAsync(connection, transaction, jobId, marker);

				// Staggered holds: writer 0 commits immediately, writer 5 holds ~20ms, so
				// a lower seq is routinely committed after a higher one.
				await Task.Delay(TimeSpan.FromMilliseconds(writerIndex * 4));
				await transaction.CommitAsync();
			}
		}));

		await Task.WhenAll(writers);
		await readerStop.CancelAsync();
		await reader;

		long[] committed = await ReadAllSeqsAsync(marker);
		long[] missed = committed.Where(seq => !observed.Contains(seq)).ToArray();

		Assert.True(
			missed.Length == 0,
			string.Create(
				CultureInfo.InvariantCulture,
				$"{missed.Length} committed event(s) were never delivered to a reader that only ever advanced Last-Event-ID to the highest seq it had seen: [{string.Join(",", missed)}]."));
		Assert.Equal(writerCount * eventsPerWriter, committed.Length);
	}

	[Fact]
	public async Task LastEventIdReplay_GlobalStream_ReturnsTheGapExactlyOnceInOrder()
	{
		Guid jobId = await SeedJobAsync();
		const int totalEvents = 60;

		long[] seqs = await InsertEventsAsync(jobId, runId: null, totalEvents);
		Array.Sort(seqs);

		// A client that last saw the 20th event of this batch reconnects with
		// Last-Event-ID = seqs[19]; replay must return exactly seqs[20..], in order.
		long lastEventId = seqs[19];
		long[] expectedGap = seqs[20..];

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"SELECT seq FROM job_events WHERE job_id = $1 AND seq > $2 ORDER BY seq", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(lastEventId);

		List<long> replayed = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
		{
			while (await reader.ReadAsync())
			{
				replayed.Add(reader.GetInt64(0));
			}
		}

		Assert.Equal(expectedGap, replayed);
	}

	[Fact]
	public async Task LastEventIdReplay_PerRunStream_ReturnsOnlyThatRunsGapInOrder()
	{
		Guid jobA = await SeedJobAsync();
		Guid jobB = await SeedJobAsync();
		Guid runA = Guid.NewGuid();
		Guid runB = Guid.NewGuid();

		// Interleave two runs' writers so their seq ranges genuinely overlap in
		// commit order, not just in insertion order.
		Task<long[]> writeA = InsertEventsAsync(jobA, runA, 30);
		Task<long[]> writeB = InsertEventsAsync(jobB, runB, 30);
		long[][] results = await Task.WhenAll(writeA, writeB);
		long[] runASeqs = results[0].OrderBy(seq => seq).ToArray();

		long lastEventId = runASeqs[9];
		long[] expectedGap = runASeqs[10..];

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"SELECT seq FROM job_events WHERE run_id = $1 AND seq > $2 ORDER BY seq", connection);
		command.Parameters.AddWithValue(runA);
		command.Parameters.AddWithValue(lastEventId);

		List<long> replayed = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
		{
			while (await reader.ReadAsync())
			{
				replayed.Add(reader.GetInt64(0));
			}
		}

		// Every replayed row belongs to run A, in order, and nothing from run B leaked in.
		Assert.Equal(expectedGap, replayed);
	}

	[Fact]
	public async Task LastEventIdReplay_UsesAnIndexRangeScan_NotASort()
	{
		Guid jobId = await SeedJobAsync();

		// A handful of rows is too small for the planner to ever prefer an index —
		// a sequential scan of a two-page table is genuinely cheaper, which would
		// make this assertion pass for the wrong reason on a toy dataset. Enough rows
		// (and a fresh ANALYZE, since these are all new inserts with no autovacuum
		// run yet) makes a *selective* tail-range query the realistic replay shape:
		// "give me the few events since my Last-Event-ID" out of a large stream.
		long[] seqs = await InsertEventsAsync(jobId, runId: null, 3_000);
		long recentThreshold = seqs.Max() - 3;

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand analyze = new("ANALYZE job_events", connection))
		{
			await analyze.ExecuteNonQueryAsync();
		}

		await using NpgsqlCommand command = new(
			"EXPLAIN (FORMAT TEXT) SELECT seq FROM job_events WHERE seq > $1 ORDER BY seq", connection);
		command.Parameters.AddWithValue(recentThreshold);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

		List<string> planLines = [];
		while (await reader.ReadAsync())
		{
			planLines.Add(reader.GetString(0));
		}

		string plan = string.Join('\n', planLines);

		// seq is the primary key, so "seq > $x ORDER BY seq" is satisfied directly by
		// the PK's btree in order — the planner should never need a separate sort
		// step to serve this replay query.
		Assert.Contains("Index", plan, StringComparison.Ordinal);
		Assert.DoesNotContain("Sort Key", plan, StringComparison.Ordinal);
	}

	/// <summary>
	/// The global stream must be able to carry an appliance-wide notice that belongs to no
	/// job and no run — the case that was impossible while <c>job_id</c> was
	/// <c>NOT NULL</c>.
	/// </summary>
	[Fact]
	public async Task ApplianceGlobalEvent_IsInsertableWithNoJobAndNoRun()
	{
		long seq = await InsertScopedEventAsync("system.notice", jobId: null, runId: null);
		Assert.True(seq > 0);
	}

	[Theory]
	[InlineData("job.state")]
	[InlineData("job.log")]
	[InlineData("download.progress")]
	public async Task JobScopedEvent_RequiresAJobId(string eventType)
	{
		Guid jobId = await SeedJobAsync();

		Assert.True(await InsertScopedEventAsync(eventType, jobId, runId: null) > 0);
		await AssertScopeRejectedAsync(eventType, jobId: null, runId: null);
	}

	[Theory]
	[InlineData("run.progress")]
	[InlineData("queue.state")]
	public async Task RunScopedEvent_RequiresARunAndRejectsAJobId(string eventType)
	{
		Guid jobId = await SeedJobAsync();
		Guid runId = await SeedRunAsync();

		Assert.True(await InsertScopedEventAsync(eventType, jobId: null, runId) > 0);
		await AssertScopeRejectedAsync(eventType, jobId: null, runId: null);
		await AssertScopeRejectedAsync(eventType, jobId, runId);
	}

	[Fact]
	public async Task ApplianceGlobalEvent_RejectsAJobOrRunScope()
	{
		Guid jobId = await SeedJobAsync();
		Guid runId = await SeedRunAsync();

		await AssertScopeRejectedAsync("system.notice", jobId, runId: null);
		await AssertScopeRejectedAsync("system.notice", jobId: null, runId);
	}

	private async Task AssertScopeRejectedAsync(string eventType, Guid? jobId, Guid? runId)
	{
		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
			() => InsertScopedEventAsync(eventType, jobId, runId));

		Assert.Equal("job_events_scope_check", exception.ConstraintName);
	}

	private async Task<long> InsertScopedEventAsync(string eventType, Guid? jobId, Guid? runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"""
			INSERT INTO job_events (run_id, job_id, event_type)
			VALUES ($1, $2, $3)
			RETURNING seq
			""", connection);
		command.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
		command.Parameters.AddWithValue(eventType);

		return (long)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// One SSE-client poll: read everything after <c>Last-Event-ID</c>, record it, and
	/// advance <c>Last-Event-ID</c> to the highest <c>seq</c> seen — never further.
	/// </summary>
	private static async Task<long> PollAsync(
		NpgsqlConnection connection, string marker, long lastEventId, SortedSet<long> observed)
	{
		await using NpgsqlCommand command = new(
			"""
			SELECT seq FROM job_events
			WHERE payload ->> 'marker' = $1 AND seq > $2
			ORDER BY seq
			""", connection);
		command.Parameters.AddWithValue(marker);
		command.Parameters.AddWithValue(lastEventId);

		long highest = lastEventId;
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			long seq = reader.GetInt64(0);
			lock (observed)
			{
				observed.Add(seq);
			}

			highest = Math.Max(highest, seq);
		}

		return highest;
	}

	private async Task<long[]> ReadAllSeqsAsync(string marker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"SELECT seq FROM job_events WHERE payload ->> 'marker' = $1 ORDER BY seq", connection);
		command.Parameters.AddWithValue(marker);

		List<long> seqs = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			seqs.Add(reader.GetInt64(0));
		}

		return [.. seqs];
	}

	private async Task<Guid> SeedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insertJob = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('catalog-index', 1, 'running') RETURNING id",
			connection);
		return (Guid)(await insertJob.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insertRun = new(
			"INSERT INTO runs (run_type, state) VALUES ('scan', 'running') RETURNING id", connection);
		return (Guid)(await insertRun.ExecuteScalarAsync())!;
	}

	private static async Task<long> InsertEventAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, string marker)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO job_events (job_id, event_type, payload)
			VALUES ($1, 'job.log', jsonb_build_object('marker', $2::text))
			RETURNING seq
			""", connection, transaction);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(marker);

		return (long)(await command.ExecuteScalarAsync())!;
	}

	private async Task<long[]> InsertEventsAsync(Guid jobId, Guid? runId, int count)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		long[] seqs = new long[count];
		for (int i = 0; i < count; i++)
		{
			await using NpgsqlCommand insertEvent = new(
				"""
				INSERT INTO job_events (run_id, job_id, event_type, payload)
				VALUES ($1, $2, 'job.log', '{}'::jsonb)
				RETURNING seq
				""", connection);
			insertEvent.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
			insertEvent.Parameters.AddWithValue(jobId);
			seqs[i] = (long)(await insertEvent.ExecuteScalarAsync())!;
		}

		return seqs;
	}
}
