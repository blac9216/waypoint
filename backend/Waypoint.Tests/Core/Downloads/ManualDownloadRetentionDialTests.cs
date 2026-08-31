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

using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Pure-logic tests for <see cref="ManualDownloadRetentionDialResolver"/> -- no
/// Postgres involved. Issue #1440 Acceptance Criteria: "Manual-download dial
/// setting is persisted and read correctly by three states: auto-prune, keep,
/// review-list."
/// </summary>
public sealed class ManualDownloadRetentionDialTests
{
	[Theory]
	[InlineData(ManualDownloadDialOptions.AutoPrune, ManualDownloadDial.AutoPrune)]
	[InlineData(ManualDownloadDialOptions.Keep, ManualDownloadDial.Keep)]
	[InlineData(ManualDownloadDialOptions.Review, ManualDownloadDial.Review)]
	public void Parse_AllThreeWireValues_RoundTripsToTypedDial(string wireValue, ManualDownloadDial expected)
	{
		ManualDownloadDial parsed = ManualDownloadRetentionDialResolver.Parse(wireValue);

		Assert.Equal(expected, parsed);
		Assert.Equal(wireValue, ManualDownloadRetentionDialResolver.ToWireValue(parsed));
	}

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	[InlineData("auto_prune")]
	[InlineData("AUTO-PRUNE")]
	[InlineData("delete")]
	public void Parse_UnrecognizedOrMiscasedValue_ThrowsArgumentException(string wireValue)
	{
		Assert.Throws<ArgumentException>(() => ManualDownloadRetentionDialResolver.Parse(wireValue));
	}

	[Fact]
	public void Parse_Null_ThrowsArgumentNullException()
	{
		// ArgumentException.ThrowIfNullOrWhiteSpace throws the ArgumentNullException
		// subtype specifically for a null argument (ArgumentException itself for
		// empty/whitespace, covered by Parse_UnrecognizedOrMiscasedValue_ThrowsArgumentException
		// above) -- xUnit's Assert.Throws<T> requires the exact type, not merely an
		// assignable one, so this needs its own assertion.
		Assert.Throws<ArgumentNullException>(() => ManualDownloadRetentionDialResolver.Parse(null!));
	}

	[Theory]
	[InlineData(ManualDownloadDialOptions.AutoPrune)]
	[InlineData(ManualDownloadDialOptions.Keep)]
	[InlineData(ManualDownloadDialOptions.Review)]
	public void Resolve_ReadsScopePolicysDialDefault(string dialDefault)
	{
		RetentionPolicy policy = MakePolicy(dialDefault);

		ManualDownloadDial resolved = ManualDownloadRetentionDialResolver.Resolve(policy);

		Assert.Equal(ManualDownloadRetentionDialResolver.Parse(dialDefault), resolved);
	}

	[Fact]
	public void Resolve_NullPolicy_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>(() => ManualDownloadRetentionDialResolver.Resolve(null!));
	}

	[Fact]
	public void Resolve_PolicyWithCorruptDialValue_ThrowsRatherThanSilentlyDefaulting()
	{
		// Simulates a value that somehow bypassed download_retention_policies_dial_check
		// (e.g. written by a future migration or a direct SQL edit) -- Resolve must
		// throw, not silently treat it as any of the three real dial states, since a
		// silent default could misresolve which manual downloads are protected from
		// auto-prune.
		RetentionPolicy corrupt = MakePolicy("purge-immediately");

		Assert.Throws<ArgumentException>(() => ManualDownloadRetentionDialResolver.Resolve(corrupt));
	}

	[Theory]
	[InlineData(ManualDownloadDial.AutoPrune, false)]
	[InlineData(ManualDownloadDial.Keep, true)]
	[InlineData(ManualDownloadDial.Review, true)]
	public void SkipsAutoPrune_OnlyAutoPruneDialIsFalse(ManualDownloadDial dial, bool expected)
	{
		Assert.Equal(expected, ManualDownloadRetentionDialResolver.SkipsAutoPrune(dial));
	}

	[Theory]
	[InlineData(ManualDownloadDial.AutoPrune, false)]
	[InlineData(ManualDownloadDial.Keep, false)]
	[InlineData(ManualDownloadDial.Review, true)]
	public void RequiresReview_OnlyReviewDialIsTrue(ManualDownloadDial dial, bool expected)
	{
		Assert.Equal(expected, ManualDownloadRetentionDialResolver.RequiresReview(dial));
	}

	private static RetentionPolicy MakePolicy(string manualDownloadDialDefault) => new(
		Guid.NewGuid(),
		RetentionPolicyScopes.Default,
		GracePeriodDays: 30,
		GraceMaxRefreshes: 0,
		manualDownloadDialDefault,
		CreatedAt: DateTimeOffset.UtcNow,
		UpdatedAt: DateTimeOffset.UtcNow);
}
