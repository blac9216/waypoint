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
/// Issue #970: pins that every value in the <see cref="JobStates"/> closed set maps to
/// exactly one <see cref="JobCountBucket"/>, so the <c>job_count_*</c> FILTER clauses
/// <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository"/> builds from
/// <see cref="JobCountBuckets.StatesIn"/> always sum to <c>job_count</c>. This is a
/// pure drift test -- no Postgres involved -- so a state added to <c>JobStates</c>
/// (and the <c>jobs_state_check</c> constraint it mirrors) without updating
/// <see cref="JobCountBuckets.Resolve"/> fails here immediately instead of only
/// showing up as an under-reported bucket sum in a live run.
/// </summary>
public sealed class JobCountBucketsTests
{
	[Fact]
	public void EveryJobState_MapsToExactlyOneBucket()
	{
		// "Exactly one" is enforced by construction: JobCountBucket.Resolve is a
		// total function (a C# switch expression with no unmatched case), so calling
		// it can never fail to produce a bucket -- this loop instead pins that the
		// bucket it produces is a *known* JobCountBucket for every entry in the
		// current closed set, catching an enum drift or exception rather than a
		// silently-uncounted state (that failure mode is what #970 actually was).
		foreach (string state in JobStates.All)
		{
			JobCountBucket bucket = JobCountBuckets.Resolve(state);
			Assert.True(Enum.IsDefined(bucket), $"job state '{state}' resolved to an undefined bucket");
		}
	}

	[Fact]
	public void Buckets_PartitionTheClosedSet_WithNoOverlapAndNoGaps()
	{
		JobCountBucket[] buckets = Enum.GetValues<JobCountBucket>();

		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (JobCountBucket bucket in buckets)
		{
			foreach (string state in JobCountBuckets.StatesIn(bucket))
			{
				Assert.True(seen.Add(state), $"job state '{state}' appears in more than one bucket");
			}
		}

		Assert.Equal(JobStates.All.OrderBy(s => s, StringComparer.Ordinal), seen.OrderBy(s => s, StringComparer.Ordinal));
	}

	[Theory]
	[InlineData(JobStates.Queued, JobCountBucket.Queued)]
	[InlineData(JobStates.Running, JobCountBucket.Running)]
	[InlineData(JobStates.Attesting, JobCountBucket.Running)]
	[InlineData(JobStates.Converting, JobCountBucket.Running)]
	[InlineData(JobStates.Done, JobCountBucket.Completed)]
	[InlineData(JobStates.Uploaded, JobCountBucket.Completed)]
	[InlineData(JobStates.Failed, JobCountBucket.Failed)]
	[InlineData(JobStates.AuthFailed, JobCountBucket.Failed)]
	[InlineData(JobStates.Cancelled, JobCountBucket.Failed)]
	[InlineData(JobStates.Blocked, JobCountBucket.Blocked)]
	public void Resolve_MapsKnownStatesToTheExpectedBucket(string state, JobCountBucket expected)
	{
		Assert.Equal(expected, JobCountBuckets.Resolve(state));
	}
}
