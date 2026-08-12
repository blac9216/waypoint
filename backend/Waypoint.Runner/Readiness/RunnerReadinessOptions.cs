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

namespace Waypoint.Runner.Readiness;

/// <summary>
/// The subset of a runner host's own options record that <see cref="RunnerReadinessReportingHostedService{TReport}"/>
/// and <see cref="RunnerHealthCheckProbe{TReport}"/> need, common to every runner host
/// (issue #461 unified what were previously two field-for-field near-duplicate options
/// types -- <c>Waypoint.DownloadRunner.DownloadRunnerOptions</c> and
/// <c>Waypoint.ComplianceRunner.Readiness.RunnerHealthOptions</c>).
///
/// This is a read-only view, not a base class each host's options type must inherit --
/// each host keeps its own <c>SectionName</c>-bound options type (so appsettings.json
/// keys, defaults, and doc comments stay host-specific) and simply implements this
/// interface to hand the shared sentinel-writer/probe the four fields they need.
/// </summary>
public interface IRunnerReadinessOptions
{
	/// <summary>
	/// Where the JSON readiness/health report is written and read back from. A plain
	/// file rather than an HTTP endpoint -- ADR-0013 §1 gives a runner host no
	/// REST/SSE surface, so <c>--health-check</c> cannot probe loopback the way
	/// <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c> does.
	/// </summary>
	string ReportFilePath { get; }

	/// <summary>How often the report file is rewritten.</summary>
	TimeSpan RefreshInterval { get; }

	/// <summary>
	/// How stale the report may be before <c>--health-check</c> calls the process
	/// unhealthy regardless of its last-written content -- catches a wedged/deadlocked
	/// reporting loop, not just a missing file. Comfortably wider than
	/// <see cref="RefreshInterval"/> so an ordinary scheduling delay never flaps the
	/// container's health status.
	/// </summary>
	TimeSpan MaxReportAge { get; }

	/// <summary>
	/// Issue #443: the stable identity this process heartbeats to <c>worker_registry</c>
	/// under (see <c>Waypoint.Core.SystemState.IWorkerRegistryWriter</c>). Defaults to
	/// the container hostname on every host that implements this interface.
	/// </summary>
	string WorkerId { get; }
}
