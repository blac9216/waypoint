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
/// Persists <see cref="Subscription"/> rows (migration 0104, issue #1421). No
/// evaluation, scheduling, or API surface -- those are #1046/#1450/#1453's slices.
/// </summary>
public interface ISubscriptionRepository
{
	/// <summary>Inserts a new subscription and returns its assigned id.</summary>
	Task<Guid> CreateAsync(Subscription subscription, CancellationToken cancellationToken);

	/// <summary>Fetches one subscription by id, or <c>null</c> when no such row exists.</summary>
	Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>All subscriptions, ordered by <c>created_at</c> for deterministic listing.</summary>
	Task<IReadOnlyList<Subscription>> ListAllAsync(CancellationToken cancellationToken);

	/// <summary>Every enabled subscription for one lane, ordered by <c>created_at</c> -- the evaluation job's (#1046) expected read shape.</summary>
	Task<IReadOnlyList<Subscription>> ListEnabledByLaneAsync(string lane, CancellationToken cancellationToken);

	/// <summary>Deletes one subscription by id. A no-op (not an error) when the row does not exist.</summary>
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
