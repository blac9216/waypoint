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
/// One discovered Photon RPM repo directory (migration 0129's <c>photon_repo_index</c>):
/// version (branch) x <see cref="PhotonRepoVariants"/> x <see cref="PhotonArches"/>,
/// identity-keyed on that triple. <see cref="HasRepodata"/> false marks a
/// <c>photon_snapshots</c>-shaped directory with no <c>repodata/repomd.xml</c> --
/// <see cref="RepomdRevision"/> and <see cref="PackageCount"/> are then both null.
/// Carries no package bytes and no per-package rows -- this is a repo-level summary
/// (package count, change-detection revision), never a download.
/// </summary>
public sealed record PhotonRepoIndexEntry(
	string Version,
	string Variant,
	string Arch,
	string BaseUrl,
	bool HasRepodata,
	string? RepomdRevision,
	int? PackageCount)
{
	/// <summary>Null until read back from storage, which assigns the row's identity.</summary>
	public Guid? Id { get; init; }

	public DateTimeOffset? DiscoveredAt { get; init; }

	public DateTimeOffset? LastSeenAt { get; init; }
}
