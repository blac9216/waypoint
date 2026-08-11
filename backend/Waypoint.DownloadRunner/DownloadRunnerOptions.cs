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

namespace Waypoint.DownloadRunner;

/// <summary>
/// Host-level configuration for the download-runner process itself (issue #441),
/// distinct from the domain options (<c>Catalog</c>, <c>Downloads</c>, <c>ManagedTool</c>,
/// <c>PowerShell</c>) it shares with <c>Waypoint.Api</c> through
/// <c>AddWaypointInfrastructure</c>.
/// </summary>
public sealed class DownloadRunnerOptions
{
	public const string SectionName = "DownloadRunner";

	/// <summary>
	/// Where <see cref="ReadinessReportingHostedService"/> writes its readiness/capability
	/// marker file and where <c>--health-check</c> reads it back from. A plain file
	/// rather than an HTTP endpoint (ADR-0013 §1: this process serves no REST/SSE
	/// surface) -- the same "the image owns how it reports health" convention
	/// <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c> documents, adapted to a process
	/// with no loopback listener to probe.
	/// </summary>
	public string ReadinessFilePath { get; set; } = "/tmp/waypoint-download-runner-readiness.json";

	/// <summary>How often the readiness marker is refreshed.</summary>
	public TimeSpan ReadinessInterval { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// How stale the marker may be before <c>--health-check</c> calls the process
	/// unhealthy -- catches a wedged/deadlocked reporting loop, not just a missing
	/// file. Comfortably wider than <see cref="ReadinessInterval"/> so an ordinary
	/// scheduling delay never flaps the container's health status.
	/// </summary>
	public TimeSpan ReadinessMaxAge { get; set; } = TimeSpan.FromSeconds(60);
}
