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
using Xunit;

namespace Waypoint.Tests.Core.Versions;

/// <summary>
/// The fallback ladder (epic #16 decision R2-5): parsed -> structural compare;
/// unparseable-but-dated -> releaseDate order; undated+unparseable -> quarantined, a
/// first-class outcome that automation must never select as a candidate.
/// </summary>
public sealed class ProductVersionClassifierTests
{
	[Fact]
	public void Classify_ParseableVersion_IsParsedOutcome()
	{
		ClassifiedProductVersion classified = ProductVersionClassifier.Classify(version: "9.1.0.0100", product: "VCENTER", catalogReleaseDate: null);

		Assert.Equal(VersionClassificationOutcome.Parsed, classified.Outcome);
		Assert.NotNull(classified.Parsed);
		Assert.True(classified.Parsed!.IsParsed);
	}

	[Fact]
	public void Classify_UnparseableWithReleaseDate_IsDateOrderedOutcome()
	{
		DateTimeOffset releaseDate = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

		ClassifiedProductVersion classified = ProductVersionClassifier.Classify("RTM-Special", "VCF", releaseDate);

		Assert.Equal(VersionClassificationOutcome.DateOrdered, classified.Outcome);
		Assert.Null(classified.Parsed);
		Assert.Equal(releaseDate, classified.CatalogReleaseDate);
	}

	[Fact]
	public void Classify_UnparseableAndUndated_IsQuarantined()
	{
		ClassifiedProductVersion classified = ProductVersionClassifier.Classify("RTM-Special", "VCF", catalogReleaseDate: null);

		Assert.Equal(VersionClassificationOutcome.Quarantined, classified.Outcome);
		Assert.Null(classified.Parsed);
		Assert.Null(classified.CatalogReleaseDate);
	}

	[Fact]
	public void Classify_NullVersionString_IsQuarantined_NeverThrows()
	{
		ClassifiedProductVersion classified = ProductVersionClassifier.Classify(null, "VCF", catalogReleaseDate: null);

		Assert.Equal(VersionClassificationOutcome.Quarantined, classified.Outcome);
	}

	[Fact]
	public void SelectNewestFirst_ExcludesQuarantinedVersions_FromAutomationCandidates()
	{
		ClassifiedProductVersion parsedNewer = ProductVersionClassifier.Classify(version: "9.1.0.0200", product: "VCENTER", catalogReleaseDate: null);
		ClassifiedProductVersion parsedOlder = ProductVersionClassifier.Classify(version: "9.1.0.0100", product: "VCENTER", catalogReleaseDate: null);
		ClassifiedProductVersion dateOrdered = ProductVersionClassifier.Classify(
			"RTM-Special", "VCENTER", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
		ClassifiedProductVersion quarantined = ProductVersionClassifier.Classify("Unknown-Build", "VCENTER", null);

		IReadOnlyList<ClassifiedProductVersion> candidates = ProductVersionAutomationCandidates.SelectNewestFirst(
			[parsedNewer, parsedOlder, dateOrdered, quarantined]);

		Assert.DoesNotContain(candidates, c => c.Outcome == VersionClassificationOutcome.Quarantined);
		Assert.Equal(3, candidates.Count);
		// Parsed entries always rank ahead of date-ordered ones (documented assumption
		// -- structural parse is stronger evidence than a timestamp).
		Assert.Equal(parsedNewer, candidates[0]);
		Assert.Equal(parsedOlder, candidates[1]);
		Assert.Equal(dateOrdered, candidates[2]);
	}

	[Fact]
	public void SelectNewestFirst_AllQuarantined_ReturnsEmpty_NeverThrows()
	{
		ClassifiedProductVersion a = ProductVersionClassifier.Classify("Unknown-A", "VCF", null);
		ClassifiedProductVersion b = ProductVersionClassifier.Classify("Unknown-B", "VCF", null);

		IReadOnlyList<ClassifiedProductVersion> candidates = ProductVersionAutomationCandidates.SelectNewestFirst([a, b]);

		Assert.Empty(candidates);
	}

	[Fact]
	public void SelectNewestFirst_DateOrderedEntries_OrderedByReleaseDateDescending()
	{
		ClassifiedProductVersion older = ProductVersionClassifier.Classify(
			"Special-A", "VKR", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
		ClassifiedProductVersion newer = ProductVersionClassifier.Classify(
			"Special-B", "VKR", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

		IReadOnlyList<ClassifiedProductVersion> candidates = ProductVersionAutomationCandidates.SelectNewestFirst([older, newer]);

		Assert.Equal(newer, candidates[0]);
		Assert.Equal(older, candidates[1]);
	}
}
