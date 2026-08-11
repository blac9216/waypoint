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

using System.Text.Json;
using Waypoint.ComplianceRunner.Readiness;
using Xunit;

namespace Waypoint.Tests.ComplianceRunner;

/// <summary>
/// <see cref="RunnerHealthCheckProbe"/> is this non-ASP.NET host's answer to a Compose
/// healthcheck (see its doc comment for why a file sentinel rather than an HTTP probe).
/// These tests cover every way <c>--health-check</c> must exit 1: missing file,
/// malformed content, a stale report, and a report that honestly says "not ready" --
/// plus the one way it exits 0.
/// </summary>
public sealed class RunnerHealthCheckProbeTests : IDisposable
{
	private readonly string _reportPath = Path.Combine(Path.GetTempPath(), $"waypoint-health-test-{Guid.NewGuid():N}.json");
	private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

	public void Dispose()
	{
		if (File.Exists(_reportPath))
		{
			File.Delete(_reportPath);
		}
	}

	[Fact]
	public void IsHealthCheckInvocation_RecognizesOnlyTheDocumentedArgument()
	{
		Assert.True(RunnerHealthCheckProbe.IsHealthCheckInvocation(["--health-check"]));
		Assert.True(RunnerHealthCheckProbe.IsHealthCheckInvocation(["--HEALTH-CHECK"]));
		Assert.False(RunnerHealthCheckProbe.IsHealthCheckInvocation([]));
		Assert.False(RunnerHealthCheckProbe.IsHealthCheckInvocation(["--other"]));
	}

	[Fact]
	public void MissingReportFile_IsUnhealthy()
	{
		int exitCode = RunnerHealthCheckProbe.Run(_reportPath, MaxAge, DateTimeOffset.UtcNow);
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public void MalformedJson_IsUnhealthy()
	{
		File.WriteAllText(_reportPath, "{ not json");
		int exitCode = RunnerHealthCheckProbe.Run(_reportPath, MaxAge, DateTimeOffset.UtcNow);
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public void FreshReadyReport_IsHealthy()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		WriteReport(new RunnerHealthReport(Ready: true, Capabilities: ["discover", "scan"], Problems: [], Timestamp: now));

		int exitCode = RunnerHealthCheckProbe.Run(_reportPath, MaxAge, now.AddSeconds(5));
		Assert.Equal(0, exitCode);
	}

	[Fact]
	public void FreshNotReadyReport_IsUnhealthy()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		WriteReport(new RunnerHealthReport(Ready: false, Capabilities: ["discover"], Problems: ["mount missing"], Timestamp: now));

		int exitCode = RunnerHealthCheckProbe.Run(_reportPath, MaxAge, now.AddSeconds(5));
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public void StaleReadyReport_IsUnhealthy()
	{
		DateTimeOffset writtenAt = DateTimeOffset.UtcNow;
		WriteReport(new RunnerHealthReport(Ready: true, Capabilities: ["discover"], Problems: [], Timestamp: writtenAt));

		DateTimeOffset now = writtenAt.Add(MaxAge).AddMinutes(1);
		int exitCode = RunnerHealthCheckProbe.Run(_reportPath, MaxAge, now);
		Assert.Equal(1, exitCode);
	}

	private void WriteReport(RunnerHealthReport report)
	{
		File.WriteAllText(_reportPath, JsonSerializer.Serialize(report));
	}
}
