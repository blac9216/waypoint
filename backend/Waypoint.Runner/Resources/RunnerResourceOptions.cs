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
/// Operator-facing configuration for resource-aware admission (ADR-0014 §5, issue #437),
/// bound from the <c>RunnerResources</c> configuration section on every runner host
/// (compliance-runner, download-runner, and the API's own Combined host -- see
/// <c>ResourceAdmissionController</c>'s doc comment for how Combined maps this onto its
/// existing <c>JobEngineOptions.MaxConcurrency</c> semantics).
/// </summary>
public sealed class RunnerResourceOptions
{
	public const string SectionName = "RunnerResources";

	/// <summary>
	/// The tested conservative CPU budget (in cores) used when cgroup data is missing,
	/// unreadable, or reports no effective limit ("max"/"unlimited") -- issue #437's
	/// "tested conservative host fallback... never silent" requirement. 1 core: safe on
	/// any host this appliance is documented to run on (ADR-0001's single-node
	/// appliance), even a minimal lab VM, while still allowing one job to proceed
	/// rather than wedging a misconfigured runner at zero admitted work.
	/// </summary>
	public double FallbackCpuCores { get; set; } = 1.0;

	/// <summary>
	/// The tested conservative memory budget used under the same fallback conditions as
	/// <see cref="FallbackCpuCores"/>. 1 GiB: comfortably below what any documented
	/// deployment target provides, while large enough for one <c>discover</c>/
	/// <c>credential-test</c>-weight job (see <c>JobResourceProfiles</c>) to be
	/// admitted even in the fallback case.
	/// </summary>
	public long FallbackMemoryBytes { get; set; } = 1024L * 1024 * 1024;

	/// <summary>
	/// Operator-configured CPU cap, in cores. When set, the effective admission budget
	/// is <c>min(discovered, this)</c> -- an operator may intentionally reserve
	/// headroom on a shared host even when cgroups report a larger allocation. Null
	/// (the default) applies no cap beyond what was discovered.
	/// </summary>
	public double? MaxCpuCores { get; set; }

	/// <summary>Operator-configured memory cap, in bytes. Same <c>min(discovered, this)</c> intersection as <see cref="MaxCpuCores"/>.</summary>
	public long? MaxMemoryBytes { get; set; }

	/// <summary>
	/// Root of the cgroup filesystem to read from. Defaults to the real mount; tests
	/// point this at a fixture directory laid out like a cgroup v1 or v2 hierarchy so
	/// cgroup parsing is exercised without depending on the test host's actual cgroup
	/// state (issue #437: "avoid platform-specific assumptions that break
	/// non-containerized tests").
	/// </summary>
	public string CgroupRoot { get; set; } = "/sys/fs/cgroup";
}
