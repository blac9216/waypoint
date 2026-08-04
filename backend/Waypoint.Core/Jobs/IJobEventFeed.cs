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

namespace Waypoint.Core.Jobs;

/// <summary>
/// The replay-and-live event feed both SSE endpoints (#7) consume. Enumeration
/// yields every <c>job_events</c> row in the scope with <c>seq</c> greater than
/// <paramref name="afterSeq"/> exactly once, in <c>seq</c> order, first from the
/// table (catch-up) and then live as rows commit -- the #104 commit-order guarantee
/// is what makes that seam exact. A <c>null</c> <paramref name="afterSeq"/> starts
/// at the current tail: live only, no replay.
///
/// Backpressure contract (#7): each subscriber owns a bounded queue. A subscriber
/// that cannot keep up is terminated with
/// <see cref="JobEventStreamOverflowException"/> -- it reconnects with its last seen
/// seq (SSE <c>Last-Event-ID</c>) and replays the gap -- rather than ever stalling
/// the shared poller or the engine.
/// </summary>
public interface IJobEventFeed
{
	IAsyncEnumerable<StreamedJobEvent> ReadAsync(JobEventStreamScope scope, long? afterSeq, CancellationToken cancellationToken);
}

/// <summary>Global (<see cref="RunId"/> null) or scoped to one run's events per the schema's scope tiers.</summary>
public sealed record JobEventStreamScope(Guid? RunId)
{
	public static readonly JobEventStreamScope Global = new((Guid?)null);

	public static JobEventStreamScope ForRun(Guid runId) => new(runId);
}

/// <summary>One <c>job_events</c> row as streamed to a client.</summary>
public sealed record StreamedJobEvent(long Seq, string EventType, Guid? JobId, Guid? RunId, string PayloadJson, DateTimeOffset CreatedAt);

/// <summary>
/// This subscriber fell behind its bounded queue and was dropped (the #7 drop+resync
/// strategy). The client reconnects with the last seq it processed; nothing was lost
/// from the table, only from this subscriber's live view.
/// </summary>
public sealed class JobEventStreamOverflowException : Exception
{
	public JobEventStreamOverflowException(long lastDeliveredSeq)
		: base($"Event stream subscriber overflowed its queue; reconnect with Last-Event-ID {lastDeliveredSeq} to resync.")
	{
		LastDeliveredSeq = lastDeliveredSeq;
	}

	/// <summary>The last seq successfully queued to this subscriber before the drop.</summary>
	public long LastDeliveredSeq { get; }
}
