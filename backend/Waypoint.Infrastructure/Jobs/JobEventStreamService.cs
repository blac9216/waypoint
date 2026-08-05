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

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>
/// The epic #7 slice-1 core: one shared tail poller over <c>job_events</c> fanning
/// out to per-subscriber bounded channels.
///
/// Why polling and not LISTEN/NOTIFY or an in-process bus (recorded on epic #7 so it
/// is not re-litigated): the #104 ordering trigger assigns <c>seq</c> in commit
/// order, so <c>WHERE seq &gt; $last ORDER BY seq</c> is an *exact* tail -- no row
/// can ever commit "behind" one already read. Replay and live are therefore the same
/// query, reconnect handoff is a seq comparison, and a single-node appliance
/// (ADR-0001) needs no cross-process push. The poll cadence matches the buffered
/// writer's flush budget, so polling adds at most one flush interval of latency.
///
/// Exactly-once handoff: a subscriber registers its channel under the fan-out lock,
/// capturing the poller's last-delivered seq S -- every row with seq &gt; S reaches
/// the channel, none at or below S does. Replay then reads the table for
/// (afterSeq, S], and the live phase continues from the channel. No gap (rows in
/// (afterSeq, S] came from the table; rows &gt; S are in the channel) and no
/// duplicate (the two ranges are disjoint by construction).
/// </summary>
public sealed partial class JobEventStreamService : BackgroundService, IJobEventFeed
{
	private sealed class Subscriber
	{
		public Subscriber(JobEventStreamScope scope, int capacity)
		{
			Scope = scope;
			Channel = System.Threading.Channels.Channel.CreateBounded<StreamedJobEvent>(new BoundedChannelOptions(capacity)
			{
				FullMode = BoundedChannelFullMode.Wait,
				SingleReader = true,
				SingleWriter = true
			});
		}

		public JobEventStreamScope Scope { get; }

		public Channel<StreamedJobEvent> Channel { get; }

		public long LastQueuedSeq { get; set; }
	}

	private readonly string _connectionString;
	private readonly IOptions<JobEngineOptions> _options;
	private readonly ILogger<JobEventStreamService> _logger;
	private readonly List<Subscriber> _subscribers = [];
	private readonly object _fanOutLock = new();
	private long _lastDeliveredSeq = -1;

	public JobEventStreamService(string connectionString, IOptions<JobEngineOptions> options, ILogger<JobEventStreamService> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_options = options;
		_logger = logger;
	}

