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

using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Pure-logic tests for <see cref="RetainedContentStateTransitions"/> -- no Postgres
/// involved. Issue #1406 Acceptance Criteria: invalid transitions (e.g. <c>purged
/// -&gt; tracked</c>, or pinning already-<c>purged</c> content) are rejected at the
/// domain layer.
/// </summary>
public sealed class RetainedContentStateTests
{
	[Theory]
	[InlineData(RetainedContentStates.Tracked, RetainedContentStates.Grace)]
	[InlineData(RetainedContentStates.Tracked, RetainedContentStates.Pinned)]
	[InlineData(RetainedContentStates.Grace, RetainedContentStates.Tracked)]
	[InlineData(RetainedContentStates.Grace, RetainedContentStates.Pinned)]
	[InlineData(RetainedContentStates.Grace, RetainedContentStates.PendingPurge)]
	[InlineData(RetainedContentStates.Pinned, RetainedContentStates.Tracked)]
	[InlineData(RetainedContentStates.Pinned, RetainedContentStates.Grace)]
	[InlineData(RetainedContentStates.PendingPurge, RetainedContentStates.Purged)]
	[InlineData(RetainedContentStates.PendingPurge, RetainedContentStates.Pinned)]
	public void CanTransition_LegalMoves_ReturnsTrue(string from, string to)
	{
		Assert.True(RetainedContentStateTransitions.CanTransition(from, to));
	}

	[Theory]
	[InlineData(RetainedContentStates.Purged, RetainedContentStates.Tracked)]
	[InlineData(RetainedContentStates.Purged, RetainedContentStates.Grace)]
	[InlineData(RetainedContentStates.Purged, RetainedContentStates.Pinned)]
	[InlineData(RetainedContentStates.Purged, RetainedContentStates.PendingPurge)]
	[InlineData(RetainedContentStates.Tracked, RetainedContentStates.PendingPurge)]
	[InlineData(RetainedContentStates.Tracked, RetainedContentStates.Purged)]
	[InlineData(RetainedContentStates.Pinned, RetainedContentStates.PendingPurge)]
	[InlineData(RetainedContentStates.Pinned, RetainedContentStates.Purged)]
	public void CanTransition_IllegalMoves_ReturnsFalse(string from, string to)
	{
		Assert.False(RetainedContentStateTransitions.CanTransition(from, to));
	}

	[Fact]
	public void CanTransition_Purged_HasNoLegalNextState()
	{
		Assert.Empty(RetainedContentStateTransitions.AllowedNextStates(RetainedContentStates.Purged));
	}

	[Theory]
	[InlineData(RetainedContentStates.Tracked)]
	[InlineData(RetainedContentStates.Grace)]
	public void CanPin_FromTrackedOrGrace_ReturnsTrue(string from)
	{
		Assert.True(RetainedContentStateTransitions.CanPin(from));
	}

	[Theory]
	[InlineData(RetainedContentStates.Pinned)]
	[InlineData(RetainedContentStates.PendingPurge)]
	[InlineData(RetainedContentStates.Purged)]
	public void CanPin_FromPinnedPendingPurgeOrPurged_ReturnsFalse(string from)
	{
		Assert.False(RetainedContentStateTransitions.CanPin(from));
	}
}
