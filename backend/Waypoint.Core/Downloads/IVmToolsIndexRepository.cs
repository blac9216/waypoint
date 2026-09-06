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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Persists the VMTools mirror index (migration 0109, issue #1392):
/// <see cref="VmToolsArtifact"/> rows from the crawler and
/// <see cref="VmToolsEsxVersionMapping"/> rows from the <c>versions</c>-file parser.
/// </summary>
public interface IVmToolsIndexRepository
{
	/// <summary>
	/// Upserts each artifact keyed on <c>(relative_path, etag)</c> (migration 0109's
	/// unique constraint): a first-seen path/etag pair inserts a new row with
	/// <c>first_seen_at</c> and <c>last_seen_at</c> both set to the artifact's own
	/// timestamps; a previously seen pair only advances <c>last_seen_at</c>, leaving
	/// <c>first_seen_at</c> untouched. Re-running against an unchanged fixture tree
	/// (issue AC3) therefore never inserts a duplicate row.
	/// </summary>
	Task UpsertArtifactsAsync(IReadOnlyCollection<VmToolsArtifact> artifacts, CancellationToken cancellationToken);

	/// <summary>All indexed artifacts, ordered by <c>relative_path</c> for deterministic assertions.</summary>
	Task<IReadOnlyList<VmToolsArtifact>> GetArtifactsAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Upserts each mapping keyed on <c>(esxi_version_dir, tools_version_code)</c>
	/// (migration 0109's unique constraint) -- a re-parse of an unchanged
	/// <c>versions</c> file touches rather than duplicates a row.
	/// </summary>
	Task UpsertVersionMappingsAsync(IReadOnlyCollection<VmToolsEsxVersionMapping> mappings, CancellationToken cancellationToken);

	/// <summary>All indexed version mappings, ordered by <c>sequence_in_file</c> -- the upstream file's own newest-first-by-ESXi-build order (issue AC2's release-recency fallback).</summary>
	Task<IReadOnlyList<VmToolsEsxVersionMapping>> GetVersionMappingsAsync(CancellationToken cancellationToken);
}
