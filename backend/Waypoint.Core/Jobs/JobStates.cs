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
/// The exact string values of <c>jobs.state</c>, matching <c>jobs_state_check</c> in
/// <c>0001_initial_schema.sql</c> verbatim -- this is the closed set; nothing here may
/// diverge from that CHECK constraint or every write becomes a runtime constraint
/// violation instead of a compile-time typo.
/// </summary>
public static class JobStates
{
	public const string Queued = "queued";
	public const string Running = "running";
	public const string Attesting = "attesting";
	public const string Converting = "converting";
	public const string Uploaded = "uploaded";
	public const string Done = "done";
	public const string Failed = "failed";
	public const string AuthFailed = "auth-failed";
	public const string Blocked = "blocked";
	public const string Cancelled = "cancelled";

	/// <summary>
	/// Every value declared above, reflected rather than hand-typed a second time so
	/// this list cannot drift from the ten <c>public const string</c> fields it mirrors
	/// -- consumers that must enumerate the whole closed set (e.g.
	/// <see cref="JobCountBuckets"/>, issue #970) read this instead of retyping the
	/// literals.
	/// </summary>
	public static readonly IReadOnlyList<string> All = typeof(JobStates)
		.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
		.Where(f => f.IsLiteral && f.FieldType == typeof(string))
		.Select(f => (string)f.GetRawConstantValue()!)
		.OrderBy(s => s, StringComparer.Ordinal)
		.ToArray();
}

/// <summary>
/// Issue #406: the <see cref="JobStates"/> values with no further outgoing transition
/// in ANY shape's <see cref="JobStateMachine"/> table (<see cref="JobStates.Queued"/>,
/// <see cref="JobStates.Running"/>, <see cref="JobStates.Attesting"/>,
/// <see cref="JobStates.Converting"/> and <see cref="JobStates.Blocked"/> all still have
/// at least one outgoing edge in at least one shape, so none of them belong here).
/// This is the set a run's last job must land in before the run itself can transition
/// out of <c>running</c> -- see <c>JobQueueRepository.TryCompleteRunAsync</c>.
/// <see cref="JobStates.Uploaded"/> and <see cref="JobStates.Done"/> are the two
/// "success" terminals (map to <c>completed</c> when every job in a run lands on one of
/// them); <see cref="JobStates.Failed"/>, <see cref="JobStates.AuthFailed"/> and
/// <see cref="JobStates.Cancelled"/> are the "failure" terminals (any one of them among
/// a run's jobs maps the run to <c>completed_with_failures</c> per
/// docs/api-contract.md's state machine).
/// </summary>
public static class JobTerminalStates
{
	/// <summary>
	/// The five terminal states, ordered. Issue #1242: this is the vocabulary
	/// <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository.TryCompleteRunAsync"/>
	/// builds its "remaining work" FILTER predicate from. Issue #1293: adding a state
	/// here does NOT fail <c>jobs_state_check</c> -- the list is only interpolated into
	/// a <c>NOT IN (...)</c> FILTER predicate, which is a valid query against any text
	/// literal the schema has never heard of. What actually catches a spelling that
	/// <see cref="JobStates"/> does not declare is
	/// <c>Waypoint.Tests.Core.Jobs.JobTerminalStatesTests.TerminalStates_AreAllKnownJobStates</c>
	/// (asserts <see cref="All"/> is a subset of <see cref="JobStates.All"/>), backstopped
	/// by <c>Waypoint.Tests.Infrastructure.Postgres.JobsRunningRequiresLeaseTests.
	/// JobStatesConstants_ExactlyMatchTheSchemasAllowedStates</c> one hop further out
	/// (asserts <see cref="JobStates.All"/> itself matches <c>jobs_state_check</c>).
	/// Neither test enforces the OTHER direction: classifying a newly added
	/// <see cref="JobStates"/> value as terminal (adding it here) is still a human step
	/// no test requires -- an omission just leaves the new state "remaining forever" in
	/// <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository.TryCompleteRunAsync"/>
	/// and files it under <see cref="JobCountBucket.Running"/> in <see cref="JobCountBuckets"/>,
	/// silently, exactly as <see cref="JobStates.Blocked"/> does by design today.
	/// </summary>
	public static readonly IReadOnlyList<string> All =
	[
		JobStates.Uploaded, JobStates.Done, JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled,
	];

