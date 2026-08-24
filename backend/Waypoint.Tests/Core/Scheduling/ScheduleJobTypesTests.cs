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

using Waypoint.Core.Scheduling;
using Xunit;

namespace Waypoint.Tests.Core.Scheduling;

/// <summary>
/// Issue #594 (epic #577): the closed-set-level assertion backing
/// <c>SchedulesEndpointTests.Create_WithNonReadOnlyJobType_Returns400("purge")</c> --
/// proves at the domain-model layer, not just through one HTTP round trip, that
/// <c>purge</c> can never be scheduled. Mirrors <c>remediate</c>'s exclusion: both are
/// destructive/explicit-confirmation-gated operations that must stay one-shot and
/// operator-initiated.
/// </summary>
public sealed class ScheduleJobTypesTests
{
	[Fact]
	public void All_NeverContainsPurge()
	{
		Assert.DoesNotContain("purge", ScheduleJobTypes.All);
	}

	[Fact]
	public void All_NeverContainsRemediate()
	{
		// Existing guarantee (CLAUDE.md "remediation is never schedulable"), asserted
		// here alongside purge's so both destructive-operation exclusions are proven
		// by the same closed-set check rather than only remediate having one.
		Assert.DoesNotContain("remediate", ScheduleJobTypes.All);
	}
}
