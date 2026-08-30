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

/// <summary>
/// The single source of truth for the scan artifact store's on-disk naming convention
/// (issue #299), matching the three inline path expressions <c>ScanJobHandler</c>
/// (issue #275) already writes: raw HDF at <c>{job-id:N}.json</c>, the attested HDF at
/// <c>{job-id:N}.attested.json</c>, and the CKL at <c>{job-id:N}.ckl</c>, all flat under
/// <see cref="ScanOptions.ArtifactStorePath"/> (no per-job subdirectory). Kept as its own
/// static helper rather than duplicating the three <c>Path.Combine</c> calls a second
/// time in the REST surface -- <c>ScanJobHandler</c>'s own call sites are left as they
/// are (out of scope here) rather than retrofitted to share this, to keep this issue's
/// diff additive-only.
/// </summary>
public static class ScanArtifactPaths
{
	/// <summary>
	/// Issue #1240 / #1312: the fixed number of on-disk paths this convention names
	/// per scan job, regardless of which ones actually exist on disk -- derived from
	/// <see cref="AllForJob"/>'s own length rather than hand-copied, so it cannot
	/// silently desync from the set <c>PurgeJobHandler</c> actually enumerates and
	/// deletes (a missing file there is a tolerated no-op, not a skip). This is the
	/// single source of truth for converting a scan-job count into the
	/// artifact-**file** count the purge lifecycle reports, letting
	/// <c>RunPurgeService</c> compute the same unit at enqueue time that the handler
	/// consumes.
	/// </summary>
	public static readonly int FilesPerJob = AllForJob(string.Empty, Guid.Empty).Length;

	/// <summary>Raw (pre-attest) HDF report path for a job.</summary>
	public static string RawHdf(string artifactStorePath, Guid jobId) =>
		Path.Combine(artifactStorePath, $"{jobId:N}.json");

	/// <summary>Attested HDF report path for a job (only exists once the attest stage has run).</summary>
	public static string AttestedHdf(string artifactStorePath, Guid jobId) =>
		Path.Combine(artifactStorePath, $"{jobId:N}.attested.json");

	/// <summary>CKL checklist path for a job (only exists once the convert stage has run).</summary>
	public static string Ckl(string artifactStorePath, Guid jobId) =>
		Path.Combine(artifactStorePath, $"{jobId:N}.ckl");

	/// <summary>
	/// Issue #1312: every on-disk artifact path this convention names for one job, in
	/// the exact set <c>PurgeJobHandler</c> iterates to delete them -- the single array
	/// both that handler and <see cref="FilesPerJob"/> are derived from, so a fourth
	/// path kind added here changes both by construction instead of requiring a
	/// hand-copied constant bump.
	/// </summary>
	public static string[] AllForJob(string artifactStorePath, Guid jobId) =>
	[
		RawHdf(artifactStorePath, jobId),
		AttestedHdf(artifactStorePath, jobId),
		Ckl(artifactStorePath, jobId),
	];

	/// <summary>
	/// The HDF this job's convert stage actually consumed and the "best available" HDF for
	/// download/inspection purposes -- the attested report when the attest stage produced
	/// one, otherwise the raw report (same fallback <c>ScanJobHandler.ExecuteConvertStageAsync</c>
	/// uses). Returns <c>null</c> when neither file exists on disk.
	/// </summary>
	public static string? ResolveHdf(string artifactStorePath, Guid jobId)
	{
		string attested = AttestedHdf(artifactStorePath, jobId);
		if (File.Exists(attested))
		{
			return attested;
		}

		string raw = RawHdf(artifactStorePath, jobId);
		return File.Exists(raw) ? raw : null;
	}
}
