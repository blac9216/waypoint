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
/// Persists the Photon lane's discovered index (migration 0130). Issue #1790 added the
/// image-tree half (<c>photon_image_index</c>) alongside the RPM-repo half #1509
/// shipped; <c>photon_subscription_config</c> still has no reader or writer (the
/// sync/subscription lane, a separate issue) -- this interface grows a matching method
/// the moment that lands, following this repo's one-repository-per-domain-table
/// convention.
/// </summary>
public interface IPhotonIndexRepository
{
	/// <summary>
	/// Inserts a new row, or updates the existing row for the same
	/// (<see cref="PhotonRepoIndexEntry.Version"/>, <see cref="PhotonRepoIndexEntry.Variant"/>,
	/// <see cref="PhotonRepoIndexEntry.Arch"/>) triple -- re-discovery of an unchanged
	/// upstream repo touches <c>last_seen_at</c> and overwrites the mutable fields
	/// (<c>base_url</c>, <c>has_repodata</c>, <c>repomd_revision</c>, <c>package_count</c>)
	/// without inserting a duplicate row or disturbing <c>discovered_at</c>.
	/// </summary>
	Task UpsertRepoIndexEntryAsync(PhotonRepoIndexEntry entry, CancellationToken cancellationToken);

	/// <summary>Every currently-indexed repo row, for tests and the future read API.</summary>
	Task<IReadOnlyList<PhotonRepoIndexEntry>> ListRepoIndexEntriesAsync(CancellationToken cancellationToken);

	/// <summary>
	/// The single row for one (version, variant, arch) triple, or <c>null</c> if never
	/// discovered -- the point-lookup counterpart to <see cref="ListRepoIndexEntriesAsync"/>,
	/// used by tests that need to assert on one row without being sensitive to
	/// whatever else the shared test database happens to hold.
	/// </summary>
	Task<PhotonRepoIndexEntry?> GetRepoIndexEntryAsync(string version, string variant, string arch, CancellationToken cancellationToken);

	/// <summary>
	/// Inserts a new row, or updates the existing row for the same
	/// (<see cref="PhotonImageIndexEntry.Version"/>, <see cref="PhotonImageIndexEntry.Channel"/>,
	/// <see cref="PhotonImageIndexEntry.RelativePath"/>) triple -- re-discovery of an
	/// unchanged upstream image touches <c>last_seen_at</c> and overwrites
	/// <c>size_bytes</c>/<c>etag</c> without inserting a duplicate row or disturbing
	/// <c>discovered_at</c> (this issue's idempotent-re-run AC).
	/// </summary>
	Task UpsertImageIndexEntryAsync(PhotonImageIndexEntry entry, CancellationToken cancellationToken);

	/// <summary>Every currently-indexed image row, for tests and the future read API.</summary>
	Task<IReadOnlyList<PhotonImageIndexEntry>> ListImageIndexEntriesAsync(CancellationToken cancellationToken);

	/// <summary>
	/// The single row for one (version, channel, relative path) triple, or <c>null</c>
	/// if never discovered -- the point-lookup counterpart to
	/// <see cref="ListImageIndexEntriesAsync"/>.
	/// </summary>
	Task<PhotonImageIndexEntry?> GetImageIndexEntryAsync(string version, string channel, string relativePath, CancellationToken cancellationToken);
}
