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
/// Storage for the <c>download_retention_policies</c> table (migration 0107, issue
/// #1406). One implementation
/// (<c>Waypoint.Infrastructure.Downloads.RetentionPolicyRepository</c>, plain
/// Npgsql -- same "no ORM for this layer" convention as
/// <c>Waypoint.Infrastructure.Downloads.DownloadRepository</c>).
/// </summary>
public interface IRetentionPolicyRepository
{
	/// <summary>
	/// Creates or replaces the policy for <paramref name="scopeKey"/> (upsert on the
	/// unique <c>scope_key</c>). Use <see cref="RetentionPolicyScopes.Default"/> to
	/// update the seeded appliance-wide fallback.
	/// </summary>
	Task<Guid> UpsertAsync(
		string scopeKey,
		int gracePeriodDays,
		int graceMaxRefreshes,
		string manualDownloadDialDefault,
		CancellationToken cancellationToken);

	Task<RetentionPolicy?> GetAsync(Guid id, CancellationToken cancellationToken);

	Task<RetentionPolicy?> GetByScopeKeyAsync(string scopeKey, CancellationToken cancellationToken);

	Task<IReadOnlyList<RetentionPolicy>> ListAsync(CancellationToken cancellationToken);
}
