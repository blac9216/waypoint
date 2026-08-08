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

/// <summary>Configuration for the <c>scan</c> job handler's InSpec stage (issue #274, second slice of the #23 split).</summary>
public sealed class ScanOptions
{
	public const string SectionName = "Scans";

	/// <summary>
	/// Root directory HDF reports land in -- a sibling volume to
	/// <see cref="Waypoint.Core.Downloads.DownloadOptions.ArtifactStorePath"/>'s
	/// download artifacts, kept as its own configurable root rather than reusing that
	/// path directly: scan HDF artifacts are keyed by job id, not depot external id, and
	/// nothing about this stage needs the download store's quarantine subdirectory
	/// convention.
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
}
