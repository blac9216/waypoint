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

namespace Waypoint.Core.SystemState;

/// <summary>
/// One <c>worker_registry</c> row (migration 0026, issue #443): a runner process's
/// last-known liveness/capability snapshot, independent of any in-flight job claim.
/// </summary>
/// <param name="WorkerId">Stable per-process identity the row is keyed on (e.g. hostname/container id).</param>
/// <param name="JobTypes">The <c>JobCapabilities</c> allowlist this worker claims from.</param>
/// <param name="Ready">The worker's own self-reported readiness at the time of the last heartbeat.</param>
/// <param name="LastSeenAt">When the worker last upserted this row.</param>
/// <param name="StarvedJobTypes">
/// Issue #467 (migration 0029): job types this worker was denying resource admission to
/// as of its last heartbeat, each tagged permanent (profile exceeds the total effective
/// budget) or transient (would fit once running jobs release budget). Empty when nothing
/// was starved.
/// </param>
/// <param name="ToolPresent">
/// Issue #560 (migration 0034): the download-runner's <c>ManagedToolPresenceChecker</c>
/// result as of its last heartbeat -- null for a compliance-runner row, which has
/// nothing to report here. Feeds <c>GET /downloads/readiness</c>'s combined
/// tool-installed + depot-token-valid answer without duplicating #39's future
/// install-flow scope.
/// </param>
public sealed record WorkerHeartbeat(
	string WorkerId,
	IReadOnlyList<string> JobTypes,
	bool Ready,
	DateTimeOffset LastSeenAt,
	IReadOnlyList<StarvedWorkerJobType> StarvedJobTypes,
	bool? ToolPresent = null);

/// <summary>JSON/DB-persisted twin of <c>Waypoint.Runner.Resources.StarvedJobType</c> -- <c>Waypoint.Core</c> cannot reference the runner project, so this is the control-plane-side shape.</summary>
/// <param name="JobType">The <c>jobs.job_type</c> value being denied admission.</param>
/// <param name="Permanent">True when the type's profile alone exceeds this worker's total effective budget (never fits); false when it is only transient contention.</param>
public sealed record StarvedWorkerJobType(string JobType, bool Permanent);
