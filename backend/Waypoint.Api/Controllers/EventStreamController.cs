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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Core.Authorization;
using Waypoint.Core.Jobs;
using Waypoint.Core.Serialization;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md SSE surface (epic #7 slice 2): <c>/api/v1/events</c> (global)
/// and <c>/api/v1/runs/{id}/events</c> (per-run), over the slice-1
/// <see cref="IJobEventFeed"/>. SSE framing: <c>id:</c> carries <c>seq</c> (that is
/// what the browser echoes back as <c>Last-Event-ID</c>), <c>event:</c> carries the
/// event type, <c>data:</c> carries the contract envelope. A heartbeat comment goes
/// out every <see cref="HeartbeatInterval"/> of silence so proxies and clients can
/// distinguish a quiet stream from a dead one.
///
/// Backpressure: when the feed drops this subscriber
/// (<see cref="JobEventStreamOverflowException"/>), the stream ends after an
/// <c>event: resync</c> frame carrying the last delivered seq -- the standard SSE
/// client then reconnects with <c>Last-Event-ID</c> and replays the gap. A fault
/// after the first byte follows the #164 semantics (abort, never a rewritten
/// response).
/// </summary>
[ApiController]
public sealed class EventStreamController : ControllerBase
{
	private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

	private readonly IJobEventFeed _feed;

	public EventStreamController(IJobEventFeed feed)
	{
		ArgumentNullException.ThrowIfNull(feed);
		_feed = feed;
	}

	[HttpGet("api/v1/events")]
	[RequireViewerRole]
	public Task GlobalStream(CancellationToken cancellationToken)
	{
		return StreamAsync(JobEventStreamScope.Global, cancellationToken);
	}

	[HttpGet("api/v1/runs/{id:guid}/events")]
	[RequireViewerRole]
	public async Task RunStream(Guid id, CancellationToken cancellationToken)
	{
		// 404 for a run that does not exist -- before the response starts, while an
		// envelope can still be written. A run with no events yet is NOT a 404; the
		// stream simply opens quiet.
		if (!await RunExistsAsync(id, cancellationToken).ConfigureAwait(false))
		{
			await Middleware.ErrorEnvelopeWriter.WriteAsync(
				HttpContext, System.Net.HttpStatusCode.NotFound, Middleware.ErrorEnvelopeWriter.ForStatusCode(404)).ConfigureAwait(false);
			return;
		}

		await StreamAsync(JobEventStreamScope.ForRun(id), cancellationToken).ConfigureAwait(false);
	}

	private async Task StreamAsync(JobEventStreamScope scope, CancellationToken cancellationToken)
	{
		long? afterSeq = ParseLastEventId();

		Response.StatusCode = StatusCodes.Status200OK;
		Response.ContentType = "text/event-stream";
		Response.Headers.CacheControl = "no-cache";
		// ADR-0003: nginx disables buffering for SSE locations; this header makes the
		// intent explicit to any intermediary that honors it.
		Response.Headers["X-Accel-Buffering"] = "no";
		await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

		long lastDelivered = afterSeq ?? -1;
		IAsyncEnumerator<StreamedJobEvent> events = _feed.ReadAsync(scope, afterSeq, cancellationToken).GetAsyncEnumerator(cancellationToken);
		try
		{
			while (true)
			{
				// Heartbeat while idle: race the next event against the interval so a
				// quiet stream still proves liveness through every proxy hop.
				Task<bool> moveNext = events.MoveNextAsync().AsTask();
				while (await Task.WhenAny(moveNext, Task.Delay(HeartbeatInterval, cancellationToken)).ConfigureAwait(false) != moveNext)
				{
					await WriteRawAsync(": heartbeat\n\n", cancellationToken).ConfigureAwait(false);
				}

				if (!await moveNext.ConfigureAwait(false))
				{
					return;
				}

				StreamedJobEvent row = events.Current;
				await WriteEventAsync(row, cancellationToken).ConfigureAwait(false);
				lastDelivered = row.Seq;
			}
		}
		catch (JobEventStreamOverflowException overflow)
		{
			// The documented drop+resync path: tell the client where to resume, end
			// the stream; EventSource reconnects with Last-Event-ID automatically.
			string payload = JsonSerializer.Serialize(
				new { last_seq = Math.Max(overflow.LastDeliveredSeq, lastDelivered) }, WaypointJsonOptions.Default);
			await WriteRawAsync($"event: resync\ndata: {payload}\n\n", CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Client went away; nothing to write.
		}
		finally
		{
			await events.DisposeAsync().ConfigureAwait(false);
		}
	}

	private long? ParseLastEventId()
	{
		string? headerValue = Request.Query["lastEventId"].FirstOrDefault();
		return long.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq) && seq >= 0
			? seq
			: null;
	}

	private async Task WriteEventAsync(StreamedJobEvent row, CancellationToken cancellationToken)
	{
		// The contract envelope: { seq, ts, type, run_id, job_id, data }. The payload
		// column is already-serialized JSON (and already redacted at write time), so
		// it is embedded as-is rather than re-serialized.
		string envelope = string.Create(CultureInfo.InvariantCulture,
			$"{{\"seq\":{row.Seq},\"ts\":\"{row.CreatedAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\",\"type\":{JsonSerializer.Serialize(row.EventType)},\"run_id\":{JsonSerializer.Serialize(row.RunId)},\"job_id\":{JsonSerializer.Serialize(row.JobId)},\"data\":{row.PayloadJson}}}");
		await WriteRawAsync(
			string.Create(CultureInfo.InvariantCulture, $"id: {row.Seq}\nevent: {row.EventType}\ndata: {envelope}\n\n"),
			cancellationToken).ConfigureAwait(false);
	}

	private async Task WriteRawAsync(string frame, CancellationToken cancellationToken)
	{
		await Response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
		await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task<bool> RunExistsAsync(Guid runId, CancellationToken cancellationToken)
	{
		IJobQueueRepository repository = HttpContext.RequestServices.GetRequiredService<IJobQueueRepository>();
		return await repository.GetRunQueueStateAsync(runId, cancellationToken).ConfigureAwait(false) is not null;
	}
}
