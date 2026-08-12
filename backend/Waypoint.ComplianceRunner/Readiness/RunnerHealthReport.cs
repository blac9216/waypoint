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
/// The JSON shape written to <see cref="RunnerHealthOptions.ReportFilePath"/> and read
/// back by <c>--health-check</c> (see <see cref="RunnerHealthCheckProbe"/>). Kept
/// deliberately small and dependency-free (no Waypoint.Core reference) so it can be
/// serialized/deserialized with the BCL's source-generated <c>System.Text.Json</c>
/// support without pulling ASP.NET's JSON options into a non-ASP.NET host.
/// </summary>
/// <param name="Capacity">
/// Issue #437 (ADR-0014 §5, tracked here alongside #461's near-duplicate download-runner
/// report): this runner's calculated resource-admission capacity and current admission
/// state.
/// </param>
public sealed record RunnerHealthReport(
	bool Ready,
	IReadOnlyList<string> Capabilities,
	IReadOnlyList<string> Problems,
	RunnerCapacityReport? Capacity,
	DateTimeOffset Timestamp);

/// <summary>
/// The JSON-serializable shape of a runner's resource-admission state (issue #437).
/// Field-for-field the same shape as <c>Waypoint.DownloadRunner.RunnerCapacityReport</c>
/// (#461: intentionally near-duplicate reports, not unified) so operators reading either
/// host's report see the same vocabulary; kept as a second, separately-defined type here
/// rather than a shared reference so this project's "no Waypoint.Core reference" boundary
/// (see this file's other doc comment) is not compromised by a cross-project dependency
/// for a handful of fields.
/// </summary>
public sealed record RunnerCapacityReport(
	string Source,
	bool IsFallback,
	double DiscoveredCpuCores,
	long DiscoveredMemoryBytes,
	double EffectiveCpuCores,
	long EffectiveMemoryBytes,
	double AdmittedCpuCores,
	long AdmittedMemoryBytes,
	int AdmittedJobCount);
