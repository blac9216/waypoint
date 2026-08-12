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

namespace Waypoint.Core.Jobs;

/// <summary>
/// Declares how much of a runner's discovered CPU/memory budget one running instance of
/// a <c>jobs.job_type</c> is expected to consume (ADR-0014 §5, issue #437). Weights are
/// deliberately coarse "how many of these could plausibly run at once" figures, not
/// measured limits enforced on the process (no cgroup child scoping is introduced here) --
/// the dispatcher sums the profiles of currently-running jobs and refuses to admit a
/// claim that would push either sum past the runner's discovered-and-capped budget (see
/// <c>Waypoint.Runner.Resources.ResourceAdmissionController</c>).
///
/// <para>
/// <b>Provisional, not final.</b> Every weight assigned to a <c>jobs_job_type_check</c>
/// value in <see cref="JobResourceProfiles"/> is an engineering guess pending live
/// measurement (issue #437's acceptance criteria: "use conservative provisional profiles
/// explicitly marked as provisional/for-measurement... do not claim final tuning"). Do
/// not read a specific number here as tuned; read the relative ordering (InSpec scans
/// cost more than a discovery ping) as the only claim actually being made today.
/// </para>
/// </summary>
/// <param name="CpuCores">
/// Estimated CPU cores one running instance of this job type occupies. Fractional values
/// are allowed (e.g. 0.25 for a lightweight probe).
/// </param>
/// <param name="MemoryBytes">Estimated resident memory one running instance of this job type occupies.</param>
public readonly record struct JobResourceProfile(double CpuCores, long MemoryBytes)
{
	/// <summary>
	/// Multiplies both weights by <paramref name="factor"/> -- used to sum a profile
	/// across <paramref name="factor"/> concurrently running instances of the same job
	/// type without allocating an accumulator loop at every admission check.
	/// </summary>
	public JobResourceProfile Scale(int factor) => new(CpuCores * factor, MemoryBytes * factor);

	public static JobResourceProfile operator +(JobResourceProfile left, JobResourceProfile right) =>
		new(left.CpuCores + right.CpuCores, left.MemoryBytes + right.MemoryBytes);
}
