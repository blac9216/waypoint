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

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>
/// The batching flusher behind <see cref="IJobLogBuffer"/>, implementing the #117
/// event-write budget: at most <see cref="JobEngineOptions.EventBatchMaxSize"/> rows
/// per INSERT, a flush cadence of <see cref="JobEngineOptions.EventFlushInterval"/>,
/// and nothing else in the transaction -- each flush is one autocommit multi-row
/// INSERT, so the <c>trg_job_events_assign_seq</c> ordering lock is taken once per
/// batch instead of once per event (#117 measured the per-event ceiling at ~900/s
/// regardless of writer count; amortization is the only lever that raises it).
///
/// Payloads pass through <see cref="ISecretRedactor"/> at enqueue time -- before the
/// event ever sits in the buffer -- so a secret that is untracked by flush time (its
/// job finished) is still redacted, and the Postgres sink stays covered by
/// security.md control 1 on this path exactly as <see cref="JobEventPublisher"/>
/// covers the single-row path.
///
/// Delivery is best-effort with bounded memory: a full buffer drops the *new* event
/// (never stalls the producer -- a PowerShell stream callback must not block on
/// Postgres), and a failed flush drops its batch, both counted and logged. Loss here
/// is acceptable for the same reason <see cref="JobEventPublisher"/> swallows
/// failures: <c>job_events</c> is observability, never the record of job/run state.
/// </summary>
public sealed partial class BufferedJobEventWriter : BackgroundService, IJobLogBuffer
{
	private readonly record struct PendingEvent(string EventType, Guid? JobId, Guid? RunId, string PayloadJson);

	private readonly string _connectionString;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<JobEngineOptions> _options;
	private readonly ILogger<BufferedJobEventWriter> _logger;
	private readonly Channel<PendingEvent> _buffer;
	private long _droppedFromFullBuffer;

	public BufferedJobEventWriter(
		string connectionString, ISecretRedactor redactor, IOptions<JobEngineOptions> options, ILogger<BufferedJobEventWriter> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_redactor = redactor;
		_options = options;
		_logger = logger;
		_buffer = Channel.CreateBounded<PendingEvent>(new BoundedChannelOptions(options.Value.EventBufferCapacity)
		{
			// Wait is never hit: TryEnqueue uses TryWrite and treats false as a drop.
			// DropWrite/DropOldest would drop silently inside the channel; doing it in
			// TryEnqueue keeps the loss counted and visible.
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true
		});
	}

	public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

		string payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : _redactor.Redact(payloadJson);
		if (_buffer.Writer.TryWrite(new PendingEvent(eventType, jobId, runId, payload)))
		{
			return true;
		}

		// Log the first drop of a full-buffer episode, then every 1000th, so a burst
		// that outruns Postgres for seconds does not itself flood the console sink.
		long dropped = Interlocked.Increment(ref _droppedFromFullBuffer);
		if (dropped == 1 || dropped % 1000 == 0)
		{
			LogBufferFull(eventType, dropped, _options.Value.EventBufferCapacity);
		}

		return false;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TimeSpan flushInterval = _options.Value.EventFlushInterval;
		using PeriodicTimer timer = new(flushInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
			{
				await FlushQueuedAsync(stoppingToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			// Host stopping; fall through to the final drain.
		}

		// Final drain so events emitted moments before shutdown are not lost with the
		// process. CancellationToken.None deliberately: stoppingToken is already
		// cancelled, and each batch INSERT still carries its own CommandTimeout, so
		// this cannot hang the host longer than one timeout per remaining batch.
		await FlushQueuedAsync(CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>Drains everything currently queued in batches of at most <see cref="JobEngineOptions.EventBatchMaxSize"/>.</summary>
	private async Task FlushQueuedAsync(CancellationToken cancellationToken)
	{
		int batchMaxSize = _options.Value.EventBatchMaxSize;
		List<PendingEvent> batch = new(batchMaxSize);

		while (true)
		{
			batch.Clear();
			while (batch.Count < batchMaxSize && _buffer.Reader.TryRead(out PendingEvent pending))
			{
				batch.Add(pending);
			}

			if (batch.Count == 0)
			{
				return;
			}

			await InsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task InsertBatchAsync(IReadOnlyList<PendingEvent> batch, CancellationToken cancellationToken)
	{
		Guid?[] runIds = new Guid?[batch.Count];
		Guid?[] jobIds = new Guid?[batch.Count];
		string[] eventTypes = new string[batch.Count];
		string[] payloads = new string[batch.Count];
		for (int index = 0; index < batch.Count; index++)
		{
			runIds[index] = batch[index].RunId;
			jobIds[index] = batch[index].JobId;
			eventTypes[index] = batch[index].EventType;
			payloads[index] = batch[index].PayloadJson;
		}

		try
		{
			await using NpgsqlConnection connection = new(_connectionString);
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

			// One statement in autocommit IS the transaction: the ordering trigger's
			// advisory lock is acquired once, held for exactly this INSERT, released at
			// its commit. unnest keeps row order, so seq order within the batch matches
			// enqueue order (and the #104 commit-order guarantee holds across batches).
			await using NpgsqlCommand command = new(
				"""
				INSERT INTO job_events (run_id, job_id, event_type, payload)
				SELECT run_id, job_id, event_type, payload::jsonb
				FROM unnest($1, $2, $3, $4) AS batch(run_id, job_id, event_type, payload)
				""", connection)
			{
				CommandTimeout = _options.Value.EventCommandTimeoutSeconds
			};
			command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = runIds });
			command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = jobIds });
			command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = eventTypes });
			command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = payloads });

			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (NpgsqlException exception)
		{
			// Same best-effort contract as JobEventPublisher, whose doc comment carries
			// the full open-vs-insert diagnosis rationale; at batch granularity the
			// actionable fact is simpler -- how many events just vanished.
			LogBatchFlushFailed(batch.Count, exception);
		}
	}

	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "job_events buffer full at {Capacity}: dropped event {EventType} ({DroppedTotal} dropped so far) -- Postgres is not keeping up or is unreachable")]
	private partial void LogBufferFull(string eventType, long droppedTotal, int capacity);

	[LoggerMessage(Level = LogLevel.Error, Message = "job_events batch flush of {BatchSize} event(s) failed; batch dropped")]
	private partial void LogBatchFlushFailed(int batchSize, Exception exception);
}
