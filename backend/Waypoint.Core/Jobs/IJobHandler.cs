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
/// The seam a job type plugs into the dispatcher through. This issue (#5) builds the
/// dispatcher, the lease/heartbeat/recovery machinery, and the run/queue controls; it
/// deliberately implements zero handlers -- PowerShell runspace hosting (#6) and the
/// <c>catalog-index</c>/<c>download</c> job types (#8/#9) are non-goals here. Register
/// an <see cref="IJobHandler"/> per <see cref="JobType"/> in DI
/// (<c>services.AddSingleton&lt;IJobHandler, MyHandler&gt;()</c>) and
/// <c>Waypoint.Infrastructure.Jobs.JobHandlerRegistry</c> resolves it by the claimed
/// job's <c>job_type</c>. A claimed job whose type has no registered handler fails
/// immediately with a clear note rather than hanging -- see
/// <c>JobDispatcherHostedService</c>.
/// </summary>
public interface IJobHandler
{
	/// <summary>The <c>jobs.job_type</c> value this handler executes (must be one of <c>jobs_job_type_check</c>'s values).</summary>
	string JobType { get; }

	/// <summary>
	/// Executes the claimed job. Implementations must observe
	/// <paramref name="cancellationToken"/> promptly -- it is cancelled on run-abort
	/// (ADR-0008 "abort marks in-flight jobs... terminally") and there is no other way
	/// for the dispatcher to stop in-flight work it hosts in-process.
	/// </summary>
	Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}