	/// <summary>
	/// The subset of <see cref="All"/> that mean "the job did not succeed", ordered.
	/// Issue #1242: <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository.TryCompleteRunAsync"/>
	/// builds its "any failure" FILTER predicate from this list.
	/// </summary>
	public static readonly IReadOnlyList<string> FailureStates =
	[
		JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled,
	];

	private static readonly HashSet<string> SuccessSet = new(StringComparer.Ordinal) { JobStates.Uploaded, JobStates.Done };
	private static readonly HashSet<string> FailureSet = new(FailureStates, StringComparer.Ordinal);
	private static readonly HashSet<string> AllSet = new(All, StringComparer.Ordinal);

	/// <summary>True for any of the five states a job never leaves.</summary>
	public static bool Contains(string state) => AllSet.Contains(state);

	/// <summary>True for the two "the job succeeded" terminals.</summary>
	public static bool IsSuccess(string state) => SuccessSet.Contains(state);

	/// <summary>True for the three "the job did not succeed" terminals.</summary>
	public static bool IsFailure(string state) => FailureSet.Contains(state);
}

/// <summary>
/// The <c>job_count_*</c> bucket (issue #970) that <c>GET /runs</c> and
/// <c>GET /runs/{id}</c> sort a job into, per docs/api-contract.md's run row. Every
/// value in <see cref="JobStates"/> resolves to exactly one bucket so
/// <c>sum(job_count_queued, job_count_running, job_count_completed, job_count_failed,
/// job_count_blocked) == job_count</c> always holds.
/// </summary>
public enum JobCountBucket
{
	Queued,
	Running,
	Completed,
	Failed,
	Blocked,
}

/// <summary>
/// Resolves each <see cref="JobStates"/> value to its <see cref="JobCountBucket"/>.
/// <see cref="JobStates.Queued"/> and <see cref="JobStates.Blocked"/> are their own
/// buckets; <see cref="JobTerminalStates.IsSuccess"/>/<see cref="JobTerminalStates.IsFailure"/>
/// (the existing terminal-state vocabulary) split the remaining terminals into
/// <see cref="JobCountBucket.Completed"/>/<see cref="JobCountBucket.Failed"/>; and
/// everything else -- <see cref="JobStates.Running"/>, <see cref="JobStates.Attesting"/>,
/// <see cref="JobStates.Converting"/> today -- is in-flight and falls into
/// <see cref="JobCountBucket.Running"/> by construction. Because every branch is
/// exhaustive over <see cref="JobStates.All"/> (pinned by
/// <c>JobCountBucketsTests.EveryJobState_MapsToExactlyOneBucket</c>), a job-state value
/// added to the CHECK constraint without an update here still lands in a bucket
/// instead of silently falling out of all of them -- worst case it is miscategorized
/// as <see cref="JobCountBucket.Running"/>, never uncounted.
/// </summary>
public static class JobCountBuckets
{
	public static JobCountBucket Resolve(string state) => state switch
	{
		JobStates.Queued => JobCountBucket.Queued,
		JobStates.Blocked => JobCountBucket.Blocked,
		_ when JobTerminalStates.IsSuccess(state) => JobCountBucket.Completed,
		_ when JobTerminalStates.IsFailure(state) => JobCountBucket.Failed,
		_ => JobCountBucket.Running,
	};

	/// <summary>Every <see cref="JobStates"/> value that resolves to <paramref name="bucket"/>.</summary>
	public static IReadOnlyList<string> StatesIn(JobCountBucket bucket) =>
		JobStates.All.Where(state => Resolve(state) == bucket).ToArray();
}
