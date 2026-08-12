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

using Waypoint.Runner.Readiness;

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// The container health probe for this non-ASP.NET host, mirroring
/// <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c>'s "the image owns how it reports
/// health; the orchestrator merely invokes it" convention, but reading the
/// <see cref="RunnerHealthReportingHostedService"/>'s sentinel file instead of probing
/// an HTTP endpoint this process does not have (see <see cref="RunnerHealthOptions"/>
/// for why). <c>dotnet Waypoint.ComplianceRunner.dll --health-check</c> exits 0
/// (healthy) or 1 (unhealthy) and needs nothing not already in the image.
///
/// A thin domain-specific wrapper around the shared
/// <see cref="RunnerHealthCheckProbe{TReport}"/> (issue #461): it supplies only the
/// <see cref="RunnerHealthReport"/> type argument and how "ready"/"timestamp"/"not
/// ready reason" are extracted from it.
/// </summary>
public static class RunnerHealthCheckProbe
{
	/// <summary>The command-line argument that switches the entry point into probe mode.</summary>
	public const string Argument = Waypoint.Runner.Readiness.RunnerHealthCheckProbe.Argument;

	/// <summary>True when the process was started to probe health rather than to run the dispatcher.</summary>
	public static bool IsHealthCheckInvocation(string[] args) =>
		Waypoint.Runner.Readiness.RunnerHealthCheckProbe.IsHealthCheckInvocation(args);

	/// <summary>
	/// Reads the health report at <paramref name="reportFilePath"/> and reports the
	/// verdict as an exit code. Never throws -- any failure (missing file, malformed
	/// JSON, stale report, reported not-ready) is an unhealthy verdict.
	/// </summary>
	public static int Run(string reportFilePath, TimeSpan maxReportAge) => Run(reportFilePath, maxReportAge, DateTimeOffset.UtcNow);

	/// <summary>Testable overload taking an explicit "now" instead of reading the clock.</summary>
	public static int Run(string reportFilePath, TimeSpan maxReportAge, DateTimeOffset now) =>
		RunnerHealthCheckProbe<RunnerHealthReport>.Run(
			reportFilePath,
			maxReportAge,
			isReady: report => report.Ready,
			timestamp: report => report.Timestamp,
			describeNotReady: report => string.Join("; ", report.Problems),
			now);
}
