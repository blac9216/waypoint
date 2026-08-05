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

using Microsoft.Extensions.Logging;
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
/// Epic #7 slice 1 (#165): the replay-then-live seam is exact (every event after the
/// requested seq exactly once, in order, across the table/channel handoff), scoping
/// filters to one run, and a stalled subscriber is dropped at the bound with a
/// resync hint while other subscribers and the poller carry on.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the service after every test.
public sealed class JobEventStreamServiceTests : IAsyncLifetime
#pragma warning restore CA1001
{
	private readonly PostgresFixture _fixture;
	private JobEventStreamService _service = null!;
	private CapturingLogger<JobEventStreamService> _logger = null!;
	private Guid _runId;
	private Guid _otherRunId;

	public JobEventStreamServiceTests(PostgresFixture fixture)
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
		_logger = new CapturingLogger<JobEventStreamService>();
		_service = CreateService(queueCapacity: 1_000);
		await _service.StartAsync(CancellationToken.None);
	}

	public async Task DisposeAsync()
	{
		await _service.StopAsync(CancellationToken.None);
	}

	private JobEventStreamService CreateService(int queueCapacity)
	{
		JobEngineOptions options = new()
		{
			EventStreamPollInterval = TimeSpan.FromMilliseconds(50),
			EventStreamSubscriberQueueCapacity = queueCapacity
		};
		return new JobEventStreamService(_fixture.ConnectionString, Options.Create(options), _logger);
	}

	[Fact]
	public async Task ReplayThenLive_YieldsEveryEventExactlyOnce_InSeqOrder()
	{
		// History that exists before the subscriber shows up.
		for (int index = 0; index < 5; index++)
		{
			await InsertEventAsync(_runId, $"pre-{index}");
		}

		long resumeAfter = await GetSeqOfAsync("pre-1");

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
		List<StreamedJobEvent> received = [];
		Task consume = Task.Run(async () =>
		{
			await foreach (StreamedJobEvent row in _service.ReadAsync(JobEventStreamScope.Global, resumeAfter, cts.Token))
			{
				lock (received)
				{
					received.Add(row);
				}

				if (received.Count >= 8)
				{
					break;
				}
			}
		}, cts.Token);

		// Live rows committed while the subscriber is attached.
		await Task.Delay(TimeSpan.FromMilliseconds(200));
		for (int index = 0; index < 5; index++)
		{
			await InsertEventAsync(_runId, $"live-{index}");
		}

		await consume;

		Assert.Equal(8, received.Count);
		Assert.Equal(received.Select(row => row.Seq).OrderBy(seq => seq), received.Select(row => row.Seq));
		Assert.Equal(received.Select(row => row.Seq).Distinct().Count(), received.Count);

		// pre-2..4 (replay) then live-0..4 would be 8 rows starting at pre-2.
		Assert.Contains(received, row => row.PayloadJson.Contains("pre-2", StringComparison.Ordinal));
		Assert.Contains(received, row => row.PayloadJson.Contains("live-4", StringComparison.Ordinal));
		Assert.DoesNotContain(received, row => row.PayloadJson.Contains("pre-0", StringComparison.Ordinal));
		Assert.DoesNotContain(received, row => row.PayloadJson.Contains("\"pre-1\"", StringComparison.Ordinal));
	}

	[Fact]
	public async Task NullAfterSeq_StartsAtTheTail_LiveOnly()
	{
		await InsertEventAsync(_runId, "history-not-replayed");

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
		List<StreamedJobEvent> received = [];
		Task consume = Task.Run(async () =>
		{
			await foreach (StreamedJobEvent row in _service.ReadAsync(JobEventStreamScope.Global, afterSeq: null, cts.Token))
			{
				received.Add(row);
				break;
			}
		}, cts.Token);

		await Task.Delay(TimeSpan.FromMilliseconds(200));
		await InsertEventAsync(_runId, "the-live-one");
		await consume;

		StreamedJobEvent only = Assert.Single(received);
		Assert.Contains("the-live-one", only.PayloadJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunScope_DeliversOnlyThatRunsEvents()
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
		List<StreamedJobEvent> received = [];
		Task consume = Task.Run(async () =>
		{
			await foreach (StreamedJobEvent row in _service.ReadAsync(JobEventStreamScope.ForRun(_runId), afterSeq: null, cts.Token))
			{
				received.Add(row);
				if (received.Count >= 2)
				{
					break;
				}
			}
		}, cts.Token);

		await Task.Delay(TimeSpan.FromMilliseconds(200));
		await InsertEventAsync(_otherRunId, "other-run-noise-1");
		await InsertEventAsync(_runId, "mine-1");
		await InsertEventAsync(_otherRunId, "other-run-noise-2");
		await InsertEventAsync(_runId, "mine-2");
		await consume;

		Assert.Equal(2, received.Count);
		Assert.All(received, row => Assert.Equal(_runId, row.RunId));
		Assert.All(received, row => Assert.Contains("mine-", row.PayloadJson, StringComparison.Ordinal));
	}

	[Fact]
	public async Task AStalledSubscriber_IsDroppedAtTheBound_OthersUnaffected()
	{
		await _service.StopAsync(CancellationToken.None);
		_service = CreateService(queueCapacity: 4);
		await _service.StartAsync(CancellationToken.None);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(25));

		// The stalled subscriber: attaches (registration happens at the first
		// MoveNext), takes one row, then stops advancing -- its queue fills behind it.
		IAsyncEnumerator<StreamedJobEvent> stalled =
			_service.ReadAsync(JobEventStreamScope.Global, afterSeq: null, cts.Token).GetAsyncEnumerator(cts.Token);

		// The healthy subscriber reads everything.
		List<StreamedJobEvent> healthyReceived = [];
		Task healthy = Task.Run(async () =>
		{
			await foreach (StreamedJobEvent row in _service.ReadAsync(JobEventStreamScope.Global, afterSeq: null, cts.Token))
			{
				healthyReceived.Add(row);
				if (healthyReceived.Count >= 13)
				{
					break;
				}
			}
		}, cts.Token);

		// Paced so the healthy reader (which drains immediately) never accumulates
		// 4 unread rows, while the stalled one -- advancing past only its first row --
		// must overflow its 4-slot queue.
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await InsertEventAsync(_runId, "flood-first");
		Assert.True(await stalled.MoveNextAsync());
		for (int index = 0; index < 12; index++)
		{
			await InsertEventAsync(_runId, $"flood-{index}");
			await Task.Delay(TimeSpan.FromMilliseconds(60));
		}

		await healthy;
		Assert.Contains(_logger.EntriesAt(LogLevel.Warning), entry => entry.Message.Contains("subscriber dropped", StringComparison.Ordinal));

		// Draining the stalled enumerator yields whatever was queued before the drop,
		// then surfaces the overflow with its resync point.
		JobEventStreamOverflowException overflow = await Assert.ThrowsAsync<JobEventStreamOverflowException>(async () =>
		{
			while (await stalled.MoveNextAsync())
			{
			}
		});

		Assert.Equal(13, healthyReceived.Count);
		Assert.True(overflow.LastDeliveredSeq > 0);
		await stalled.DisposeAsync();
	}

	/// <summary>PR #168 round 1, finding 1: a prime whose query raced a registration
	/// (rows committed between the subscriber attaching and the prime result landing)
	/// must NOT advance the tail past undelivered rows -- reproduced deterministically
	/// through the internal seam: subscribe, commit rows the poller has not yet seen,
	/// force a prime, then start the poller and require every row to arrive.</summary>
	[Fact]
	public async Task APrimeRacingARegistration_CannotGapTheSubscriber()
	{
		// Fresh, NOT-started service: no poller ticks compete with the sequence below.
		await _service.StopAsync(CancellationToken.None);
		_service = CreateService(queueCapacity: 1_000);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
		List<StreamedJobEvent> received = [];
		Task consume = Task.Run(async () =>
		{
			await foreach (StreamedJobEvent row in _service.ReadAsync(JobEventStreamScope.Global, afterSeq: null, cts.Token))
			{
				received.Add(row);
				if (received.Count >= 3)
				{
					break;
				}
			}
		}, cts.Token);

		// Let the subscriber register (its own initial prime runs with zero
		// subscribers and is legitimate), then commit rows nobody has delivered.
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		for (int index = 0; index < 3; index++)
		{
			await InsertEventAsync(_runId, $"raced-{index}");
		}

		// The racy prime: pre-fix this advanced the tail past raced-0..2 and the
		// subscriber was permanently gapped; post-fix it is a no-op while anyone is
		// subscribed, and the poller (started next) delivers through the fan-out.
		await _service.PrimeTailForTestAsync(CancellationToken.None);
		await _service.StartAsync(CancellationToken.None);

		await consume;

		Assert.Equal(3, received.Count);
		Assert.Equal(
			new[] { "raced-0", "raced-1", "raced-2" },
			received.Select(row => System.Text.Json.JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("marker").GetString()));
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}'::jsonb, 'pending') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task InsertEventAsync(Guid runId, string marker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO job_events (run_id, event_type, payload) VALUES ($1, 'run.progress', jsonb_build_object('marker', $2::text))", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(marker);
		await insert.ExecuteNonQueryAsync();
	}

	private async Task<long> GetSeqOfAsync(string marker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT seq FROM job_events WHERE payload->>'marker' = $1", connection);
		query.Parameters.AddWithValue(marker);
		return (long)(await query.ExecuteScalarAsync())!;
	}
}