	public async IAsyncEnumerable<StreamedJobEvent> ReadAsync(
		JobEventStreamScope scope, long? afterSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(scope);

		// -1 means "not yet primed": the poller hasn't run, so the tail must come
		// from the table for the no-replay case to actually mean "from now on".
		if (Volatile.Read(ref _lastDeliveredSeq) < 0)
		{
			await PrimeTailAsync(cancellationToken).ConfigureAwait(false);
		}

		Subscriber subscriber = new(scope, _options.Value.EventStreamSubscriberQueueCapacity);
		long liveFromSeq;
		lock (_fanOutLock)
		{
			liveFromSeq = _lastDeliveredSeq;
			subscriber.LastQueuedSeq = liveFromSeq;
			_subscribers.Add(subscriber);
		}

		try
		{
			long lastYielded = afterSeq ?? liveFromSeq;
			if (lastYielded < liveFromSeq)
			{
				await foreach (StreamedJobEvent row in QueryRangeAsync(scope, lastYielded, liveFromSeq, cancellationToken).ConfigureAwait(false))
				{
					yield return row;
					lastYielded = row.Seq;
				}
			}

			while (await subscriber.Channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				while (subscriber.Channel.Reader.TryRead(out StreamedJobEvent? row))
				{
					// Belt-and-braces against a client claiming an afterSeq ahead of
					// the tail: never yield at or below what was already yielded.
					if (row.Seq > lastYielded)
					{
						yield return row;
						lastYielded = row.Seq;
					}
				}
			}

			// The writer only completes with an exception (overflow); surface it.
			await subscriber.Channel.Reader.Completion.ConfigureAwait(false);
		}
		finally
		{
			lock (_fanOutLock)
			{
				_subscribers.Remove(subscriber);
			}
		}
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(_options.Value.EventStreamPollInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
			{
				try
				{
					await PollOnceAsync(stoppingToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception exception)
				{
					// Same contract as every other observability component: a poll
					// failure is logged and retried next tick, never fatal to the host.
					LogPollFailed(exception);
				}
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			// Host stopping.
		}
	}

	private async Task PollOnceAsync(CancellationToken cancellationToken)
	{
		if (Volatile.Read(ref _lastDeliveredSeq) < 0)
		{
			await PrimeTailAsync(cancellationToken).ConfigureAwait(false);
		}

		bool hasSubscribers;
		lock (_fanOutLock)
		{
			hasSubscribers = _subscribers.Count > 0;
		}

		if (!hasSubscribers)
		{
			// Still advance the tail so a first subscriber's "live only" start point
			// is current, but skip the fan-out machinery.
			await PrimeTailAsync(cancellationToken).ConfigureAwait(false);
			return;
		}

		List<StreamedJobEvent> batch = [];
		await foreach (StreamedJobEvent row in QueryRangeAsync(JobEventStreamScope.Global, Volatile.Read(ref _lastDeliveredSeq), long.MaxValue, cancellationToken).ConfigureAwait(false))
		{
			batch.Add(row);
		}

		if (batch.Count == 0)
		{
			return;
		}

		lock (_fanOutLock)
		{
			foreach (StreamedJobEvent row in batch)
			{
				foreach (Subscriber subscriber in _subscribers.ToArray())
				{
					if (!Matches(subscriber.Scope, row))
					{
						continue;
					}

					if (subscriber.Channel.Writer.TryWrite(row))
					{
						subscriber.LastQueuedSeq = row.Seq;
					}
					else
					{
						// The #7 drop+resync strategy: this subscriber alone is
						// terminated; the exception carries its resync point.
						subscriber.Channel.Writer.TryComplete(new JobEventStreamOverflowException(subscriber.LastQueuedSeq));
						_subscribers.Remove(subscriber);
						LogSubscriberDropped(subscriber.LastQueuedSeq);
					}
				}

				_lastDeliveredSeq = row.Seq;
			}
		}
	}

	private static bool Matches(JobEventStreamScope scope, StreamedJobEvent row)
	{
		return scope.RunId is not Guid runId || row.RunId == runId;
	}

	/// <summary>Test seam for the priming race (PR #168 round 1): drives one prime attempt deterministically.</summary>
	internal Task PrimeTailForTestAsync(CancellationToken cancellationToken) => PrimeTailAsync(cancellationToken);

	private async Task PrimeTailAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT COALESCE(max(seq), 0) FROM job_events", connection);
		long tail = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		lock (_fanOutLock)
		{
			// THE invariant (PR #168 round 1, finding 1): once anyone is subscribed,
			// the tail advances only by fan-out delivery. A prime that raced a
			// registration (the query ran before the subscriber existed, the result
			// arrived after) would otherwise skip rows past every registered channel
			// -- a permanent, silent gap. With subscribers present, the very next
			// PollOnceAsync delivers those same rows through the fan-out instead.
			if (_lastDeliveredSeq < tail)
			{
				_lastDeliveredSeq = tail;
			}
		}
	}

	private async IAsyncEnumerable<StreamedJobEvent> QueryRangeAsync(
		JobEventStreamScope scope, long afterSeq, long upToSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT seq, event_type, job_id, run_id, payload::text, created_at
			FROM job_events
			WHERE seq > $1 AND seq <= $2 AND ($3::uuid IS NULL OR run_id = $3)
			ORDER BY seq
			""", connection);
		command.Parameters.AddWithValue(afterSeq);
		command.Parameters.AddWithValue(upToSeq);
		command.Parameters.AddWithValue((object?)scope.RunId ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			yield return new StreamedJobEvent(
				reader.GetInt64(0),
				reader.GetString(1),
				reader.IsDBNull(2) ? null : reader.GetGuid(2),
				reader.IsDBNull(3) ? null : reader.GetGuid(3),
				reader.GetString(4),
				reader.GetFieldValue<DateTimeOffset>(5));
		}
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "job_events tail poll failed; retrying next tick")]
	private partial void LogPollFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Event stream subscriber dropped at seq {LastQueuedSeq}: bounded queue overflowed (slow client); it must reconnect with Last-Event-ID to resync")]
	private partial void LogSubscriberDropped(long lastQueuedSeq);
}
