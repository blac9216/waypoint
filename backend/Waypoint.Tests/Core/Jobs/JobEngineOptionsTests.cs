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
/// The defaults on <see cref="JobEngineOptions"/> are a documented contract, not
/// arbitrary numbers: ADR-0008 fixes the consecutive-auth-failure threshold at 3, and
/// issue #117's measurement is what justifies the deliberately-short event command
/// timeout. An unnoticed edit to either changes engine behaviour with nothing failing,
/// which is why they are pinned here rather than only described in a doc comment.
/// </summary>
public sealed class JobEngineOptionsTests
{
	[Fact]
	public void Defaults_MatchTheDocumentedContract()
	{
		JobEngineOptions options = new();

		Assert.True(options.Enabled);
		Assert.Equal(4, options.MaxConcurrency);
		Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
		Assert.Equal(TimeSpan.FromMinutes(5), options.LeaseDuration);
		Assert.Equal(TimeSpan.FromSeconds(30), options.RecoveryInterval);
		Assert.Equal(50, options.RecoveryBatchSize);

		// ADR-0008: "N consecutive auth failures (default 3)".
		Assert.Equal(3, options.ConsecutiveAuthFailureThreshold);

		// #117 measured the job_events ordering lock's worst case at p99 38.30ms under 16
		// concurrent writers; 5s is a wide margin over that while still failing a
		// genuinely stuck writer ~6x faster than Npgsql's inherited 30s default.
		Assert.Equal(5, options.EventCommandTimeoutSeconds);
		Assert.True(
			options.EventCommandTimeoutSeconds < 30,
			"The event command timeout must stay below Npgsql's inherited default, or #108/#117's whole argument evaporates.");
	}

	/// <summary>
	/// The heartbeat cadence defaults to a third of the lease, so a job survives one
	/// missed beat and still renews with a full beat to spare. Asserted as the
	/// relationship rather than the literal value, because the relationship is the
	/// property that matters -- a heartbeat at or above the lease duration would let a
	/// healthy job be recovered out from under itself.
	/// </summary>
	[Theory]
	[InlineData(300, 100)]
	[InlineData(90, 30)]
	[InlineData(30, 10)]
	public void HeartbeatIntervalOrDefault_IsAThirdOfTheLease(int leaseSeconds, int expectedHeartbeatSeconds)
	{
		JobEngineOptions options = new() { LeaseDuration = TimeSpan.FromSeconds(leaseSeconds) };

		Assert.Equal(TimeSpan.FromSeconds(expectedHeartbeatSeconds), options.HeartbeatIntervalOrDefault);
		Assert.True(options.HeartbeatIntervalOrDefault < options.LeaseDuration);
	}

	[Fact]
	public void HeartbeatIntervalOrDefault_PrefersAnExplicitSetting()
	{
		JobEngineOptions options = new()
		{
			LeaseDuration = TimeSpan.FromMinutes(5),
			HeartbeatInterval = TimeSpan.FromSeconds(2)
		};

		Assert.Equal(TimeSpan.FromSeconds(2), options.HeartbeatIntervalOrDefault);
	}

	[Fact]
	public void SectionName_MatchesTheConfigurationKeyTheAppsettingsUse() => Assert.Equal("JobEngine", JobEngineOptions.SectionName);
}
