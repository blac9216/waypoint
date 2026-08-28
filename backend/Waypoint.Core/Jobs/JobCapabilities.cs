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
/// The two execution-domain job-type allowlists ADR-0013 §2 assigns to the
/// <c>compliance-runner</c> and <c>download-runner</c> Compose services, and that
/// ADR-0014's atomic claim now enforces (issue #435): "the atomic <c>FOR UPDATE SKIP
/// LOCKED</c> claim includes the runner's explicit job-type allowlist... a runner must
/// never claim and release work belonging to another execution domain." Each set is a
/// subset of <c>jobs_job_type_check</c>'s closed values (0001/0019's migrations) --
/// together they are exhaustive over every <c>job_type</c> the schema accepts, so a
/// future job type must be added to exactly one set here in the same change that adds
/// it to the CHECK constraint, or it becomes unclaimable by any runner.
///
/// Both runners still live behind the single combined
/// <c>Waypoint.Runner.Jobs.JobDispatcherHostedService</c>/<c>Waypoint.Runner.Jobs.JobHandlerRegistry</c> today --
/// ADR-0013's split into two separate runner executables is a later migration -- so
/// these sets are the seam that split will key its two claim loops on, and are already
/// what <see cref="IJobRunnerRepository.ClaimJobAsync"/> requires a caller to narrow to
/// per issue #435.
/// </summary>
public static class JobCapabilities
{
	/// <summary>
	/// <c>compliance-runner</c>'s allowlist (ADR-0013 §2, reassigned by
	/// <see href="../../docs/adr/0017-compliance-content-runner-placement.md">ADR-0017</see>):
	/// <c>discover</c>, <c>credential-test</c>, <c>scan</c>, <c>remediate</c>, and
	/// <c>content-pull</c>/<c>content-import</c>. <c>remediate</c> has no registered
	/// <see cref="IJobHandler"/> yet (ADR-0013 lists it as "later"), but belongs to
	/// this domain the moment one lands. <c>content-pull</c>/<c>content-import</c>
	/// moved here from <see cref="Download"/> per ADR-0017: compliance content is
	/// consumed only by compliance execution (<c>scan</c> reads profiles from it) and
	/// shares no dependency with the download domain. <c>purge</c> (issue #594, epic
	/// #577) belongs here for the same reason <c>scan</c> does: deleting a terminal
	/// run's HDF/CKL artifact files requires write access to the scan-artifact volume
	/// (ADR-0014 §7), which only <c>compliance-runner</c>'s mount grants -- the API
	/// process mounts that volume read-only (deploy/compose.yaml).
	/// </summary>
	public static readonly IReadOnlySet<string> Compliance = new HashSet<string>(StringComparer.Ordinal)
	{
		"discover",
		"credential-test",
		"scan",
		"remediate",
		"content-pull",
		"content-import",
		"content-check",
		"purge"
	};

	/// <summary>
	/// <c>download-runner</c>'s allowlist (ADR-0013 §2): <c>catalog-index</c>,
	/// <c>download</c>, and the remaining "later" content-library/repository job types
	/// reserved in <c>jobs_job_type_check</c> (<c>bundle-export</c>, <c>bundle-import</c>,
	/// <c>content-library-sync</c>, <c>update</c>). <c>content-pull</c>/<c>content-import</c>
	/// moved to <see cref="Compliance"/> (ADR-0017) -- see that ADR for why. None of the
	/// remaining "later" types has a registered <see cref="IJobHandler"/> yet; they are
	/// listed here so the closed <c>job_type</c> set and the closed capability sets stay
	/// in lockstep from day one rather than drifting until each handler lands.
	/// </summary>
	public static readonly IReadOnlySet<string> Download = new HashSet<string>(StringComparer.Ordinal)
	{
		"catalog-index",
		"download",
		"bundle-export",
		"bundle-import",
		"content-library-sync",
		"update",
		"tool-install",
		"depot-enrollment",
		"catalog-pull"
	};

	/// <summary>
	/// The union of every capability set -- exactly <c>jobs_job_type_check</c>'s
	/// values. Used by the still-combined dispatcher (<c>Waypoint.Runner.Jobs.JobDispatcherHostedService</c>)
	/// until ADR-0013's runner split lands, so today's single process keeps claiming
	/// every job type it always has, through the same non-empty-allowlist path a split
	/// runner will use with a narrower set.
	/// </summary>
	public static readonly IReadOnlySet<string> All = new HashSet<string>(Compliance.Concat(Download), StringComparer.Ordinal);
}
