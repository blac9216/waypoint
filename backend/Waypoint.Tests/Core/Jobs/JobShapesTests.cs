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

	// Issue #741/#743: output_kind (a narrowed plan item's frozen catalog kind) is now
	// the PRIMARY routing signal -- test-pinned to prove it is read BY CATALOG KIND, not
	// inferred from target_kind, per #743's AC.

	[Fact]
	public void ForJob_VcsaServiceStigItemOnVsphereTarget_ReturnsStandard_NotSrg()
	{
		// The critical case #741 exists to fix: a VCSA STIG service item's OWNING TARGET
		// is vsphere-kind (not ssh-kind), and the pre-#741 target_kind-only inference
		// would have wrongly routed a vsphere target to Standard regardless -- but this
		// proves the NEW output_kind-first read reaches the same correct answer for the
		// right reason (catalog kind hdf_ckl), not by accident of target_kind agreeing.
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"vsphere","transport":"ssh","selector_kind":"service","selector_name":"envoy","output_kind":"hdf_ckl"}""";
		Assert.Equal(JobShape.Standard, JobShapes.ForJob("scan", payload));
	}

	[Fact]
	public void ForJob_VcsaServiceSrgItemOnVsphereTarget_ReturnsSrg_NotStandard()
	{
		// The inverse: an SRG-kind VCSA service item on the SAME vsphere-kind target
		// (docs/compliance-parity.md's vSphere 9-0 SRG row) must route to Srg (HDF-only)
		// -- target_kind alone would get this wrong the other direction (it would infer
		// Standard because the target is vsphere-kind).
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"vsphere","transport":"ssh","selector_kind":"service","selector_name":"envoy","output_kind":"hdf"}""";
		Assert.Equal(JobShape.Srg, JobShapes.ForJob("scan", payload));
	}

	[Fact]
	public void ForJob_SshTargetProductItem_OutputKindHdf_ReturnsSrg()
	{
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"ssh","transport":"ssh","selector_kind":"target","output_kind":"hdf"}""";
		Assert.Equal(JobShape.Srg, JobShapes.ForJob("scan", payload));
	}

	[Fact]
	public void ForJob_UnnarrowedCollapsedJob_NoOutputKind_FallsBackToTargetKindInference()
	{
		// A collapsed whole-target remainder job (RunCreationService.BuildUnnarrowedTargetJobSpec)
		// carries no output_kind -- the legacy target_kind inference must still apply so
		// this pre-#741 job shape is unaffected.
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"ssh","unnarrowed":true}""";
		Assert.Equal(JobShape.Srg, JobShapes.ForJob("scan", payload));
	}

	[Theory]
	[InlineData("123")]
	[InlineData("null")]
	public void ForJob_NonStringOutputKind_FallsBackToTargetKindInference(string outputKindJsonValue)
	{
		// A non-string output_kind value (malformed payload shape) is treated as absent
		// -- falls back to target_kind inference -- rather than either hard-coded shape.
		string payload = $$"""{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"vsphere","output_kind":{{outputKindJsonValue}}}""";
		Assert.Equal(JobShape.Standard, JobShapes.ForJob("scan", payload));
	}

	[Fact]
	public void ForJob_UnknownOutputKindStringValue_TreatedAsNotHdf_ReturnsStandard()
	{
		// A well-formed but out-of-vocabulary output_kind string is NOT treated as
		// "absent" -- it is read literally and only "hdf" routes to Srg, so any other
		// string (including an unrecognized one) routes to Standard. This is the
		// primary catalog-kind read succeeding, not a target_kind fallback.
		string payload = """{"target_id":"11111111-1111-1111-1111-111111111111","target_kind":"ssh","output_kind":"unknown_kind"}""";
		Assert.Equal(JobShape.Standard, JobShapes.ForJob("scan", payload));
	}
}
