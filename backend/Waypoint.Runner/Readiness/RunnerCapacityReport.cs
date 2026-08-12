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

using System.Linq;
using Waypoint.Runner.Resources;

namespace Waypoint.Runner.Readiness;

/// <summary>
/// The JSON-serializable shape of a runner's resource-admission state (issue #437),
/// shared by every runner host's readiness/health report (issue #461 unified what were
/// previously field-for-field duplicate <c>RunnerCapacityReport</c> types in the
/// download-runner and compliance-runner projects) so operators reading any host's
/// report see the same vocabulary.
/// </summary>
/// <param name="Source">Where the discovered budget came from: <c>"CgroupV2"</c>, <c>"CgroupV1"</c>, or <c>"Fallback"</c> (see <see cref="HostResourceLimitSource"/>).</param>
/// <param name="IsFallback">True when <paramref name="Source"/> is <c>"Fallback"</c> -- surfaced as its own boolean so a dashboard need not string-match <paramref name="Source"/> to flag it.</param>
/// <param name="DiscoveredCpuCores">Raw discovered CPU budget, in cores, before any operator cap.</param>
/// <param name="DiscoveredMemoryBytes">Raw discovered memory budget, in bytes, before any operator cap.</param>
/// <param name="EffectiveCpuCores">Effective CPU budget admission enforces: <c>min(discovered, operator cap)</c>.</param>
/// <param name="EffectiveMemoryBytes">Effective memory budget admission enforces: <c>min(discovered, operator cap)</c>.</param>
/// <param name="AdmittedCpuCores">CPU cores currently committed to admitted, still-running jobs.</param>
/// <param name="AdmittedMemoryBytes">Memory bytes currently committed to admitted, still-running jobs.</param>
/// <param name="AdmittedJobCount">How many jobs are currently admitted/running.</param>
/// <param name="StarvedJobTypes">
/// Issue #467: job types this runner is currently denying admission to, each tagged
/// permanent (the type's profile alone exceeds <see cref="EffectiveCpuCores"/>/
/// <see cref="EffectiveMemoryBytes"/> -- no amount of waiting fixes this) or transient
/// (would fit once currently-admitted jobs release budget). Empty when nothing is
/// starved. Cleared for a job type the moment it is next admitted, so a transient entry
/// disappears on its own once contention passes -- this is a live snapshot, not a
/// historical log.
/// </param>
public sealed record RunnerCapacityReport(
	string Source,
	bool IsFallback,
	double DiscoveredCpuCores,
	long DiscoveredMemoryBytes,
	double EffectiveCpuCores,
	long EffectiveMemoryBytes,
	double AdmittedCpuCores,
	long AdmittedMemoryBytes,
	int AdmittedJobCount,
	IReadOnlyList<StarvedJobTypeReport> StarvedJobTypes);

/// <summary>JSON-serializable twin of <see cref="Resources.StarvedJobType"/> for <see cref="RunnerCapacityReport"/>.</summary>
public sealed record StarvedJobTypeReport(string JobType, bool Permanent)
{
	public static StarvedJobTypeReport FromDomain(StarvedJobType starved) => new(starved.JobType, starved.Permanent);
}

/// <summary>
/// Builds a <see cref="RunnerCapacityReport"/> from a live <see cref="ResourceAdmissionController"/>.
/// Extracted so both runner hosts' readiness reporters call one implementation
/// (issue #461) instead of each carrying its own copy of this mapping.
/// </summary>
public static class RunnerCapacityReportFactory
{
	public static RunnerCapacityReport FromController(ResourceAdmissionController resourceAdmission)
	{
		ArgumentNullException.ThrowIfNull(resourceAdmission);

		return new RunnerCapacityReport(
			Source: resourceAdmission.Discovered.Source.ToString(),
			IsFallback: resourceAdmission.Discovered.IsFallback,
			DiscoveredCpuCores: resourceAdmission.Discovered.CpuCores,
			DiscoveredMemoryBytes: resourceAdmission.Discovered.MemoryBytes,
			EffectiveCpuCores: resourceAdmission.EffectiveBudget.CpuCores,
			EffectiveMemoryBytes: resourceAdmission.EffectiveBudget.MemoryBytes,
			AdmittedCpuCores: resourceAdmission.AdmittedCpuCores,
			AdmittedMemoryBytes: resourceAdmission.AdmittedMemoryBytes,
			AdmittedJobCount: resourceAdmission.AdmittedJobCount,
			StarvedJobTypes: [.. resourceAdmission.StarvedJobTypes
				.Select(StarvedJobTypeReport.FromDomain)
				.OrderBy(starved => starved.JobType, StringComparer.Ordinal)]);
	}
}
