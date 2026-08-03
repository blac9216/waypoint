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
	public void TerminalStates_HaveNoAllowedNextStates(JobShape shape, string terminalState)
	{
		Assert.Empty(JobStateMachine.AllowedNextStates(shape, terminalState));
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
}
