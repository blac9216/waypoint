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
}
