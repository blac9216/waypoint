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
/// Bound from the <c>WorkerRegistry</c> configuration section (issue #443): controls
/// how the control plane turns a <c>worker_registry</c> row's <c>last_seen_at</c> into
/// an available/unavailable verdict for <c>GET /system</c>.
/// </summary>
public sealed class WorkerRegistryOptions
{
	public const string SectionName = "WorkerRegistry";

	/// <summary>
	/// A worker row older than this is reported unavailable, the same "no update in N
	/// seconds means the process is gone" shape migration 0016's lease-recovery uses.
	/// Both runners heartbeat well inside this window (see each host's
	/// ReadinessInterval/RefreshInterval, both &lt;= 30s today) -- 90 seconds tolerates
	/// a couple of missed cycles under load before flipping a live worker's status.
	/// </summary>
	public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(90);
}
