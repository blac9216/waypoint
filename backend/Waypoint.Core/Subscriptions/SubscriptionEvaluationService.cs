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

using Waypoint.Core.Catalog;
using Waypoint.Core.Secrets;

namespace Waypoint.Core.Subscriptions;

/// <summary>
/// Orchestrates one subscription's evaluation (issue #1472, epic #1182): the lib.json
/// version-counter pre-check for a library-mirror lane (AC2), then the fetch-set/
/// projected-bytes diff (AC1) via <see cref="ISubscriptionFetchSetCalculator"/>. Pure
/// with respect to persistence -- it neither reads nor writes
/// <see cref="ISubscriptionEvaluationStateRepository"/> itself; the caller
/// (<c>SubscriptionEvaluationJobHandler</c>) reads the prior state, passes it in, and
/// persists the returned <see cref="SubscriptionEvaluationOutcome"/> afterwards. Kept
/// unit-testable without Postgres, matching <see cref="LibraryPresenceEvaluator"/>'s
/// convention.
/// </summary>
public sealed class SubscriptionEvaluationService
{
	private readonly ISubscriptionFetchSetCalculator _fetchSetCalculator;
	private readonly ILibraryVersionCounterGateway _versionCounterGateway;

	public SubscriptionEvaluationService(ISubscriptionFetchSetCalculator fetchSetCalculator, ILibraryVersionCounterGateway versionCounterGateway)
	{
		ArgumentNullException.ThrowIfNull(fetchSetCalculator);
		ArgumentNullException.ThrowIfNull(versionCounterGateway);
		_fetchSetCalculator = fetchSetCalculator;
		_versionCounterGateway = versionCounterGateway;
	}

	/// <summary>
	/// Evaluates <paramref name="subscription"/>. Only a
	/// <see cref="Waypoint.Core.Secrets.RepoStores.ContentLibraries"/>-lane subscription
	/// ever calls <see cref="ILibraryVersionCounterGateway"/> -- every other lane has no
	/// lib.json to poll and always runs the full diff. A pre-check that fails
	/// (<see cref="LibraryVersionCounterResult.Success"/> false) is treated as "nothing
	/// observed" and never short-circuits the diff (issue #1472 Risks: fail open, never
	/// fail closed and stall the lane).
	/// </summary>
	public Task<SubscriptionEvaluationOutcome> EvaluateAsync(
		Subscription subscription,
		IReadOnlyList<DepotArtifact> candidateArtifacts,
		SubscriptionEvaluationState? priorState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(subscription);
		ArgumentNullException.ThrowIfNull(candidateArtifacts);

		bool isLibraryMirrorLane = string.Equals(subscription.Lane, RepoStores.ContentLibraries, StringComparison.Ordinal);
		return isLibraryMirrorLane
			? EvaluateLibraryMirrorLaneAsync(subscription, candidateArtifacts, priorState, cancellationToken)
			: Task.FromResult(EvaluateDiff(subscription, candidateArtifacts, versionCounter: null));
	}

	private async Task<SubscriptionEvaluationOutcome> EvaluateLibraryMirrorLaneAsync(
		Subscription subscription,
		IReadOnlyList<DepotArtifact> candidateArtifacts,
		SubscriptionEvaluationState? priorState,
		CancellationToken cancellationToken)
	{
		LibraryVersionCounterResult counterResult = await _versionCounterGateway
			.GetVersionCounterAsync(subscription.Product, subscription.Lane, cancellationToken).ConfigureAwait(false);

		if (counterResult.Success
			&& priorState?.LastSeenLibVersionCounter is long previousCounter
			&& counterResult.VersionCounter == previousCounter)
		{
			return new SubscriptionEvaluationOutcome(
				subscription.Id,
				Skipped: true,
				SkipReason: $"lib.json version counter unchanged ({previousCounter}); items.json diff skipped.",
				FetchSet: [],
				ProjectedBytes: null,
				VersionCounter: previousCounter);
		}

		// Success-but-changed, or a failed pre-check (fail open): run the full diff.
		// A failed pre-check carries forward the prior counter untouched rather than
		// fabricating a new one -- the next evaluation gets another chance to observe
		// the real value instead of this failure silently becoming "the" last-seen fact.
		long? versionCounter = counterResult.Success ? counterResult.VersionCounter : priorState?.LastSeenLibVersionCounter;
		return EvaluateDiff(subscription, candidateArtifacts, versionCounter);
	}

	private SubscriptionEvaluationOutcome EvaluateDiff(Subscription subscription, IReadOnlyList<DepotArtifact> candidateArtifacts, long? versionCounter)
	{
		IReadOnlyList<SubscriptionFetchItem> fetchSet = _fetchSetCalculator.ComputeFetchSet(subscription, candidateArtifacts);

		long? projectedBytes;
		if (fetchSet.Count == 0)
		{
			projectedBytes = 0;
		}
		else if (fetchSet.Any(item => item.SizeBytes is null))
		{
			// Issue #1472 Risks: an unknown size must surface as an honest "unknown"
			// total, never a silently-wrong partial sum.
			projectedBytes = null;
		}
		else
		{
			projectedBytes = fetchSet.Sum(item => item.SizeBytes!.Value);
		}

		return new SubscriptionEvaluationOutcome(subscription.Id, Skipped: false, SkipReason: null, fetchSet, projectedBytes, versionCounter);
	}
}
