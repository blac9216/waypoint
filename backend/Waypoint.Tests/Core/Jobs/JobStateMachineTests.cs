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

/// <summary>Pure-logic tests for <see cref="JobStateMachine"/> -- no Postgres involved.</summary>
public sealed class JobStateMachineTests
{
	[Theory]
	[InlineData(JobShape.Standard, JobStates.Queued, JobStates.Running)]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.Attesting)]
	[InlineData(JobShape.Standard, JobStates.Attesting, JobStates.Converting)]
	[InlineData(JobShape.Standard, JobStates.Converting, JobStates.Uploaded)]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.Failed)]
	[InlineData(JobShape.Standard, JobStates.Attesting, JobStates.Failed)]
	[InlineData(JobShape.Standard, JobStates.Converting, JobStates.Failed)]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.AuthFailed)]
	[InlineData(JobShape.Standard, JobStates.Queued, JobStates.Cancelled)]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.Cancelled)]
	[InlineData(JobShape.Standard, JobStates.Queued, JobStates.Blocked)]
	[InlineData(JobShape.Standard, JobStates.Blocked, JobStates.Queued)]
	[InlineData(JobShape.Simple, JobStates.Queued, JobStates.Running)]
	[InlineData(JobShape.Simple, JobStates.Running, JobStates.Done)]
	[InlineData(JobShape.Simple, JobStates.Running, JobStates.Failed)]
	[InlineData(JobShape.Simple, JobStates.Running, JobStates.AuthFailed)]
	[InlineData(JobShape.Simple, JobStates.Queued, JobStates.Cancelled)]
	[InlineData(JobShape.Simple, JobStates.Queued, JobStates.Blocked)]
	[InlineData(JobShape.Simple, JobStates.Blocked, JobStates.Queued)]
	[InlineData(JobShape.Srg, JobStates.Queued, JobStates.Running)]
	[InlineData(JobShape.Srg, JobStates.Running, JobStates.Attesting)]
	[InlineData(JobShape.Srg, JobStates.Attesting, JobStates.Done)]
	[InlineData(JobShape.Srg, JobStates.Running, JobStates.Failed)]
	[InlineData(JobShape.Srg, JobStates.Attesting, JobStates.Failed)]
	[InlineData(JobShape.Srg, JobStates.Running, JobStates.AuthFailed)]
	[InlineData(JobShape.Srg, JobStates.Queued, JobStates.Cancelled)]
	[InlineData(JobShape.Srg, JobStates.Running, JobStates.Cancelled)]
	[InlineData(JobShape.Srg, JobStates.Attesting, JobStates.Cancelled)]
	[InlineData(JobShape.Srg, JobStates.Queued, JobStates.Blocked)]
	[InlineData(JobShape.Srg, JobStates.Blocked, JobStates.Queued)]
	public void CanTransition_LegalMoves_ReturnsTrue(JobShape shape, string from, string to)
	{
		Assert.True(JobStateMachine.CanTransition(shape, from, to));
	}

	[Theory]
	[InlineData(JobShape.Standard, JobStates.Queued, JobStates.Attesting)]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.Uploaded)]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.Converting)]
	[InlineData(JobShape.Standard, JobStates.Uploaded, JobStates.Running)]
	[InlineData(JobShape.Standard, JobStates.Failed, JobStates.Queued)]
	[InlineData(JobShape.Standard, JobStates.Cancelled, JobStates.Queued)]
	[InlineData(JobShape.Simple, JobStates.Queued, JobStates.Done)]
	[InlineData(JobShape.Simple, JobStates.Done, JobStates.Running)]
	[InlineData(JobShape.Simple, JobStates.Running, JobStates.Attesting)]
	[InlineData(JobShape.Simple, JobStates.Failed, JobStates.Queued)]
	[InlineData(JobShape.Srg, JobStates.Queued, JobStates.Attesting)]
	[InlineData(JobShape.Srg, JobStates.Running, JobStates.Done)]
	[InlineData(JobShape.Srg, JobStates.Running, JobStates.Converting)]
	[InlineData(JobShape.Srg, JobStates.Attesting, JobStates.Converting)]
	[InlineData(JobShape.Srg, JobStates.Attesting, JobStates.Uploaded)]
	[InlineData(JobShape.Srg, JobStates.Done, JobStates.Running)]
	[InlineData(JobShape.Srg, JobStates.Failed, JobStates.Queued)]
	public void CanTransition_IllegalMoves_ReturnsFalse(JobShape shape, string from, string to)
	{
		Assert.False(JobStateMachine.CanTransition(shape, from, to));
	}

	[Theory]
	[InlineData(JobShape.Standard, JobStates.Uploaded)]
	[InlineData(JobShape.Standard, JobStates.Failed)]
	[InlineData(JobShape.Standard, JobStates.AuthFailed)]
	[InlineData(JobShape.Standard, JobStates.Cancelled)]
	[InlineData(JobShape.Simple, JobStates.Done)]
	[InlineData(JobShape.Simple, JobStates.Failed)]
	[InlineData(JobShape.Simple, JobStates.AuthFailed)]
	[InlineData(JobShape.Simple, JobStates.Cancelled)]
	[InlineData(JobShape.Srg, JobStates.Done)]
	[InlineData(JobShape.Srg, JobStates.Failed)]
	[InlineData(JobShape.Srg, JobStates.AuthFailed)]
	[InlineData(JobShape.Srg, JobStates.Cancelled)]
	public void TerminalStates_HaveNoAllowedNextStates(JobShape shape, string terminalState)
	{
		Assert.Empty(JobStateMachine.AllowedNextStates(shape, terminalState));
	}

	[Theory]
	[InlineData(JobShape.Standard)]
	[InlineData(JobShape.Simple)]
	[InlineData(JobShape.Srg)]
	public void EngineMayRequeueRunningJobs_ButHandlersMayNot(JobShape shape)
	{
		Assert.True(JobStateMachine.CanEngineTransition(shape, JobStates.Running, JobStates.Queued));
		Assert.False(JobStateMachine.CanTransition(shape, JobStates.Running, JobStates.Queued));
	}

	[Theory]
	[InlineData(JobShape.Standard, JobStates.Running, JobStates.Cancelled)]
	[InlineData(JobShape.Simple, JobStates.Running, JobStates.Failed)]
	[InlineData(JobShape.Standard, JobStates.Queued, JobStates.Blocked)]
	[InlineData(JobShape.Srg, JobStates.Attesting, JobStates.Done)]
	public void EngineAlsoRetainsPipelineTransitions(JobShape shape, string from, string to)
	{
		Assert.True(JobStateMachine.CanEngineTransition(shape, from, to));
	}

	/// <summary>
	/// Issue #293: a stage-complete requeue (or #282's lease-recovery sweep) must be
	/// able to move a Standard-shape job resting mid-pipeline -- attesting or
	/// converting -- straight back to queued, the same engine-only privilege
	/// <see cref="JobStates.Running"/> already had. No handler may do this itself
	/// (<see cref="JobStateMachine.CanTransition"/> stays false for both).
	/// </summary>
	[Theory]
	[InlineData(JobStates.Attesting)]
	[InlineData(JobStates.Converting)]
	public void EngineMayRequeueAttestingOrConvertingJobs_ButHandlersMayNot(string fromState)
	{
		Assert.True(JobStateMachine.CanEngineTransition(JobShape.Standard, fromState, JobStates.Queued));
		Assert.False(JobStateMachine.CanTransition(JobShape.Standard, fromState, JobStates.Queued));
	}

	[Fact]
	public void JobShapes_OnlyScanIsStandard()
	{
		Assert.Equal(JobShape.Standard, JobShapes.ForJobType("scan"));

		foreach (string otherType in new[]
		{
			"remediate", "discover", "download", "catalog-index",
			"bundle-export", "bundle-import", "content-library-sync", "content-pull", "content-import", "update"
		})
		{
			Assert.Equal(JobShape.Simple, JobShapes.ForJobType(otherType));
		}
	}

	/// <summary>
	/// A state added to <see cref="JobStates"/> but forgotten in
	/// <see cref="JobStateMachine"/>'s tables is silently unreachable: every
	/// <c>CanTransition</c> into or out of it returns false, so the dispatcher would
	/// reject the transition and force the job to <c>failed</c> with no hint why. Pin
	/// the two vocabularies together instead.
	/// </summary>
	[Theory]
	[InlineData(JobShape.Standard)]
	[InlineData(JobShape.Simple)]
	[InlineData(JobShape.Srg)]
	public void EveryJobState_IsKnownToItsShapeOrDeliberatelyAbsent(JobShape shape)
	{
		string[] notInThisShape = shape switch
		{
			JobShape.Standard => [JobStates.Done],
			JobShape.Simple => [JobStates.Attesting, JobStates.Converting, JobStates.Uploaded],
			_ => [JobStates.Converting, JobStates.Uploaded]
		};

		foreach (string state in AllJobStates())
		{
			bool known = JobStateMachine.AllowedNextStates(shape, state).Count > 0
				|| AllJobStates().Any(other => JobStateMachine.CanTransition(shape, other, state));

			if (notInThisShape.Contains(state, StringComparer.Ordinal))
			{
				Assert.False(known, $"'{state}' should not appear in the {shape} table.");
			}
			else
			{
				Assert.True(known, $"'{state}' is in JobStates but unreachable in the {shape} table.");
			}
		}
	}

	[Theory]
	[InlineData(JobShape.Standard)]
	[InlineData(JobShape.Simple)]
	[InlineData(JobShape.Srg)]
	public void CanTransition_UnknownFromState_ReturnsFalse(JobShape shape)
	{
		Assert.False(JobStateMachine.CanTransition(shape, "not-a-state", JobStates.Running));
		Assert.Empty(JobStateMachine.AllowedNextStates(shape, "not-a-state"));
	}

	[Fact]
	public void CanTransition_BlankStates_Throw()
	{
		Assert.Throws<ArgumentException>(() => JobStateMachine.CanTransition(JobShape.Simple, " ", JobStates.Running));
		Assert.Throws<ArgumentException>(() => JobStateMachine.CanTransition(JobShape.Simple, JobStates.Queued, " "));
		Assert.Throws<ArgumentException>(() => JobStateMachine.AllowedNextStates(JobShape.Simple, " "));
	}

	private static string[] AllJobStates() =>
	[
		JobStates.Queued,
		JobStates.Running,
		JobStates.Attesting,
		JobStates.Converting,
		JobStates.Uploaded,
		JobStates.Done,
		JobStates.Failed,
		JobStates.AuthFailed,
		JobStates.Blocked,
		JobStates.Cancelled
	];
}
