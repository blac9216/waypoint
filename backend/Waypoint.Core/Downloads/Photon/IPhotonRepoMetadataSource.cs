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

namespace Waypoint.Core.Downloads.Photon;

/// <summary>
/// The HTTP boundary <c>PhotonRepoDiscoveryJobHandler</c> (in
/// <c>Waypoint.Infrastructure.Execution</c>) is abstracted behind, so a test can feed
/// canned fixture responses instead of a real network -- this repo's convention
/// wherever a job handler needs a vendor HTTP
/// boundary (e.g. <c>IManagedToolDepotFetcher</c>). Every method here is METADATA
/// ONLY: <see cref="GetVersionBranchesAsync"/> reads the small
/// <c>photon_cve_metadata/photon_versions.json</c> document (research #1029 finding
/// 1's tier-1 discovery surface); <see cref="TryGetRepomdRevisionAndPackageCountAsync"/>
/// reads a repo's <c>repodata/repomd.xml</c> and, when present, its referenced
/// <c>primary.xml.gz</c> HEADER content (name/epoch/version/release/arch/size/checksum
/// per package) -- never a package RPM itself. There is deliberately no method on this
/// interface capable of fetching an actual package or image file (this issue's AC 5:
/// "no code path from discovery to file fetch").
/// </summary>
public interface IPhotonRepoMetadataSource
{
	/// <summary>
	/// Returns the Photon version branches published at
	/// <c>&lt;baseUrl&gt;/photon_cve_metadata/photon_versions.json</c> (e.g. <c>["1.0",
	/// "2.0", "3.0", "4.0", "5.0"]</c>), or <c>null</c> if that document is unreachable
	/// or malformed -- the caller then has nothing to enumerate and reports that as the
	/// job's failure, rather than falling back to a hardcoded version list (research
	/// #1029 finding 1's "no hardcoded major versions" design implication).
	/// </summary>
	Task<IReadOnlyList<string>?> GetVersionBranchesAsync(string baseUrl, CancellationToken cancellationToken);

	/// <summary>
	/// Probes <c>&lt;repoBaseUrl&gt;/repodata/repomd.xml</c> for one (version, variant,
	/// arch) repo. A repo with no <c>repodata/</c> at all but whose directory does exist
	/// upstream (a <c>photon_snapshots</c>-shaped directory, research #1029 finding 5) is
	/// a normal, expected outcome -- returned as <see cref="PhotonRepomdProbeResult.NotFound"/>,
	/// never thrown as an exception. A directory that does not exist upstream at all (a
	/// combination this repo's cartesian enumeration guessed but the vendor never
	/// published) is a distinct outcome, <see cref="PhotonRepomdProbeResult.Absent"/> --
	/// a plain 404 on <c>repodata/repomd.xml</c> cannot tell the two apart on its own, so
	/// implementations probe the repo directory itself before classifying either way.
	/// A <see cref="PhotonRepomdProbeResult.NotFound"/> classification is a POSITIVE
	/// observation that this repo has no repodata (the caller writes it as
	/// <c>has_repodata = false</c> over whatever a previous sweep recorded), so an
	/// implementation must only return it on evidence that actually supports the claim:
	/// where the evidence is merely absent -- a 404 on <c>repomd.xml</c> while the
	/// <c>repodata/</c> directory is right there, i.e. upstream mid-regeneration -- the
	/// outcome is <see cref="PhotonRepomdProbeResult.Failed(string)"/>, never
	/// <see cref="PhotonRepomdProbeResult.NotFound"/> (issue #1835 Option B: one
	/// transient sweep must not downgrade a previously-<c>Found</c> row).
	/// </summary>
	Task<PhotonRepomdProbeResult> TryGetRepomdRevisionAndPackageCountAsync(string repoBaseUrl, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of probing one repo's <c>repodata/repomd.xml</c>.
/// <see cref="Kind"/> distinguishes "no repodata here, but the directory exists" (a
/// normal, indexable <c>photon_snapshots</c>-shaped classification) from "the directory
/// itself does not exist upstream" (nothing to index -- no row) from "repodata exists
/// and parsed" and from "something unexpected happened" (network/parse/validation
/// failure -- logged and skipped, never failing the whole discovery job for one bad
/// repo).
/// </summary>
public sealed record PhotonRepomdProbeResult(PhotonRepomdProbeKind Kind, string? Revision, int? PackageCount, string? Error)
{
	public static readonly PhotonRepomdProbeResult NotFound = new(PhotonRepomdProbeKind.NoRepodata, null, null, null);

	public static readonly PhotonRepomdProbeResult Absent = new(PhotonRepomdProbeKind.Absent, null, null, null);

	public static PhotonRepomdProbeResult Found(string revision, int packageCount) =>
		new(PhotonRepomdProbeKind.Found, revision, packageCount, null);

	public static PhotonRepomdProbeResult Failed(string error) =>
		new(PhotonRepomdProbeKind.Error, null, null, error);
}

public enum PhotonRepomdProbeKind
{
	Found,
	NoRepodata,
	Absent,
	Error,
}
