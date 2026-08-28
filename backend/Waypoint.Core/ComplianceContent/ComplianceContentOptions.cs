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

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// Configuration for the <c>content-pull</c> handler's working-tree mount (ADR-0017: a
/// compliance-runner-only persistent volume, read-only to scan/discover/credential-test
/// and writable only by content-pull/content-import execution).
/// </summary>
public sealed class ComplianceContentOptions
{
	public const string SectionName = "ComplianceContent";

	/// <summary>
	/// Root directory of the compliance-content working tree. Matches
	/// <c>deploy/compose.yaml</c>'s eventual <c>compliance-content</c> volume
	/// mount; tests point this at a temp directory.
	/// </summary>
	public string ContentPath { get; set; } = "/var/lib/waypoint/compliance-content";

	/// <summary>
	/// Issue #993: wall-clock bound each per-leaf <c>inspec check</c> gets
	/// (<c>Test-WaypointInspecCheck -TimeoutSeconds</c>, unchanged from issue #989's
	/// per-unit protection). Also the multiplicand <see cref="ContentPullChunkSize"/>
	/// scales by to size each bounded chunk invocation's own
	/// <c>PowerShellRequest.Timeout</c> -- see <see cref="ContentPullChunkOverheadSeconds"/>.
	/// </summary>
	public int InspecCheckTimeoutSeconds { get; set; } = 60;

	/// <summary>
	/// Issue #993: how many executable-leaf candidates one
	/// <c>Get-WaypointComplianceContentEntries</c> invocation covers. The whole
	/// content-pull importer used to run as ONE PowerShell pipeline bounded by
	/// PowerShellOptions.DefaultInvocationTimeout (a fixed 00:30:00) -- fine while
	/// per-check work fast-failed (issue #984), but once #989 made checks genuinely run
	/// to completion (~20s each), the aggregate of hundreds of real checks exceeded that
	/// fixed wall clock and the whole pipeline was force-stopped, discarding the
	/// atomically-staged-at-the-end importer output entirely (0 profiles promoted).
	/// Chunking bounds each invocation's timeout to THIS MANY leaves' worth of
	/// per-check budget instead of the whole tree's -- doubling the content tree
	/// doubles the chunk COUNT, never any single chunk's own timeout, which is what
	/// makes the budget scale structurally rather than needing a bigger magic constant
	/// every time vendor content grows.
	/// </summary>
	public int ContentPullChunkSize { get; set; } = 25;

	/// <summary>
	/// Issue #993: fixed per-chunk-invocation overhead (module dispatch, filesystem
	/// enumeration, JSON/PSObject marshalling) added on top of
	/// <c>ContentPullChunkSize x InspecCheckTimeoutSeconds</c> when computing each
	/// chunk's own <c>PowerShellRequest.Timeout</c> -- generous relative to the
	/// sub-second cost these operations actually take, so a slow CI runner does not
	/// make an otherwise-healthy chunk look like a timeout.
	/// </summary>
	public int ContentPullChunkOverheadSeconds { get; set; } = 30;

	/// <summary>
	/// Issue #993: fixed bound for the git-only <c>Sync-WaypointComplianceContentTree</c>
	/// invocation (clone/fetch/checkout + directory enumeration, no `inspec check`
	/// involved) -- independent of content size in the same sense a `git clone` of a
	/// given repository does not get slower as more `inspec check` work exists to do
	/// later; a genuinely oversized repository clone is a distinct, rarer failure mode
	/// this issue does not attempt to bound differently than before.
	/// </summary>
	public TimeSpan ContentSyncTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
