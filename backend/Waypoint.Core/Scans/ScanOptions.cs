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

namespace Waypoint.Core.Scans;

/// <summary>Configuration for the <c>scan</c> job handler's InSpec/attest/convert stages (issues #274/#275, second and third slices of the #23 split).</summary>
public sealed class ScanOptions
{
	public const string SectionName = "Scans";

	/// <summary>
	/// Root directory HDF reports land in -- a sibling volume to
	/// <see cref="Waypoint.Core.Downloads.DownloadOptions.ArtifactStorePath"/>'s
	/// download artifacts, kept as its own configurable root rather than reusing that
	/// path directly: scan HDF artifacts are keyed by job id, not depot external id, and
	/// nothing about this stage needs the download store's quarantine subdirectory
	/// convention. The convert stage (#275) writes the CKL next to the HDF under this
	/// same root, keyed by job id (<c>{job-id}.ckl</c>).
	/// </summary>
	public string ArtifactStorePath { get; set; } = "/var/lib/waypoint/artifacts/scans";

	/// <summary>
	/// Root directory of compliance content (InSpec profiles). This M1 slice scans a
	/// single fixed vSphere profile per target kind; a real profile-selection surface
	/// (per docs/api-contract.md's <c>/profiles</c>) is out of scope here -- see #274's
	/// PR body.
	/// </summary>
	public string ProfilePath { get; set; } = "/opt/waypoint/profiles/vsphere";

	/// <summary>Per-invocation InSpec wall-clock budget.</summary>
	public int TimeoutSeconds { get; set; } = 1800;

	/// <summary>
	/// The <c>/config-docs</c> <c>profile</c> key attestations resolve against for this
	/// M1 slice's fixed vSphere target (#275). Same fixed-profile scope call as
	/// <see cref="ProfilePath"/> -- a real per-target/per-component profile mapping is
	/// the same future <c>/profiles</c> surface both defer to.
	/// </summary>
	public string AttestationProfile { get; set; } = "vsphere-stig";

	/// <summary>
	/// Per-invocation SAF CLI (attest/convert) wall-clock budget. Separate from
	/// <see cref="TimeoutSeconds"/> because the SAF steps run against a local file, not
	/// a network target, and are expected to complete far faster than an InSpec scan.
	/// </summary>
	public int SafTimeoutSeconds { get; set; } = 300;

	/// <summary>
	/// Static CKL benchmark metadata stamped onto the <c>STIG_INFO</c> header during the
	/// convert stage (#275 AC "benchmark metadata"), keyed by <see cref="Waypoint.Core.Sites.TargetKinds"/>.
	/// A real per-benchmark lookup synced from STIG Manager (the sibling repo's
	/// <c>Resolve-BenchmarkMetadata</c>) is out of scope here -- that resolver requires a
	/// live STIG Manager connection, which is #25's integration, not this slice's. This
	/// M1 fixed map is the same "single fixed profile" scope call as
	/// <see cref="ProfilePath"/>/<see cref="AttestationProfile"/>: it stamps a static,
	/// operator-configured identity onto the CKL header so STIG Manager import has
	/// something to match against, without depending on #25 being built first.
	/// </summary>
	public Dictionary<string, ScanBenchmarkMetadata> BenchmarkMetadata { get; } = [];
}

/// <summary>One target kind's static CKL <c>STIG_INFO</c> header identity (see <see cref="ScanOptions.BenchmarkMetadata"/>).</summary>
public sealed class ScanBenchmarkMetadata
{
	public string? BenchmarkId { get; set; }

	public string? Title { get; set; }

	public string? ReleaseInfo { get; set; }

	public string? Version { get; set; }
}
