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
/// Writes one row to <c>job_events</c>. Every implementation must honor the two costs
/// <c>0001_initial_schema.sql</c> documents on <c>trg_job_events_assign_seq</c> and
/// issue #117 measured: (1) "emit last, in a short transaction" -- callers emit *after*
/// their own state-changing write has already committed on its own connection, never
/// inside the same transaction, so the global ordering lock is never held across a row
/// lock; (2) the write carries its own short, explicitly chosen command timeout rather
/// than inheriting Npgsql's 30s default (see
/// <c>Waypoint.Runner.Jobs.JobEventPublisher</c> for the chosen budget and why).
/// </summary>
public interface IJobEventPublisher
{
	/// <summary>
	/// Emits one event. <paramref name="payloadJson"/> is a JSON object literal (already
	/// serialized -- this interface does not impose a payload shape). Failure to emit is
	/// logged by the implementation and does not throw: the <c>job_events</c> stream is
	/// best-effort observability, never the source of truth for job/run state, which is
	/// already durably committed by the time an event is emitted.
	/// </summary>
	Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken);
}
