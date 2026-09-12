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

namespace Waypoint.Core.Subscriptions;

/// <summary>
/// Computes one subscription's fetch set against a candidate catalog slice (issue
/// #1472 AC1/AC4): every <paramref name="candidateArtifacts"/> row matching the
/// subscription's product, NOT already <see cref="DepotArtifactStatuses.Present"/>, and
/// whose version is <see cref="SubscriptionLineMembership.InLine"/> per
/// <see cref="ISubscriptionLineEvaluator"/>. Split out of
/// <see cref="SubscriptionEvaluationService"/> as its own injectable seam so the
/// lib.json short-circuit (AC2, "provable via a unit test asserting the diff method is
/// not called") has something concrete to assert against.
/// </summary>
public interface ISubscriptionFetchSetCalculator
{
	/// <summary>
	/// Filters and orders <paramref name="candidateArtifacts"/> into the subscription's
	/// fetch set. Deterministic and side-effect-free -- no catalog/network access.
	/// </summary>
	IReadOnlyList<SubscriptionFetchItem> ComputeFetchSet(Subscription subscription, IReadOnlyList<DepotArtifact> candidateArtifacts);
}

/// <inheritdoc cref="ISubscriptionFetchSetCalculator"/>
public sealed class SubscriptionFetchSetCalculator : ISubscriptionFetchSetCalculator
{
	private readonly ISubscriptionLineEvaluator _lineEvaluator;

	public SubscriptionFetchSetCalculator(ISubscriptionLineEvaluator lineEvaluator)
	{
		ArgumentNullException.ThrowIfNull(lineEvaluator);
		_lineEvaluator = lineEvaluator;
	}

	public IReadOnlyList<SubscriptionFetchItem> ComputeFetchSet(Subscription subscription, IReadOnlyList<DepotArtifact> candidateArtifacts)
	{
		ArgumentNullException.ThrowIfNull(subscription);
		ArgumentNullException.ThrowIfNull(candidateArtifacts);

		List<SubscriptionFetchItem> items = [];
		foreach (DepotArtifact artifact in candidateArtifacts)
		{
			if (!string.Equals(artifact.Product, subscription.Product, StringComparison.Ordinal))
			{
				continue;
			}

			// Already downloaded -- nothing to fetch, regardless of line membership.
			if (string.Equals(artifact.Status, DepotArtifactStatuses.Present, StringComparison.Ordinal))
			{
				continue;
			}

			if (artifact.Version is null)
			{
				continue;
			}

			SubscriptionLineEvaluation evaluation = _lineEvaluator.Evaluate(
				subscription.AnchorVersion, subscription.Product, subscription.LineGranularity, artifact.Version, candidateCatalogReleaseDate: null);
			if (evaluation.Membership != SubscriptionLineMembership.InLine)
			{
				continue;
			}

			items.Add(new SubscriptionFetchItem(artifact.Id, artifact.ExternalId, artifact.Version, artifact.BundleId, artifact.SizeBytes));
		}

		// Deterministic order for callers that fan out one job per item (issue #1472
		// AC3) -- ordinal on the depot-relative identity, never insertion order off an
		// unordered catalog read.
		items.Sort((a, b) => string.CompareOrdinal(a.ExternalId, b.ExternalId));
		return items;
	}
}
