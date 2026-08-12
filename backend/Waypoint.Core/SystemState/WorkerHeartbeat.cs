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
public sealed record WorkerHeartbeat(
	string WorkerId,
	IReadOnlyList<string> JobTypes,
	bool Ready,
	DateTimeOffset LastSeenAt);
