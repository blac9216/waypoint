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
/// The exact string values of <c>job_events.event_type</c>, matching
/// <c>job_events_event_type_check</c> and <c>job_events_scope_check</c> in
/// <c>0001_initial_schema.sql</c>. Scope (which of job_id/run_id each type requires) is
/// documented per member; <see cref="Waypoint.Infrastructure.Jobs.JobEventPublisher"/>
/// does not re-validate it client-side -- the database CHECK is the enforcement point,
/// consistent with "an unclassified event type fails closed".
/// </summary>
public static class JobEventTypes
{
	/// <summary>Job-scoped (job_id required). A job's state transitioned.</summary>
	public const string JobState = "job.state";

	/// <summary>Job-scoped (job_id required). One log line, post-scrub.</summary>
	public const string JobLog = "job.log";

	/// <summary>Run-scoped (run_id required, job_id NULL). Aggregate run counts/percent.</summary>
	public const string RunProgress = "run.progress";

	/// <summary>
	/// Run-scoped (run_id required, job_id NULL). A run's priority queue changed halted
	/// state -- the consecutive-auth-failure halt (ADR-0008) and its resume both emit
	/// this.
	/// </summary>
	public const string QueueState = "queue.state";

	/// <summary>Job-scoped (job_id required). Download byte/rate/ETA progress.</summary>
	public const string DownloadProgress = "download.progress";

	/// <summary>Appliance-wide (job_id and run_id both NULL).</summary>
	public const string SystemNotice = "system.notice";
}
