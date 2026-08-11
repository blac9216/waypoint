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
/// The download-runner's self-reported readiness/capability point-in-time state
/// (issue #441's "runner-local health/capability reporting" acceptance criterion).
/// Serialized to <see cref="DownloadRunnerOptions.ReadinessFilePath"/> by
/// <see cref="ReadinessReportingHostedService"/> and read back by the
/// <c>--health-check</c> CLI mode (see <see cref="HealthCheckProbe"/>) and, in
/// principle, by any future capability surface the API composes runner status from
/// (ADR-0014 §7's "runner readiness must include registered capabilities... fail
/// closed when required dependencies or mounts are unavailable").
/// </summary>
/// <param name="Ready">
/// True only when every required dependency (database reachable, artifact store
/// writable) is available. The managed download tool is deliberately NOT part of
/// readiness -- ADR-0015 models it as optional operator-installed state, and
/// domain-model.md confirms catalog indexing works without it, so a runner missing
/// only the tool is ready and simply fails <c>download</c> jobs individually with a
/// clear tool-absent note (see <c>ToolGatedDownloadJobHandler</c>), rather than the
/// whole container reporting unhealthy and blocking catalog-index traffic too.
/// </param>
/// <param name="JobTypes">The exact job-type allowlist this host claims (<see cref="DownloadRunnerJobTypes.Allowed"/>).</param>
/// <param name="ToolPresent">Whether the managed <c>vcf-download-tool</c> is currently installed -- reported for operator visibility, not readiness.</param>
/// <param name="ArtifactStoreWritable">Whether <c>Downloads:ArtifactStorePath</c> is reachable and writable.</param>
/// <param name="DepotPathReadable">Whether <c>Catalog:DepotPath</c> is reachable and readable.</param>
/// <param name="GeneratedAt">When this snapshot was produced (UTC) -- what <c>--health-check</c> compares against <see cref="DownloadRunnerOptions.ReadinessMaxAge"/>.</param>
public sealed record ReadinessSnapshot(
	bool Ready,
	IReadOnlyList<string> JobTypes,
	bool ToolPresent,
	bool ArtifactStoreWritable,
	bool DepotPathReadable,
	DateTimeOffset GeneratedAt);
