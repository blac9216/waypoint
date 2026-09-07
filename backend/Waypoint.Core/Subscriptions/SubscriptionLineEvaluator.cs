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

using Waypoint.Core.Versions;

namespace Waypoint.Core.Subscriptions;

/// <inheritdoc cref="ISubscriptionLineEvaluator"/>
public sealed class SubscriptionLineEvaluator : ISubscriptionLineEvaluator
{
	public SubscriptionLineEvaluation Evaluate(
		string anchorVersion,
		string? product,
		SubscriptionLineGranularity granularity,
		string candidateVersion,
		DateTimeOffset? candidateCatalogReleaseDate)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(anchorVersion);
		ArgumentException.ThrowIfNullOrWhiteSpace(candidateVersion);

		// Quarantine is decided first and unconditionally -- a quarantined candidate
		// can never become "in line" no matter the granularity, including
		// whole-release (issue #1421 AC3, epic #16 decision R2-5).
		ClassifiedProductVersion candidate = ProductVersionClassifier.Classify(candidateVersion, product, candidateCatalogReleaseDate);
		if (candidate.Outcome == VersionClassificationOutcome.Quarantined)
		{
			return new SubscriptionLineEvaluation(SubscriptionLineMembership.Quarantined, AnchorLine: null, CandidateLine: null);
		}

		// whole-release tracks every non-quarantined release of the product/lane --
		// no numeric line boundary at all, so there is nothing further to compare.
		if (granularity == SubscriptionLineGranularity.WholeRelease)
		{
			return new SubscriptionLineEvaluation(SubscriptionLineMembership.InLine, AnchorLine: null, CandidateLine: null);
		}

		VersionLineGranularity lineGranularity = ToVersionLineGranularity(granularity);

		// A date-ordered candidate (unparseable but dated) has no numeric segments to
		// extract a line from at a numeric granularity -- it can never be judged
		// in-line against a subminor/minor/major anchor, only against whole-release.
		if (candidate.Outcome != VersionClassificationOutcome.Parsed || candidate.Parsed is null)
		{
			return new SubscriptionLineEvaluation(SubscriptionLineMembership.OutOfLine, AnchorLine: null, CandidateLine: null);
		}

		ProductVersion anchor = ProductVersionParser.Parse(anchorVersion, product);
		string? anchorLine = VersionLineExtractor.TryGetLine(anchor, lineGranularity);
		string? candidateLine = VersionLineExtractor.TryGetLine(candidate.Parsed, lineGranularity);

		if (anchorLine is null || candidateLine is null)
		{
			// Either the anchor itself does not parse to enough segments, or the
			// candidate does not -- never fabricated, so this can only be OutOfLine.
			return new SubscriptionLineEvaluation(SubscriptionLineMembership.OutOfLine, anchorLine, candidateLine);
		}

		SubscriptionLineMembership membership = string.Equals(anchorLine, candidateLine, StringComparison.Ordinal)
			? SubscriptionLineMembership.InLine
			: SubscriptionLineMembership.OutOfLine;
		return new SubscriptionLineEvaluation(membership, anchorLine, candidateLine);
	}

	private static VersionLineGranularity ToVersionLineGranularity(SubscriptionLineGranularity granularity) => granularity switch
	{
		SubscriptionLineGranularity.Subminor => VersionLineGranularity.Subminor,
		SubscriptionLineGranularity.Minor => VersionLineGranularity.Minor,
		SubscriptionLineGranularity.Major => VersionLineGranularity.Major,
		_ => throw new ArgumentOutOfRangeException(nameof(granularity), granularity, "WholeRelease is handled before this call and has no VersionLineGranularity counterpart."),
	};
}
