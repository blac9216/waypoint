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

namespace Waypoint.Core.Capacity;

/// <summary>
/// Configuration for the shared capacity lease pool (issue #569, ADR-0020). Bound from
/// the <c>CapacityPool</c> section on each runner host.
/// </summary>
public sealed class CapacityPoolOptions
{
	public const string SectionName = "CapacityPool";

	/// <summary>
	/// Master switch. When disabled, admission falls back to ADR-0014 §5's per-runner
	/// model exactly as before #569: each replica bounds itself to its own
	/// discovered/capped budget with no cross-runner coordination.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Operator-set total appliance CPU capacity. When both this and
	/// <see cref="PoolMemoryBytes"/> are set, the pool row is written
	/// <c>source='operator'</c> and derived reports never overwrite it. Leave unset to
	/// let runners derive the pool from host capacity (ADR-0018 discovery).
	/// </summary>
	public double? PoolCpuCores { get; set; }

	/// <summary>Operator-set total appliance memory capacity, in bytes. See <see cref="PoolCpuCores"/>.</summary>
	public long? PoolMemoryBytes { get; set; }

	/// <summary>
	/// How long a capacity lease or reservation stays valid without a heartbeat before
	/// it stops counting against the pool (worker loss). Renewed on the same cadence as
	/// the job lease heartbeat, so a worker that loses its job lease loses its capacity
	/// lease on the same clock.
	/// </summary>
	public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// ADR-0020 fairness: how long a job must be continuously denied pool capacity
	/// before its runner parks an anti-starvation reservation that holds freed capacity
	/// for it against smaller claims.
	/// </summary>
	public TimeSpan StarvationReservationAfter { get; set; } = TimeSpan.FromSeconds(30);
}
