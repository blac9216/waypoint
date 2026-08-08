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

using Waypoint.Core.Pagination;

namespace Waypoint.Core.Catalog;

/// <summary>
/// Storage for the depot catalog (issue #193, epic #9 slice 1). One implementation
/// (<c>Waypoint.Infrastructure.Catalog.DepotArtifactRepository</c>, plain Npgsql --
/// same "no ORM for this layer" convention as <c>CredentialRepository</c> and
/// <c>JobQueueRepository</c>). The <c>catalog-index</c> handler that populates this
/// store via the real depot tool lands with issue #194; this slice proves the store
/// and the REST surface with invented fixtures.
/// </summary>
public interface IDepotArtifactRepository
{
	/// <summary>
	/// Inserts or updates by <see cref="DepotArtifactUpsert.ExternalId"/> -- the same
	/// identity twice yields one row with the newer payload (issue #193 acceptance
	/// criterion). Returns the row's id.
	/// </summary>
	Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken);

	/// <summary>
	/// Filtered, paginated list plus the filtered total (for <c>X-Total-Count</c> --
	/// the count reflects the filter, not the whole table, per every other paginated
	/// resource in this codebase).
	/// </summary>
	Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(
		DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken);
}
