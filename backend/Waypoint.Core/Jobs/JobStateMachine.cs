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
/// The <c>jobs.state</c> transition graph from <c>docs/api-contract.md</c> "State
/// machines", as an explicit, testable table rather than scattered <c>if</c> checks
/// through the dispatcher. Two shapes share the table (see <see cref="JobShape"/>);
/// <see cref="Waypoint.Core.Jobs.JobStates.Blocked"/> and
/// <see cref="Waypoint.Core.Jobs.JobStates.Cancelled"/> are reachable from every active
/// state regardless of shape (the auth-failure halt and run-abort are shape-agnostic
/// engine operations, not pipeline stages).
///
/// This table is the pipeline vocabulary only; it does not talk to Postgres and, as of
/// this slice, nothing consults it before writing. An earlier revision of this comment
/// said "a transition it rejects never reaches a SQL statement" -- true only while there
/// were no writers at all, and #128's <see cref="Waypoint.Core.Jobs.IJobQueueRepository.ReleaseClaimAsync"/>
/// makes it false: releasing an unexecuted claim writes <c>running</c> -&gt;
/// <c>queued</c>, which <see cref="CanTransition"/> rejects in both shapes because
/// requeueing is an engine operation rather than a pipeline stage. That write is
/// correct; the sentence was not. Separating engine transitions from pipeline ones is
/// designed on #135 and owned by #129 -- it is a recorded plan, not something that ships
/// here. Until it does, treat this type as a reference table, not an enforced gate.
/// </summary>
public static class JobStateMachine
{
	private static readonly IReadOnlyDictionary<string, string[]> StandardTransitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
	{
		[JobStates.Queued] = [JobStates.Running, JobStates.Cancelled, JobStates.Blocked],
		[JobStates.Running] = [JobStates.Attesting, JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled],
		[JobStates.Attesting] = [JobStates.Converting, JobStates.Failed, JobStates.Cancelled],
		[JobStates.Converting] = [JobStates.Uploaded, JobStates.Failed, JobStates.Cancelled],
		[JobStates.Blocked] = [JobStates.Queued],
		[JobStates.Uploaded] = [],
		[JobStates.Failed] = [],
		[JobStates.AuthFailed] = [],
		[JobStates.Cancelled] = []
	};

	private static readonly IReadOnlyDictionary<string, string[]> SimpleTransitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
	{
		[JobStates.Queued] = [JobStates.Running, JobStates.Cancelled, JobStates.Blocked],
		[JobStates.Running] = [JobStates.Done, JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled],
		[JobStates.Blocked] = [JobStates.Queued],
		[JobStates.Done] = [],
		[JobStates.Failed] = [],
		[JobStates.AuthFailed] = [],
		[JobStates.Cancelled] = []
	};

	/// <summary>True if <paramref name="fromState"/> -&gt; <paramref name="toState"/> is a legal transition for <paramref name="shape"/>.</summary>
	public static bool CanTransition(JobShape shape, string fromState, string toState)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromState);
		ArgumentException.ThrowIfNullOrWhiteSpace(toState);

		IReadOnlyDictionary<string, string[]> table = shape == JobShape.Standard ? StandardTransitions : SimpleTransitions;

		return table.TryGetValue(fromState, out string[]? allowed) && Array.IndexOf(allowed, toState) >= 0;
	}

	/// <summary>The states a job of the given shape may still reach from <paramref name="fromState"/>.</summary>
	public static IReadOnlyList<string> AllowedNextStates(JobShape shape, string fromState)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromState);

		IReadOnlyDictionary<string, string[]> table = shape == JobShape.Standard ? StandardTransitions : SimpleTransitions;

		return table.TryGetValue(fromState, out string[]? allowed) ? allowed : [];
	}
}
