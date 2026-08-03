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
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves <c>job_events.seq</c> (the SSE replay source, docs/api-contract.md) stays
/// monotonic and gap-safe under real concurrent writers, and that a
/// <c>Last-Event-ID</c>-style range query serves the exact replay gap, in order,
/// exactly once — for both stream scopes (global and per-run).
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

	[Fact]
	public async Task ConcurrentInserts_ProduceUniqueMonotonicSeq_NoDuplicatesNoGapsInTheReturnedSet()
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

	private async Task<Guid> SeedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insertJob = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('catalog-index', 1, 'running') RETURNING id",
			connection);
		return (Guid)(await insertJob.ExecuteScalarAsync())!;
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
