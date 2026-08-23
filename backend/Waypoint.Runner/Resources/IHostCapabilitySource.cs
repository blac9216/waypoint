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
/// Reads the CPU and memory this process's host actually offers (ADR-0018 issue #555),
/// used by <see cref="CgroupResourceDiscovery"/> only when no explicit, finite cgroup
/// limit was found -- an explicit container/cgroup limit always stays authoritative
/// over anything read here.
/// </summary>
public interface IHostCapabilitySource
{
	/// <summary>
	/// CPU cores available to this process. Prefers the OS-reported processor affinity
	/// mask when one is available (fewer cores than the host has, e.g. `taskset`/CPU-set
	/// pinning outside of cgroups) and otherwise falls back to the host's total logical
	/// processor count.
	/// </summary>
	double AvailableCpuCores();

	/// <summary>Total physical memory installed on the host, in bytes.</summary>
	long TotalMemoryBytes();
}

/// <summary>
/// The production <see cref="IHostCapabilitySource"/>: reads real OS/runtime state.
/// Never a test double -- <c>CgroupResourceDiscoveryTests</c> supplies a fake
/// implementation instead of exercising this class, so host-derived discovery tests do
/// not depend on the actual test host's CPU/memory (ADR-0018, mirroring
/// <see cref="RunnerResourceOptions.CgroupRoot"/>'s "avoid platform-specific
/// assumptions" convention for cgroup fixtures).
/// </summary>
public sealed class SystemHostCapabilitySource : IHostCapabilitySource
{
	/// <summary>
	/// <see cref="Environment.ProcessorCount"/> already reflects the OS scheduler's
	/// affinity view on both Windows and Linux (it is not a raw hardware core count on a
	/// process pinned to a CPU set outside of cgroups -- e.g. `taskset`), so no separate
	/// affinity-mask API call is layered on top of it.
	/// </summary>
	public double AvailableCpuCores() => Environment.ProcessorCount;

	/// <summary>
	/// <see cref="GC.GetGCMemoryInfo"/>'s <c>TotalAvailableMemoryBytes</c> is the GC's
	/// own view of the memory it could use, which on a host with no cgroup memory limit
	/// (the only case <see cref="CgroupResourceDiscovery"/> ever calls this from) equals
	/// real host physical memory -- the same reasoning that class's own doc comment
	/// gives for *not* using this API as a cgroup-limit reader applies in reverse here:
	/// once cgroup discovery has already established there is no container limit to
	/// read, the GC's total-available figure is exactly the host capacity this method
	/// is asked for, not a downstream-adjusted container number.
	/// </summary>
	public long TotalMemoryBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
}
