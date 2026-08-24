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

namespace Waypoint.Core.Catalog;

/// <summary>Terminal outcomes <c>catalog_pull_state.last_outcome</c> records (migration 0049).</summary>
public static class CatalogPullOutcomes
{
	public const string Succeeded = "succeeded";
	public const string Failed = "failed";
	public const string AuthFailed = "auth_failed";
}

/// <summary>
/// The <c>catalog_pull_state</c> singleton row (issue #687, migration 0049): the most
/// recent connected vendor catalog-pull attempt's outcome, plus the last genuinely
/// successful remote refresh's timestamp and item count -- kept distinct from
/// <c>depot_artifacts.indexed_at</c>, which also advances on a purely local,
/// credential-free re-index (<c>CatalogIndexJobHandler</c>, issue #690 AC) and so
/// cannot by itself answer "when did we last actually reach Broadcom."
/// </summary>
public sealed record CatalogPullState(
	DateTimeOffset? LastAttemptAt,
	string? LastOutcome,
	string? LastFailureReason,
	DateTimeOffset? LastSuccessAt,
	int? LastSuccessItemCount,
	DateTimeOffset UpdatedAt);

/// <summary>Storage for the <c>catalog_pull_state</c> singleton (issue #687, mirrors <see cref="Waypoint.Core.Downloads.IDepotEnrollmentRepository"/>'s one-implementation convention).</summary>
public interface ICatalogPullStateRepository
{
	/// <summary>Reads the singleton row. Migration 0049 seeds it unconditionally (<c>id = 1</c>); a null return means the row was deleted out of band.</summary>
	Task<CatalogPullState?> GetAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Records a genuinely successful remote refresh: the authenticated vendor
	/// catalog was reached and <paramref name="itemCount"/> artifacts were indexed
	/// from it (0 is a legitimate, honest value -- issue #687 AC). Advances both the
	/// last-attempt and last-success facts together.
	/// </summary>
	Task RecordSuccessAsync(int itemCount, CancellationToken cancellationToken);

	/// <summary>
	/// Records a failed attempt (runner/tool/auth/parse/cleanup failure) without
	/// touching the prior <c>last_success_at</c>/<c>last_success_item_count</c> --
	/// prior-good preservation applies to the reported state, not only the on-disk
	/// catalog (issue #687 AC).
	/// </summary>
	Task RecordFailureAsync(bool isAuthFailure, string failureReason, CancellationToken cancellationToken);
}
