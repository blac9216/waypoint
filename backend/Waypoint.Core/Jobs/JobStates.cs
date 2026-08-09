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
	private static readonly HashSet<string> Success = new(StringComparer.Ordinal) { JobStates.Uploaded, JobStates.Done };
	private static readonly HashSet<string> Failure = new(StringComparer.Ordinal) { JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled };
	private static readonly HashSet<string> All = new(StringComparer.Ordinal) { JobStates.Uploaded, JobStates.Done, JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled };

	/// <summary>True for any of the five states a job never leaves.</summary>
	public static bool Contains(string state) => All.Contains(state);

	/// <summary>True for the two "the job succeeded" terminals.</summary>
	public static bool IsSuccess(string state) => Success.Contains(state);

	/// <summary>True for the three "the job did not succeed" terminals.</summary>
	public static bool IsFailure(string state) => Failure.Contains(state);
}
