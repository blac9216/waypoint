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
/// Pure-logic tests for <see cref="JobShapes"/> -- no Postgres involved. Issue #309:
/// <see cref="JobShapes.ForJob"/> is the shape-routing entry point the dispatcher now
/// calls for every claimed job; it must route an ssh-kind scan to <see cref="JobShape.Srg"/>
/// while leaving every other scan (and every non-scan job type) exactly where
/// <see cref="JobShapes.ForJobType"/> already put it.
/// </summary>
public sealed class JobShapesTests
{
	[Fact]
	public void ForJob_ScanWithSshTargetKind_ReturnsSrg()
	{
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"ssh"}""";
		Assert.Equal(JobShape.Srg, JobShapes.ForJob("scan", payload));
	}

	[Theory]
	[InlineData("vsphere")]
	[InlineData("nsx-api")]
	public void ForJob_ScanWithNonSshTargetKind_ReturnsStandard(string kind)
	{
		string payload = $$"""{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"{{kind}}"}""";
		Assert.Equal(JobShape.Standard, JobShapes.ForJob("scan", payload));
	}

	[Fact]
	public void ForJob_ScanWithNoTargetKindInPayload_DegradesToStandard()
	{
		// Pre-#309 payload shape (no target_kind field at all) -- must not throw, and
		// must match ForJobType's prior behavior exactly.
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111"}""";
		Assert.Equal(JobShape.Standard, JobShapes.ForJob("scan", payload));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{}")]
	[InlineData("not-json")]
	[InlineData("{\"target_kind\":123}")]
	public void ForJob_ScanWithMalformedOrEmptyPayload_DegradesToStandard_NeverThrows(string payload)
	{
		Assert.Equal(JobShape.Standard, JobShapes.ForJob("scan", payload));
	}

	[Theory]
	[InlineData("download")]
	[InlineData("discover")]
	[InlineData("catalog-index")]
	public void ForJob_NonScanJobType_ReturnsSimple_RegardlessOfPayload(string jobType)
	{
		string payload = """{"target_kind":"ssh"}""";
		Assert.Equal(JobShape.Simple, JobShapes.ForJob(jobType, payload));
	}

	[Fact]
	public void ForJobType_StillReturnsStandardForScan_UnawareOfTargetKind()
	{
		// ForJobType is retained as the job_type-only rule -- proves it was not silently
		// repointed at ForJob's payload-aware behavior.
		Assert.Equal(JobShape.Standard, JobShapes.ForJobType("scan"));
	}
}
