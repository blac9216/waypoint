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
/// Issue #437: every value in <c>jobs_job_type_check</c> (exactly
/// <see cref="JobCapabilities.All"/>) must resolve to a profile, and an unregistered
/// type must degrade to <see cref="JobResourceProfiles.Default"/> rather than throwing.
/// </summary>
public sealed class JobResourceProfilesTests
{
	[Theory]
	[MemberData(nameof(AllJobTypes))]
	public void EveryClosedJobType_ResolvesToAPositiveProfile(string jobType)
	{
		JobResourceProfile profile = JobResourceProfiles.ForJobType(jobType);
		Assert.True(profile.CpuCores > 0);
		Assert.True(profile.MemoryBytes > 0);
	}

	[Fact]
	public void UnknownJobType_ResolvesToDefault()
	{
		JobResourceProfile profile = JobResourceProfiles.ForJobType("not-a-real-job-type");
		Assert.Equal(JobResourceProfiles.Default, profile);
	}

	[Fact]
	public void ScanIsHeavierThanDiscover_ReflectingInSpecFanOutVersusALightProbe()
	{
		JobResourceProfile scan = JobResourceProfiles.ForJobType("scan");
		JobResourceProfile discover = JobResourceProfiles.ForJobType("discover");

		Assert.True(scan.CpuCores > discover.CpuCores);
		Assert.True(scan.MemoryBytes > discover.MemoryBytes);
	}

	[Fact]
	public void Scale_MultipliesBothWeights()
	{
		JobResourceProfile profile = new(CpuCores: 0.5, MemoryBytes: 100);
		JobResourceProfile scaled = profile.Scale(3);

		Assert.Equal(1.5, scaled.CpuCores, precision: 6);
		Assert.Equal(300, scaled.MemoryBytes);
	}

	[Fact]
	public void AdditionOperator_SumsBothWeights()
	{
		JobResourceProfile left = new(CpuCores: 1.0, MemoryBytes: 100);
		JobResourceProfile right = new(CpuCores: 0.5, MemoryBytes: 50);

		JobResourceProfile sum = left + right;

		Assert.Equal(1.5, sum.CpuCores, precision: 6);
		Assert.Equal(150, sum.MemoryBytes);
	}

	public static TheoryData<string> AllJobTypes()
	{
		TheoryData<string> data = [];
		foreach (string jobType in JobCapabilities.All.Order(StringComparer.Ordinal))
		{
			data.Add(jobType);
		}

		return data;
	}

	/// <summary>
	/// Issue #737 (epic #726 Wave 2 capstone, ADR-0024 "Resource admission applies to
	/// real component jobs"): every closed catalog transport
	/// (<see cref="Waypoint.Core.ComplianceContent.CatalogTransports.All"/>) resolves to
	/// a positive, lighter-than-the-flat-scan-weight profile -- a single component is a
	/// materially smaller unit of work than the legacy whole-target fan-out.
	/// </summary>
	[Theory]
	[InlineData("vmware")]
	[InlineData("ssh")]
	[InlineData("nsx-api")]
	[InlineData("vcf-api")]
	public void ResolveScanComponentProfile_EveryClosedTransport_ResolvesToAPositiveProfileLighterThanFlatScan(string transport)
	{
		JobResourceProfile componentProfile = JobResourceProfiles.ResolveScanComponentProfile(transport);
		JobResourceProfile flatScan = JobResourceProfiles.ForJobType("scan");

		Assert.True(componentProfile.CpuCores > 0);
		Assert.True(componentProfile.MemoryBytes > 0);
		Assert.True(componentProfile.CpuCores <= flatScan.CpuCores);
		Assert.True(componentProfile.MemoryBytes <= flatScan.MemoryBytes);
	}

	[Fact]
	public void ResolveScanComponentProfile_NullTransport_FallsBackToFlatScanWeight()
	{
		// The legacy per-target scan payload has no `transport` key at all -- null
		// must resolve to the unchanged flat `scan` weight, not throw and not silently
		// under-admit a whole-target job at a component-sized budget.
		JobResourceProfile profile = JobResourceProfiles.ResolveScanComponentProfile(null);
		Assert.Equal(JobResourceProfiles.ForJobType("scan"), profile);
	}

	[Fact]
	public void ResolveScanComponentProfile_UnknownTransport_FallsBackToFlatScanWeight()
	{
		JobResourceProfile profile = JobResourceProfiles.ResolveScanComponentProfile("not-a-real-transport");
		Assert.Equal(JobResourceProfiles.ForJobType("scan"), profile);
	}
}
