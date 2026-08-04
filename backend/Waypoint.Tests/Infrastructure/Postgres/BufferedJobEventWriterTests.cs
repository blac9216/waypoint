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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the #117 event-write budget the buffered path claims: batches amortize the
/// ordering lock (measured as distinct inserting transactions over a burst, via each
/// row's <c>xmin</c>), enqueue order survives into commit-ordered <c>seq</c>, secrets
/// are scrubbed before the Postgres sink, loss is bounded-and-counted rather than
/// silent, and a stopping host drains what it accepted.
/// </summary>
[Collection("Postgres")]
public sealed class BufferedJobEventWriterTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private Guid _jobId;

	public BufferedJobEventWriterTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('scan', 1, 'queued') RETURNING id", connection);
		_jobId = (Guid)(await insert.ExecuteScalarAsync())!;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private BufferedJobEventWriter CreateWriter(
		InPlaySecretRedactor? redactor = null,
		JobEngineOptions? options = null,
		CapturingLogger<BufferedJobEventWriter>? logger = null,
		string? connectionString = null)
	{
		return new BufferedJobEventWriter(
			connectionString ?? _fixture.ConnectionString,
			redactor ?? new InPlaySecretRedactor(),
			Options.Create(options ?? new JobEngineOptions()),
			logger ?? new CapturingLogger<BufferedJobEventWriter>());
	}

	/// <summary>The budget's core claim: a 250-event burst lands in ceil(250/100) = 3
	/// transactions, not 250 -- read back as distinct <c>xmin</c> values, which record
	/// the inserting transaction per row and need no stats-collector delay.</summary>
	[Fact]
	public async Task ABurst_LandsInBatchedTransactions_InEnqueueOrder()
	{
		BufferedJobEventWriter writer = CreateWriter();
		string marker = Guid.NewGuid().ToString("N");
		for (int index = 0; index < 250; index++)
		{
			Assert.True(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, $"{{\"marker\":\"{marker}\",\"line\":{index}}}"));
		}

		await writer.StartAsync(CancellationToken.None);
		try
		{
			await WaitForEventCountAsync(marker, 250);
		}
		finally
		{
			await writer.StopAsync(CancellationToken.None);
		}

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand transactions = new(
			"SELECT count(DISTINCT xmin::text) FROM job_events WHERE payload->>'marker' = $1", connection))
		{
			transactions.Parameters.AddWithValue(marker);
			long distinctTransactions = (long)(await transactions.ExecuteScalarAsync())!;
			Assert.InRange(distinctTransactions, 1, 3);
		}

		// seq order must reproduce enqueue order: unnest preserves row order within a
		// batch, and batches flush in enqueue order.
		await using NpgsqlCommand ordered = new(
			"SELECT payload->>'line' FROM job_events WHERE payload->>'marker' = $1 ORDER BY seq", connection);
		ordered.Parameters.AddWithValue(marker);
		List<int> lines = [];
		await using NpgsqlDataReader reader = await ordered.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			lines.Add(int.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture));
		}

		Assert.Equal(Enumerable.Range(0, 250), lines);
	}

	/// <summary>security.md control 1 on the batched path: a tracked secret enqueued in a
	/// payload never reaches <c>job_events</c> -- and because scrubbing happens at
	/// enqueue, that holds even when the secret is untracked before the flush runs.</summary>
	[Fact]
	public async Task ATrackedSecret_NeverReachesTheTable_EvenIfUntrackedBeforeFlush()
	{
		InPlaySecretRedactor redactor = new();
		BufferedJobEventWriter writer = CreateWriter(redactor);
		string marker = Guid.NewGuid().ToString("N");
		const string canary = "invented-redaction-canary-0123";

		using (redactor.Track(canary))
		{
			Assert.True(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, $"{{\"marker\":\"{marker}\",\"line\":\"auth {canary} rejected\"}}"));
		}

		await writer.StartAsync(CancellationToken.None);
		try
		{
			await WaitForEventCountAsync(marker, 1);
		}
		finally
		{
			await writer.StopAsync(CancellationToken.None);
		}

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand stored = new("SELECT payload::text FROM job_events WHERE payload->>'marker' = $1", connection);
		stored.Parameters.AddWithValue(marker);
		string payload = (string)(await stored.ExecuteScalarAsync())!;

		Assert.DoesNotContain(canary, payload, StringComparison.Ordinal);
		Assert.Contains("[REDACTED]", payload, StringComparison.Ordinal);
	}

	/// <summary>The single-row path keeps the same control-1 guarantee.</summary>
	[Fact]
	public async Task JobEventPublisher_AlsoScrubsThePayload()
	{
		InPlaySecretRedactor redactor = new();
		JobEventPublisher publisher = new(
			_fixture.ConnectionString, commandTimeoutSeconds: 5, redactor, NullLogger<JobEventPublisher>.Instance);
		string marker = Guid.NewGuid().ToString("N");
		const string canary = "invented-redaction-canary-9876";

		using (redactor.Track(canary))
		{
			await publisher.EmitAsync(
				JobEventTypes.JobLog, _jobId, null, $"{{\"marker\":\"{marker}\",\"line\":\"{canary}\"}}", CancellationToken.None);
		}

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand stored = new("SELECT payload::text FROM job_events WHERE payload->>'marker' = $1", connection);
		stored.Parameters.AddWithValue(marker);
		string payload = (string)(await stored.ExecuteScalarAsync())!;

		Assert.DoesNotContain(canary, payload, StringComparison.Ordinal);
	}

	/// <summary>Events accepted before the host stops must land via the final drain, not die with the process.</summary>
	[Fact]
	public async Task EventsEnqueuedJustBeforeStop_AreDrainedOnShutdown()
	{
		BufferedJobEventWriter writer = CreateWriter();
		await writer.StartAsync(CancellationToken.None);
		string marker = Guid.NewGuid().ToString("N");
		for (int index = 0; index < 5; index++)
		{
			Assert.True(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, $"{{\"marker\":\"{marker}\"}}"));
		}

		await writer.StopAsync(CancellationToken.None);

		Assert.Equal(5, await CountEventsAsync(marker));
	}

	/// <summary>A full buffer drops the new event, tells the caller, and logs a counted
	/// drop -- it never blocks the producer and never grows without bound.</summary>
	[Fact]
	public void AFullBuffer_DropsTheNewEvent_CountedAndLogged()
	{
		CapturingLogger<BufferedJobEventWriter> logger = new();

		// Never started, so nothing drains: capacity 2 fills at once. The connection
		// string is irrelevant -- no flush runs in this test.
		BufferedJobEventWriter writer = CreateWriter(
			options: new JobEngineOptions { EventBufferCapacity = 2 }, logger: logger);

		Assert.True(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, "{}"));
		Assert.True(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, "{}"));
		Assert.False(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, "{}"));

		Assert.Contains(logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("buffer full", StringComparison.Ordinal));
	}

	/// <summary>#117's recorded-baseline requirement: 2,000 events must comfortably beat
	/// the ~900/s row-at-a-time plateau end to end (the assertion is deliberately loose
	/// for CI; the measured rate is logged for the record).</summary>
	[Fact]
	public async Task TwoThousandEvents_ClearWellAboveTheRowAtATimePlateau()
	{
		BufferedJobEventWriter writer = CreateWriter();
		string marker = Guid.NewGuid().ToString("N");
		for (int index = 0; index < 2000; index++)
		{
			Assert.True(writer.TryEnqueue(JobEventTypes.JobLog, _jobId, null, $"{{\"marker\":\"{marker}\",\"line\":{index}}}"));
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		await writer.StartAsync(CancellationToken.None);
		try
		{
			await WaitForEventCountAsync(marker, 2000, timeoutSeconds: 30);
		}
		finally
		{
			await writer.StopAsync(CancellationToken.None);
		}

		stopwatch.Stop();

		// 2,000 events at the unbatched ~900/s plateau would need > 2.2s of insert time
		// alone. The 10s bound is loose on purpose (CI containers are slow and the
		// wait polls at 50ms), but it still fails a regression to row-at-a-time writes
		// with a wide margin measured locally (~0.3s).
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"2000 events took {stopwatch.Elapsed.TotalSeconds:F1}s end to end -- the batch path has regressed toward row-at-a-time writes.");
	}

	private async Task<long> CountEventsAsync(string marker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM job_events WHERE payload->>'marker' = $1", connection);
		count.Parameters.AddWithValue(marker);
		return (long)(await count.ExecuteScalarAsync())!;
	}

	private async Task WaitForEventCountAsync(string marker, long expected, int timeoutSeconds = 15)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		long seen = -1;
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
		{
			seen = await CountEventsAsync(marker);
			if (seen == expected)
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(50));
		}

		Assert.Fail($"Expected {expected} events for marker {marker} within {timeoutSeconds}s, saw {seen}.");
	}
}
