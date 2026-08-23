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

using Waypoint.Core.Jobs;

namespace Waypoint.Runner.Resources;

/// <summary>
/// ADR-0018 (issue #555): the startup admission invariant -- "no advertised core job
/// type may be permanently inadmissible; fail readiness with an exact config action
/// otherwise" (owner ruling, 2026-08-23). Every runner host calls
/// <see cref="Validate"/> once, after its <see cref="JobHandlerRegistry"/> allowlist and
/// <see cref="ResourceAdmissionController"/> effective budget are both known, and before
/// <c>host.Run()</c>/<c>host.RunAsync()</c> -- a runner whose own configuration can
/// never admit a job type it claims to serve must not start at all, rather than start
/// and rely on issue #467's runtime starvation warning for an operator to eventually
/// notice.
///
/// <para>
/// This mirrors <c>JobHandlerRegistry</c>'s own "fails closed with no ambiguity left for
/// startup to silently ignore" convention (see that type's doc comment) and
/// <c>Waypoint.DownloadRunner.Program</c>'s existing
/// <c>ManagedTool:ToolStatePath</c> startup guard -- both throw
/// <see cref="InvalidOperationException"/> synchronously during composition rather than
/// starting a dispatcher that can only ever fail every job of a given type.
/// </para>
/// </summary>
public static class ResourceAdmissionInvariant
{
	/// <summary>
	/// Checks every job type in <paramref name="advertisedJobTypes"/> (a runner's
	/// <see cref="JobHandlerRegistry.AllowedJobTypes"/>) against
	/// <paramref name="effectiveBudget"/>. Throws <see cref="InvalidOperationException"/>
	/// naming every offending job type, its resource profile, the effective budget, and
	/// the exact <c>RunnerResources__*</c> configuration keys an operator can raise, if
	/// any type's <see cref="JobResourceProfile"/> alone exceeds the budget on either
	/// axis -- the same "permanent" condition
	/// <see cref="ResourceAdmissionController.TryAdmit"/> already distinguishes from
	/// transient contention (issue #467), just checked once at startup instead of
	/// discovered later through a rate-limited runtime log line.
	/// </summary>
	public static void Validate(IReadOnlySet<string> advertisedJobTypes, HostResourceLimits effectiveBudget)
	{
		ArgumentNullException.ThrowIfNull(advertisedJobTypes);

		List<string> problems = [];
		foreach (string jobType in advertisedJobTypes.OrderBy(type => type, StringComparer.Ordinal))
		{
			JobResourceProfile profile = JobResourceProfiles.ForJobType(jobType);
			bool cpuExceeds = profile.CpuCores > effectiveBudget.CpuCores;
			bool memoryExceeds = profile.MemoryBytes > effectiveBudget.MemoryBytes;
			if (!cpuExceeds && !memoryExceeds)
			{
				continue;
			}

			problems.Add(
				$"'{jobType}' needs {profile.CpuCores} cores / {profile.MemoryBytes} bytes, " +
				$"but the effective budget is only {effectiveBudget.CpuCores} cores / {effectiveBudget.MemoryBytes} bytes " +
				$"(exceeds on {DescribeExceededAxes(cpuExceeds, memoryExceeds)}).");
		}

		if (problems.Count == 0)
		{
			return;
		}

		throw new InvalidOperationException(
			"Runner host startup failed: this runner advertises a job type it can never admit, which would " +
			"leave it permanently starved (ADR-0018, issue #555). Offending job type(s):\n" +
			string.Join('\n', problems.Select(problem => $"  - {problem}")) +
			"\nFix by raising RunnerResources__MaxCpuCores / RunnerResources__MaxMemoryBytes (if an operator " +
			"cap is the binding constraint) or RunnerResources__FallbackCpuCores / RunnerResources__FallbackMemoryBytes " +
			"(if this runner fell back to the conservative default rather than deriving cgroup/host capacity), " +
			"or move this job type to a runner with more capacity.");
	}

	private static string DescribeExceededAxes(bool cpuExceeds, bool memoryExceeds) =>
		(cpuExceeds, memoryExceeds) switch
		{
			(true, true) => "both CPU and memory",
			(true, false) => "CPU",
			(false, true) => "memory",
			_ => "neither" // Unreachable: Validate only calls this when at least one is true.
		};
}
