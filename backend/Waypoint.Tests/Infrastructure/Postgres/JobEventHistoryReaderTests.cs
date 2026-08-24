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

using System.Text.Json;
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
/// Issue #581 (ADR-0019): <see cref="JobEventStreamService.ReadHistoryAsync"/> against
/// real Postgres -- the bounded, cursor-paged historical counterpart to
/// <see cref="JobEventStreamServiceTests"/>'s live/replay coverage. Focus: no
/// duplicates/gaps across page boundaries on a fixed set (stable keyset ordering),
/// job_id/event-type/severity filters preserve that same paging correctness rather
/// than silently truncating, the page-size cap, and empty/unknown-run/unknown-job
/// behavior.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle via IAsyncLifetime; no service to dispose here (ReadHistoryAsync opens its own short-lived connection per call).
public sealed class JobEventHistoryReaderTests : IAsyncLifetime
#pragma warning restore CA1001
{
	private readonly PostgresFixture _fixture;
	private JobEventStreamService _reader = null!;
	private Guid _runId;
	private Guid _otherRunId;

	public JobEventHistoryReaderTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_runId = await SeedRunAsync();
		_otherRunId = await SeedRunAsync();
		_reader = new JobEventStreamService(
			_fixture.ConnectionString,
			Options.Create(new JobEngineOptions { Enabled = false }),
			NullLogger<JobEventStreamService>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task PagingThroughASmallLimit_YieldsEveryRowExactlyOnceInSeqOrder()
	{
		Guid jobId = await SeedJobAsync(_runId);
		List<long> insertedSeqs = [];
		for (int index = 0; index < 25; index++)
		{
			insertedSeqs.Add(await InsertLogEventAsync(_runId, jobId, "information", $"line-{index}"));
		}

		List<StreamedJobEvent> collected = [];
		long? afterSeq = null;
		int pageCount = 0;
		while (true)
		{
			JobEventHistoryPage page = await _reader.ReadHistoryAsync(
				new JobEventHistoryQuery(_runId, JobId: null, EventTypes: null, Severities: null, afterSeq, Limit: 7),
				CancellationToken.None);
			collected.AddRange(page.Items);
			pageCount++;

			if (page.NextCursor is null)
			{
				break;
			}

			afterSeq = page.NextCursor;
			Assert.True(pageCount < 20, "paging did not terminate -- likely a cursor/limit off-by-one");
		}

		// 25 rows at a page size of 7 -> 4 pages (7+7+7+4), never fewer (no silent
		// truncation) and never more (no duplicate re-delivery of the last row).
		Assert.Equal(4, pageCount);
		Assert.Equal(insertedSeqs, collected.Select(row => row.Seq));
		Assert.Equal(insertedSeqs.Count, collected.Select(row => row.Seq).Distinct().Count());
	}

	[Fact]
	public async Task FullPageWithMoreRows_ReturnsNonNullCursor_ExactPageEndsWithNullCursor()
	{
		Guid jobId = await SeedJobAsync(_runId);
		for (int index = 0; index < 5; index++)
		{
			await InsertLogEventAsync(_runId, jobId, "information", $"line-{index}");
		}

		JobEventHistoryPage exactPage = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, null, null, null, null, Limit: 5), CancellationToken.None);
		Assert.Equal(5, exactPage.Items.Count);
		Assert.Null(exactPage.NextCursor);

		JobEventHistoryPage truncatedPage = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, null, null, null, null, Limit: 4), CancellationToken.None);
		Assert.Equal(4, truncatedPage.Items.Count);
		Assert.NotNull(truncatedPage.NextCursor);
		// The cursor is the seq of the LAST RETURNED row (item index 3, the 4th of 5),
		// not the excluded 5th -- passing it back as afterSeq resumes exactly at the
		// row that follows what the caller already has, with no gap or duplicate.
		Assert.Equal(exactPage.Items[3].Seq, truncatedPage.NextCursor);
		Assert.Equal(truncatedPage.Items, exactPage.Items.Take(4));
	}

	[Fact]
	public async Task JobIdFilter_NarrowsToOnlyThatJobsEvents_AcrossPages()
	{
		Guid jobA = await SeedJobAsync(_runId);
		Guid jobB = await SeedJobAsync(_runId);
		List<long> jobASeqs = [];
		for (int index = 0; index < 6; index++)
		{
			jobASeqs.Add(await InsertLogEventAsync(_runId, jobA, "information", $"a-{index}"));
			await InsertLogEventAsync(_runId, jobB, "information", $"b-{index}");
		}

		List<StreamedJobEvent> collected = [];
		long? afterSeq = null;
		while (true)
		{
			JobEventHistoryPage page = await _reader.ReadHistoryAsync(
				new JobEventHistoryQuery(_runId, jobA, null, null, afterSeq, Limit: 2), CancellationToken.None);
			collected.AddRange(page.Items);
			if (page.NextCursor is null)
			{
				break;
			}

			afterSeq = page.NextCursor;
		}

		Assert.Equal(jobASeqs, collected.Select(row => row.Seq));
		Assert.All(collected, row => Assert.Equal(jobA, row.JobId));
	}

	[Fact]
	public async Task EventTypeFilter_NarrowsAcrossPages_WithoutSkippingMatches()
	{
		Guid jobId = await SeedJobAsync(_runId);
		List<long> logSeqs = [];
		for (int index = 0; index < 4; index++)
		{
			logSeqs.Add(await InsertLogEventAsync(_runId, jobId, "information", $"log-{index}"));
			await InsertJobStateEventAsync(_runId, jobId, "running");
		}

		List<StreamedJobEvent> collected = [];
		long? afterSeq = null;
		while (true)
		{
			JobEventHistoryPage page = await _reader.ReadHistoryAsync(
				new JobEventHistoryQuery(_runId, null, [JobEventTypes.JobLog], null, afterSeq, Limit: 1), CancellationToken.None);
			collected.AddRange(page.Items);
			if (page.NextCursor is null)
			{
				break;
			}

			afterSeq = page.NextCursor;
		}

		Assert.Equal(logSeqs, collected.Select(row => row.Seq));
		Assert.All(collected, row => Assert.Equal(JobEventTypes.JobLog, row.EventType));
	}

	[Fact]
	public async Task SeverityFilter_NarrowsToMatchingJobLogRowsOnly()
	{
		Guid jobId = await SeedJobAsync(_runId);
		long warningSeq = await InsertLogEventAsync(_runId, jobId, "warning", "careful");
		await InsertLogEventAsync(_runId, jobId, "information", "fyi");
		long errorSeq = await InsertLogEventAsync(_runId, jobId, "error", "boom");

		JobEventHistoryPage page = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, null, null, ["warning", "error"], null, Limit: 100), CancellationToken.None);

		Assert.Equal([warningSeq, errorSeq], page.Items.Select(row => row.Seq));
	}

	[Fact]
	public async Task RunScope_NeverLeaksAnotherRunsEvents()
	{
		Guid jobId = await SeedJobAsync(_runId);
		Guid otherJobId = await SeedJobAsync(_otherRunId);
		await InsertLogEventAsync(_runId, jobId, "information", "mine");
		await InsertLogEventAsync(_otherRunId, otherJobId, "information", "not-mine");

		JobEventHistoryPage page = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, null, null, null, null, Limit: 100), CancellationToken.None);

		StreamedJobEvent only = Assert.Single(page.Items);
		Assert.Contains("mine", only.PayloadJson, StringComparison.Ordinal);
		Assert.DoesNotContain("not-mine", only.PayloadJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunWithNoEvents_ReturnsEmptyPage_NotAnError()
	{
		JobEventHistoryPage page = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, null, null, null, null, Limit: 50), CancellationToken.None);

		Assert.Empty(page.Items);
		Assert.Null(page.NextCursor);
	}

	[Fact]
	public async Task UnknownJobId_MatchesNoRows_ButRunsOwnEventsUntouched()
	{
		Guid jobId = await SeedJobAsync(_runId);
		await InsertLogEventAsync(_runId, jobId, "information", "exists");

		JobEventHistoryPage page = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, JobId: Guid.NewGuid(), null, null, null, Limit: 50), CancellationToken.None);

		Assert.Empty(page.Items);
	}

	[Fact]
	public async Task RedactedPayloadAtWriteTime_IsWhatHistoryReturns_NoAdditionalTransform()
	{
		// This reader performs no scrubbing of its own; it must return exactly what
		// was written (already-redacted) -- proven here by asserting the payload
		// round-trips byte-for-byte through ReadHistoryAsync.
		Guid jobId = await SeedJobAsync(_runId);
		long seq = await InsertLogEventAsync(_runId, jobId, "error", "already-redacted-content");

		JobEventHistoryPage page = await _reader.ReadHistoryAsync(
			new JobEventHistoryQuery(_runId, null, null, null, null, Limit: 50), CancellationToken.None);

		StreamedJobEvent row = Assert.Single(page.Items, r => r.Seq == seq);
		using JsonDocument doc = JsonDocument.Parse(row.PayloadJson);
		Assert.Equal("already-redacted-content", doc.RootElement.GetProperty("line").GetString());
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}'::jsonb, 'pending') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, state, priority) VALUES ($1, 'scan', 'queued', 3) RETURNING id", connection);
		insert.Parameters.AddWithValue(runId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<long> InsertLogEventAsync(Guid runId, Guid jobId, string severity, string line)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO job_events (run_id, job_id, event_type, payload)
			VALUES ($1, $2, 'job.log', jsonb_build_object('severity', $3::text, 'line', $4::text))
			RETURNING seq
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(jobId);
		insert.Parameters.AddWithValue(severity);
		insert.Parameters.AddWithValue(line);
		return (long)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<long> InsertJobStateEventAsync(Guid runId, Guid jobId, string toState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO job_events (run_id, job_id, event_type, payload)
			VALUES ($1, $2, 'job.state', jsonb_build_object('to', $3::text))
			RETURNING seq
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(jobId);
		insert.Parameters.AddWithValue(toState);
		return (long)(await insert.ExecuteScalarAsync())!;
	}
}
