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

using Waypoint.Core.Subscriptions;
using Xunit;

namespace Waypoint.Tests.Core.Subscriptions;

/// <summary>
/// Issue #1421 AC3: line-membership boundaries for subminor/minor/major/whole-release,
/// including the quarantined-candidate delegation case. Fixture version strings are
/// invented (repo convention, never lab/vendor-exported values).
/// </summary>
public sealed class SubscriptionLineEvaluatorTests
{
	private readonly SubscriptionLineEvaluator _evaluator = new();

	[Theory]
	[InlineData("11.4.7", "11.4.7.900", SubscriptionLineGranularity.Subminor, SubscriptionLineMembership.InLine)]
	[InlineData("11.4.7", "11.4.8.900", SubscriptionLineGranularity.Subminor, SubscriptionLineMembership.OutOfLine)]
	[InlineData("11.4.7", "11.4.900", SubscriptionLineGranularity.Minor, SubscriptionLineMembership.InLine)]
	[InlineData("11.4.7", "11.5.0", SubscriptionLineGranularity.Minor, SubscriptionLineMembership.OutOfLine)]
	[InlineData("11.4.7", "11.9.2", SubscriptionLineGranularity.Major, SubscriptionLineMembership.InLine)]
	[InlineData("11.4.7", "12.0.0", SubscriptionLineGranularity.Major, SubscriptionLineMembership.OutOfLine)]
	[InlineData("11.4.7", "97.13.2", SubscriptionLineGranularity.WholeRelease, SubscriptionLineMembership.InLine)]
	public void Evaluate_ParsedCandidate_MatchesExpectedMembership(
		string anchor, string candidate, SubscriptionLineGranularity granularity, SubscriptionLineMembership expected)
	{
		SubscriptionLineEvaluation result = _evaluator.Evaluate(anchor, product: null, granularity, candidate, candidateCatalogReleaseDate: null);

		Assert.Equal(expected, result.Membership);
	}

	[Theory]
	[InlineData(SubscriptionLineGranularity.Subminor)]
	[InlineData(SubscriptionLineGranularity.Minor)]
	[InlineData(SubscriptionLineGranularity.Major)]
	public void Evaluate_QuarantinedCandidate_NeverRanksInLine(SubscriptionLineGranularity granularity)
	{
		// Undated AND unparseable -- epic #16 decision R2-5's quarantine rung.
		SubscriptionLineEvaluation result = _evaluator.Evaluate(
			"11.4.7", product: null, granularity, candidateVersion: "N/A", candidateCatalogReleaseDate: null);

		Assert.Equal(SubscriptionLineMembership.Quarantined, result.Membership);
	}

	[Fact]
	public void Evaluate_QuarantinedCandidate_NeverRanksInLine_EvenAtWholeRelease()
	{
		SubscriptionLineEvaluation result = _evaluator.Evaluate(
			"11.4.7", product: null, SubscriptionLineGranularity.WholeRelease, candidateVersion: "TBD", candidateCatalogReleaseDate: null);

		Assert.Equal(SubscriptionLineMembership.Quarantined, result.Membership);
	}

	[Fact]
	public void Evaluate_UndatedUnparseableCandidate_DelegatesToClassifierQuarantine_NotSilentlyIncluded()
	{
		// A caller must never treat "we don't know" as "in scope" -- the whole point
		// of delegating to ProductVersionClassifier rather than reimplementing.
		SubscriptionLineEvaluation result = _evaluator.Evaluate(
			"11.4.7", product: null, SubscriptionLineGranularity.Major, candidateVersion: "garbage-string", candidateCatalogReleaseDate: null);

		Assert.Equal(SubscriptionLineMembership.Quarantined, result.Membership);
		Assert.NotEqual(SubscriptionLineMembership.InLine, result.Membership);
	}

	[Fact]
	public void Evaluate_DatedButUnparseableCandidate_FallsBackToDateOrdering_NeverInLineAtNumericGranularity()
	{
		// Unparseable-but-dated (R2-5's middle rung) has no numeric line to compare,
		// so it can never be judged in-line against a subminor/minor/major anchor.
		SubscriptionLineEvaluation result = _evaluator.Evaluate(
			"11.4.7", product: null, SubscriptionLineGranularity.Major, candidateVersion: "Build-Fall2026",
			candidateCatalogReleaseDate: DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

		Assert.Equal(SubscriptionLineMembership.OutOfLine, result.Membership);
	}

	[Fact]
	public void Evaluate_UnparseableAnchor_NeverFabricatesALine_YieldsOutOfLine()
	{
		SubscriptionLineEvaluation result = _evaluator.Evaluate(
			"N/A", product: null, SubscriptionLineGranularity.Minor, candidateVersion: "11.4.7", candidateCatalogReleaseDate: null);

		Assert.Equal(SubscriptionLineMembership.OutOfLine, result.Membership);
		Assert.Null(result.AnchorLine);
	}

	[Fact]
	public void Evaluate_ReturnsExtractedLines_ForDiagnosticDisplay()
	{
		SubscriptionLineEvaluation result = _evaluator.Evaluate(
			"11.4.7", product: null, SubscriptionLineGranularity.Subminor, candidateVersion: "11.4.7.900", candidateCatalogReleaseDate: null);

		Assert.Equal("11.4.7", result.AnchorLine);
		Assert.Equal("11.4.7", result.CandidateLine);
	}
}
