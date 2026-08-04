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
/// The volume path into <c>job_events</c>, for callers that emit many events fast --
/// PowerShell stream capture in epic #6 is the intended client, where every output
/// line of every parallel job is one <c>job.log</c> event. Enqueues never block and
/// never throw; a background flusher batches rows into single multi-row INSERTs so one
/// ordering-lock acquisition (see #117) amortizes across the whole batch. Use
/// <see cref="IJobEventPublisher"/> instead when an event should be durable *now*
/// (state transitions, halts): this buffer trades up to a flush interval of latency
/// and best-effort delivery for throughput.
/// </summary>
public interface IJobLogBuffer
{
	/// <summary>
	/// Queues one event for the next batch flush. Returns <c>false</c> when the buffer
	/// is full and the event was dropped (counted and logged by the implementation) --
	/// callers do not need to act on it, but tests do.
	/// </summary>
	bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson);
}
