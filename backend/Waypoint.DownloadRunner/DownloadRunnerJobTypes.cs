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

namespace Waypoint.DownloadRunner;

/// <summary>
/// The download-runner's actual claim allowlist for this M1 catch-up (issue #441).
/// Deliberately narrower than <c>Waypoint.Core.Jobs.JobCapabilities.Download</c>,
/// which also reserves five further "later" content-library/bundle/update job types
/// that have no registered <see cref="Waypoint.Core.Jobs.IJobHandler"/> anywhere yet --
/// claiming one of those here would fail it immediately with "no handler registered"
/// (<c>Waypoint.Runner.Jobs.JobDispatcherHostedService</c>) instead of leaving it
/// queued for the runner that eventually implements it. A future issue that adds a
/// handler for one of those types adds it to this set in the same change.
///
/// <c>tool-install</c> (issue #619): <c>ManagedToolInstallJobHandler</c> registered
/// with <c>JobType == "tool-install"</c> back in #39/#602, but this allowlist was
/// never updated to include it -- every install job (local-repository, upload, and
/// depot-fetch) queued successfully and then sat <c>queued</c> forever because no
/// runner ever claimed it (<c>Program.cs</c> filters <c>AddWaypointExecution</c>'s
/// full handler set down to exactly this set before building
/// <c>Waypoint.Runner.Jobs.JobHandlerRegistry</c>). See
/// <c>Waypoint.Tests.Runner.EveryRegisteredJobHandlerIsClaimableTests</c> for the
/// convention test that now catches this class of gap for any future handler.
///
/// <c>binaries-download</c> (issue #1482, migration 0099 reserved it in
/// <c>jobs_job_type_check</c>/<see cref="Waypoint.Core.Jobs.JobCapabilities.Download"/>
/// without allowlisting it here -- see PR #1596 / issue #1479's scope note): this is
/// the INVERSE of the <c>tool-install</c> gap above -- there, a handler existed and the
/// allowlist lagged; here, the allowlist entry and <c>BinariesDownloadJobHandler</c>'s
/// registration land together in this one change, so the type is never reserved
/// without a claimer.
/// </summary>
public static class DownloadRunnerJobTypes
{
	public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
	{
		"catalog-index",
		"download",
		"tool-install",
		"depot-enrollment",
		"catalog-pull",
		"retention-sweep",
		"binaries-download"
	};
}
