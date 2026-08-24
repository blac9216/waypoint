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
/// documented per member; <see cref="Waypoint.Runner.Jobs.JobEventPublisher"/>
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

	/// <summary>Job-scoped (job_id required). A <c>discover</c> job's item-count progress (issue #21, migration 0011).</summary>
	public const string DiscoverProgress = "discover.progress";

	/// <summary>Appliance-wide (job_id and run_id both NULL).</summary>
	public const string SystemNotice = "system.notice";

	/// <summary>
	/// Every value the schema's <c>job_events_event_type_check</c> CHECK constraint
	/// allows. Issue #581's bounded history read validates its <c>kind</c> filter
	/// against this set client-side (400 on an unknown kind) rather than letting a
	/// typo silently match zero rows and look like an empty history.
	/// </summary>
	public static readonly IReadOnlyList<string> All =
	[
		JobState, JobLog, RunProgress, QueueState, DownloadProgress, DiscoverProgress, SystemNotice,
	];

	public static bool IsValid(string eventType) => All.Contains(eventType, StringComparer.Ordinal);
}

/// <summary>
/// The closed set of <c>job.log</c> payload <c>severity</c> values PowerShell stream
/// capture emits (<c>PowerShellExecutor.WireStreamCapture</c>: Information/Warning/
/// Error/Verbose/Debug streams, one severity string each). Issue #581's history read
/// validates its <c>level</c> filter against this set (400 on an unknown value).
/// </summary>
public static class JobLogSeverities
{
	public const string Information = "information";
	public const string Warning = "warning";
	public const string Error = "error";
	public const string Verbose = "verbose";
	public const string Debug = "debug";

	public static readonly IReadOnlyList<string> All = [Information, Warning, Error, Verbose, Debug];

	public static bool IsValid(string severity) => All.Contains(severity, StringComparer.Ordinal);
}
