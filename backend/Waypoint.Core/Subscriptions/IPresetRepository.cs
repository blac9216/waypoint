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

namespace Waypoint.Core.Subscriptions;

/// <summary>
/// Persists <see cref="Preset"/> rows (migration 0104, issue #1421): shipped
/// read-only content and operator clone-to-custom rows. No API surface -- that is
/// #1450/#1453's slice.
/// </summary>
public interface IPresetRepository
{
	/// <summary>Inserts a new preset (shipped or custom) and returns its assigned id.</summary>
	Task<Guid> CreateAsync(Preset preset, CancellationToken cancellationToken);

	/// <summary>Fetches one preset by id, or <c>null</c> when no such row exists.</summary>
	Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>All presets, ordered by <c>created_at</c> for deterministic listing.</summary>
	Task<IReadOnlyList<Preset>> ListAllAsync(CancellationToken cancellationToken);

	/// <summary>Only shipped, read-only presets (<c>is_custom = false</c>), ordered by <c>created_at</c>.</summary>
	Task<IReadOnlyList<Preset>> ListShippedAsync(CancellationToken cancellationToken);

	/// <summary>Deletes one preset by id (a custom clone; a shipped preset is appliance-managed content and is never deleted through this path). A no-op when the row does not exist.</summary>
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
