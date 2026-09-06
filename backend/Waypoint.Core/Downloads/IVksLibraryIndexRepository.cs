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
/// Persists the shared VKS/VKR library item index (migration 0111, issue #1480):
/// <see cref="VksLibraryItem"/> rows from either backend, keyed on (source, name).
/// </summary>
public interface IVksLibraryIndexRepository
{
	/// <summary>
	/// Upserts each item keyed on <c>(source, name)</c> (migration 0111's unique
	/// constraint): a first-seen (source, name) pair inserts a new row with
	/// <c>discovered_at</c> and <c>last_seen_at</c> both set to the item's own
	/// timestamps; a previously seen pair only advances <c>last_seen_at</c> and
	/// refreshes its dimensions/etag/sha256/size, leaving <c>discovered_at</c>
	/// untouched. Re-indexing an unchanged listing therefore never inserts a
	/// duplicate row.
	/// </summary>
	Task UpsertItemsAsync(IReadOnlyCollection<VksLibraryItem> items, CancellationToken cancellationToken);

	/// <summary>All indexed items, ordered by <c>name</c> for deterministic assertions.</summary>
	Task<IReadOnlyList<VksLibraryItem>> GetItemsAsync(CancellationToken cancellationToken);
}
