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

namespace Waypoint.Runner.Resources;

/// <summary>
/// The effective CPU/memory budget a runner discovered for itself (ADR-0014 §5, issue
/// #437), before operator caps (<see cref="RunnerResourceOptions"/>) are intersected in.
/// Immutable and source-agnostic: <see cref="Source"/> only records where the numbers
/// came from, for logging and health-diagnostics visibility -- admission math treats
/// every source identically.
/// </summary>
/// <param name="CpuCores">Effective CPU budget, in cores (fractional allowed -- cgroup CPU quotas are not integral).</param>
/// <param name="MemoryBytes">Effective memory budget, in bytes.</param>
/// <param name="Source">Where this reading came from -- see <see cref="HostResourceLimitSource"/>.</param>
public readonly record struct HostResourceLimits(double CpuCores, long MemoryBytes, HostResourceLimitSource Source)
{
	/// <summary>True when this reading came from the tested conservative fallback rather than an actual cgroup read.</summary>
	public bool IsFallback => Source == HostResourceLimitSource.Fallback;
}

/// <summary>Where a <see cref="HostResourceLimits"/> reading was sourced from -- surfaced in health diagnostics so a silent fallback is never actually silent (issue #437 AC).</summary>
public enum HostResourceLimitSource
{
	/// <summary>Read from a cgroup v2 unified hierarchy (<c>cpu.max</c>, <c>memory.max</c>).</summary>
	CgroupV2,

	/// <summary>Read from a cgroup v1 hierarchy (<c>cpu.cfs_quota_us</c>/<c>cpu.cfs_period_us</c>, <c>memory.limit_in_bytes</c>).</summary>
	CgroupV1,

	/// <summary>
	/// Cgroup data was missing, unreadable, or reported "unlimited" -- the tested
	/// conservative host fallback in <see cref="RunnerResourceOptions"/> was used
	/// instead. Always logged (see <c>CgroupResourceDiscovery</c>) and always surfaced
	/// through health diagnostics; never a silent default.
	/// </summary>
	Fallback
}
