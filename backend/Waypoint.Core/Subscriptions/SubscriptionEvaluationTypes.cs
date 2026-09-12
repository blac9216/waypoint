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
/// One catalog artifact the evaluation job (#1472, epic #1182) has determined belongs
/// to a subscription's fetch set: in-line per <see cref="ISubscriptionLineEvaluator"/>
/// and not yet <see cref="Waypoint.Core.Catalog.DepotArtifactStatuses.Present"/>.
/// <see cref="BundleId"/> mirrors <see cref="Waypoint.Core.Catalog.DepotArtifact.BundleId"/>
/// -- null when the row predates migration 0132 or was written by the offline disk
/// walk, in which case the fan-out step skips it (nothing usable to pass as the real
/// tool's <c>--id</c>, the same #1783 rule <c>DownloadsController.QueueBinariesDownload</c>
/// enforces at request time).
/// </summary>
public sealed record SubscriptionFetchItem(Guid DepotArtifactId, string ExternalId, string? Version, string? BundleId, long? SizeBytes);

/// <summary>
/// The evaluation job's answer for one subscription. <see cref="Skipped"/> is true only
/// for the lib.json version-counter short-circuit (issue #1472 AC2) -- a subscription
/// with a non-empty <see cref="FetchSet"/> is never also <see cref="Skipped"/>.
/// <see cref="ProjectedBytes"/> is <c>null</c> whenever any fetch-set item's catalog
/// size is unknown (issue #1472 Risks: "a missing size should surface as an honest
/// 'unknown' rather than a silently-wrong total"), never a partial sum.
/// <see cref="VersionCounter"/> is the lib.json counter value this evaluation observed
/// (or, on a skip, the still-current one) -- null for a non-library-mirror lane, which
/// never runs the pre-check at all.
/// </summary>
public sealed record SubscriptionEvaluationOutcome(
	Guid SubscriptionId,
	bool Skipped,
	string? SkipReason,
	IReadOnlyList<SubscriptionFetchItem> FetchSet,
	long? ProjectedBytes,
	long? VersionCounter);

/// <summary>
/// Persisted per-subscription evaluation state (migration <c>subscription_evaluation_state</c>,
/// issue #1472): the lib.json version counter and result summary observed at the last
/// evaluation, read back on the next run so a restart (or an unrelated evaluation of a
/// different subscription) never forces a redundant full diff -- see
/// <see cref="ILibraryVersionCounterGateway"/>'s doc comment.
/// </summary>
public sealed record SubscriptionEvaluationState(
	Guid SubscriptionId,
	long? LastSeenLibVersionCounter,
	DateTimeOffset? LastEvaluatedAt,
	int LastFetchSetCount,
	long? LastProjectedBytes);

/// <summary>
/// Storage for <see cref="SubscriptionEvaluationState"/> (one row per subscription, ON
/// DELETE CASCADE off <c>subscriptions.id</c>). One implementation
/// (<c>Waypoint.Infrastructure.Subscriptions.SubscriptionEvaluationStateRepository</c>,
/// plain Npgsql, matching every other repository in this codebase).
/// </summary>
public interface ISubscriptionEvaluationStateRepository
{
	/// <summary>The subscription's last-recorded evaluation state, or <c>null</c> when it has never been evaluated.</summary>
	Task<SubscriptionEvaluationState?> GetAsync(Guid subscriptionId, CancellationToken cancellationToken);

	/// <summary>Upserts by <see cref="SubscriptionEvaluationState.SubscriptionId"/> -- one row per subscription, always the latest evaluation's facts.</summary>
	Task UpsertAsync(SubscriptionEvaluationState state, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces the persisted fetch set for a fresh (not-yet-fanned-out) evaluation --
	/// clears any prior <c>fanned_out_at</c> marker, since a new evaluation's fetch set
	/// has never been consumed. Requires an existing row (the caller always calls
	/// <see cref="UpsertAsync"/> first in the same evaluation).
	/// </summary>
	Task SetFetchSetAsync(Guid subscriptionId, IReadOnlyList<SubscriptionFetchItem> fetchSet, CancellationToken cancellationToken);

	/// <summary>
	/// The subscription's persisted fetch set, or <c>null</c> when there is none, it is
	/// empty, or it was already consumed (<c>fanned_out_at</c> set) -- the single read
	/// <c>SubscriptionEvaluationFanOutService</c> uses to decide whether there is
	/// anything left to fan out.
	/// </summary>
	Task<IReadOnlyList<SubscriptionFetchItem>?> GetPendingFetchSetAsync(Guid subscriptionId, CancellationToken cancellationToken);

	/// <summary>Marks the subscription's currently-persisted fetch set consumed -- idempotent, a no-op if already marked.</summary>
	Task MarkFannedOutAsync(Guid subscriptionId, CancellationToken cancellationToken);
}
