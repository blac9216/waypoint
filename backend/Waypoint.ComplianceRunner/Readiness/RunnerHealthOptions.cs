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

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// Configuration for the runner-local health/capability report (issue #440 AC4).
///
/// This host has no ASP.NET/HTTP listener (ADR-0013 §1: a runner is not another web
/// API), so it cannot answer a <c>wget --spider</c>-style Compose healthcheck the way
/// <c>Waypoint.Api</c> does (see <c>HealthCheckProbe</c>'s doc comment). Instead it
/// follows the same "the image owns how it reports health" convention with a
/// filesystem sentinel instead of a loopback HTTP call: <see cref="RunnerHealthReportingHostedService"/>
/// re-evaluates readiness on <see cref="RefreshInterval"/> and writes the JSON
/// <see cref="RunnerHealthReport"/> to <see cref="ReportFilePath"/>; <c>--health-check</c>
/// (<see cref="RunnerHealthCheckProbe"/>) reads that file and exits 0/1 the same way
/// the API's probe does. A Compose <c>HEALTHCHECK</c> runs
/// <c>dotnet Waypoint.ComplianceRunner.dll --health-check</c> exactly like the API
/// image runs its own probe -- the actual Dockerfile/Compose wiring is #442's job, not
/// this issue's, per its Affected Files table.
/// </summary>
public sealed class RunnerHealthOptions
{
	public const string SectionName = "RunnerHealth";

	/// <summary>
	/// Where the JSON health report is written and read back from. Defaults to a path
	/// under the container's writable temp directory so no extra volume is required
	/// just to report health.
	/// </summary>
	public string ReportFilePath { get; set; } = "/tmp/waypoint-compliance-runner-health.json";

	/// <summary>How often readiness is re-evaluated and the report file rewritten.</summary>
	public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// How stale a report file is allowed to be before <c>--health-check</c> treats it
	/// as unhealthy, even if its own last-written content said "ready". Larger than
	/// <see cref="RefreshInterval"/> so an on-time refresh never trips this by itself; it
	/// exists to catch a hung/deadlocked reporting loop (the process is alive enough to
	/// keep its container running, but no longer proving it is doing useful work).
	/// </summary>
	public TimeSpan MaxReportAge { get; set; } = TimeSpan.FromMinutes(2);

	/// <summary>
	/// Issue #443: the stable identity this process heartbeats to <c>worker_registry</c>
	/// under (see <c>Waypoint.Core.SystemState.IWorkerRegistryWriter</c>). Defaults to
	/// the container hostname (Docker gives each container a unique one by default);
	/// see <c>Waypoint.DownloadRunner.DownloadRunnerOptions.WorkerId</c> for the same
	/// default reasoning on the download-runner side.
	/// </summary>
	public string WorkerId { get; set; } = Environment.MachineName;
}
