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
/// through the dispatcher. Three shapes share the table (see <see cref="JobShape"/>);
/// <see cref="Waypoint.Core.Jobs.JobStates.Cancelled"/> is reachable from every active
/// pipeline state; <see cref="Waypoint.Core.Jobs.JobStates.Blocked"/> is reachable only
/// from <c>queued</c>. Engine actors have a distinct graph because recovery and claim
/// release may requeue <c>running</c> work, while handlers must never do so and bypass
/// retry accounting.
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

	private static readonly IReadOnlyDictionary<string, string[]> SrgTransitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
	{
		[JobStates.Queued] = [JobStates.Running, JobStates.Cancelled, JobStates.Blocked],
		[JobStates.Running] = [JobStates.Attesting, JobStates.Failed, JobStates.AuthFailed, JobStates.Cancelled],
		[JobStates.Attesting] = [JobStates.Done, JobStates.Failed, JobStates.Cancelled],
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

		IReadOnlyDictionary<string, string[]> table = TransitionsFor(shape);

		return table.TryGetValue(fromState, out string[]? allowed) && Array.IndexOf(allowed, toState) >= 0;
	}

	/// <summary>
	/// True when an engine actor may perform the transition. Beyond the handler-legal
	/// graph, an engine actor (lease recovery, claim release, or issue #293's
	/// stage-complete requeue) may always move an active, lease-bearing state --
	/// <c>running</c>, or <c>attesting</c>/<c>converting</c> for a <see cref="JobShape.Standard"/>/
	/// <see cref="JobShape.Srg"/> job resting mid-pipeline -- back to <c>queued</c>.
	/// <c>queued</c> stays the only claimable/unleased state (the #274 ruling): this is
	/// what lets a job requeued mid-pipeline re-enter the claim query at all, and what
	/// makes #282's crashed-attest/convert recovery legal rather than a silent CHECK
	/// violation waiting to happen.
	/// </summary>
	public static bool CanEngineTransition(JobShape shape, string fromState, string toState) =>
		CanTransition(shape, fromState, toState)
		|| (string.Equals(toState, JobStates.Queued, StringComparison.Ordinal)
			&& (string.Equals(fromState, JobStates.Running, StringComparison.Ordinal)
				|| string.Equals(fromState, JobStates.Attesting, StringComparison.Ordinal)
				|| string.Equals(fromState, JobStates.Converting, StringComparison.Ordinal)));

	/// <summary>The states a job of the given shape may still reach from <paramref name="fromState"/>.</summary>
	public static IReadOnlyList<string> AllowedNextStates(JobShape shape, string fromState)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromState);

		IReadOnlyDictionary<string, string[]> table = TransitionsFor(shape);

		return table.TryGetValue(fromState, out string[]? allowed) ? allowed : [];
	}

	private static IReadOnlyDictionary<string, string[]> TransitionsFor(JobShape shape) => shape switch
	{
		JobShape.Standard => StandardTransitions,
		JobShape.Srg => SrgTransitions,
		_ => SimpleTransitions
	};
}
