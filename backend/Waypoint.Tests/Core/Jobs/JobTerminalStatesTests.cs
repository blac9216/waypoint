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

using Waypoint.Core.Jobs;
using Xunit;

namespace Waypoint.Tests.Core.Jobs;

/// <summary>
/// Issue #1242: <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository.TryCompleteRunAsync"/>
/// now builds its "remaining work" and "any failure" FILTER predicates from
/// <see cref="JobTerminalStates.All"/>/<see cref="JobTerminalStates.FailureStates"/>
/// instead of hand-typed SQL literal lists, so those predicates can no longer drift
/// from <see cref="JobTerminalStates.Contains"/>/<see cref="JobTerminalStates.IsFailure"/>
/// by construction. This is a pure drift test -- no Postgres involved -- that pins the
/// internal consistency those two lists must have for that construction to hold: every
/// <see cref="JobStates"/> value is either terminal or not by exactly one of
/// <see cref="JobTerminalStates.IsSuccess"/>/<see cref="JobTerminalStates.IsFailure"/>
/// (never both, and the two together are exactly <see cref="JobTerminalStates.All"/>,
/// so no state can be "remaining forever" the way issue #970 actually manifested for
/// the job-count buckets), <see cref="JobTerminalStates.FailureStates"/> is a subset of
/// <see cref="JobTerminalStates.All"/>, and both lists are duplicate-free (a duplicate
/// would silently double-count nothing here, but would signal the list was hand-edited
/// rather than generated). The existing Postgres run-completion tests in
/// <c>JobQueueRepositoryTests</c> exercise the generated SQL itself.
/// </summary>
public sealed class JobTerminalStatesTests
{
	[Fact]
	public void EveryJobState_IsTerminalIffSuccessOrFailure()
	{
		foreach (string state in JobStates.All)
		{
			bool isSuccess = JobTerminalStates.IsSuccess(state);
			bool isFailure = JobTerminalStates.IsFailure(state);

			// Success and failure never overlap for the same state...
			Assert.False(isSuccess && isFailure, $"job state '{state}' is both a success and a failure terminal");

			// ...and together they are exactly the terminal set -- no state is
			// "terminal" without being one or the other, and no non-terminal state is
			// miscounted as success or failure.
			Assert.Equal(JobTerminalStates.Contains(state), isSuccess || isFailure);
		}
	}

	[Fact]
	public void FailureStates_IsASubsetOfAll()
	{
		foreach (string state in JobTerminalStates.FailureStates)
		{
			Assert.Contains(state, JobTerminalStates.All);
		}
	}

	[Fact]
	public void TerminalStates_AreAllKnownJobStates()
	{
		// Guards against JobTerminalStates.All/FailureStates drifting to reference a
		// state spelling that JobStates itself no longer declares.
		foreach (string state in JobTerminalStates.All)
		{
			Assert.Contains(state, JobStates.All);
		}
	}

	[Fact]
	public void All_AndFailureStates_HaveNoDuplicates()
	{
		Assert.Equal(JobTerminalStates.All.Count, JobTerminalStates.All.Distinct(StringComparer.Ordinal).Count());
		Assert.Equal(JobTerminalStates.FailureStates.Count, JobTerminalStates.FailureStates.Distinct(StringComparer.Ordinal).Count());
	}
}
