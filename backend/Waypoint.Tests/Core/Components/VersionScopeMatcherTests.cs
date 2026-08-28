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

using Waypoint.Core.Components;
using Xunit;

namespace Waypoint.Tests.Core.Components;

/// <summary>
/// Exhaustive proof of issue #998's CORRECTED owner decision (2026-08-28): a closed
/// two-form scope test, fail-closed on any unknown key form or unparseable observed
/// version -- never nearest-version inference, never a numeric range comparison.
/// </summary>
public sealed class VersionScopeMatcherTests
{
	// -- N.M (minor-scoped) exact/prefix hits -------------------------------------

	[Theory]
	[InlineData("8.0", "8.0")]
	[InlineData("8.0.3", "8.0")]
	[InlineData("8.0.0", "8.0")]
	[InlineData("8.0.100.12345", "8.0")]
	public void Matches_NMKey_HitsExactAndPatchExtensions(string observed, string key)
	{
		Assert.True(VersionScopeMatcher.Matches(observed, key));
	}

	[Theory]
	[InlineData("8.1", "8.0")]
	[InlineData("7.0.3", "8.0")]
	[InlineData("9.0", "8.0")]
	[InlineData("8", "8.0")]
	public void Matches_NMKey_MissesDifferentMinor(string observed, string key)
	{
		Assert.False(VersionScopeMatcher.Matches(observed, key));
	}

	/// <summary>
	/// The prefix trap: "8.10" must never match catalog key "8.1" merely because the
	/// STRING "8.10" starts with the substring "8.1" -- segments compare as parsed
	/// integers, not raw string prefixes.
	/// </summary>
	[Fact]
	public void Matches_NMKey_PrefixTrap_8Point10DoesNotMatch8Point1()
	{
		Assert.False(VersionScopeMatcher.Matches("8.10", "8.1"));
		Assert.False(VersionScopeMatcher.Matches("8.10.0", "8.1"));
	}

	[Fact]
	public void Matches_NMKey_PrefixTrap_8Point1DoesMatch8Point1()
	{
		Assert.True(VersionScopeMatcher.Matches("8.1", "8.1"));
		Assert.True(VersionScopeMatcher.Matches("8.1.9", "8.1"));
	}

	// -- N.x (major-line-scoped) hits/misses --------------------------------------

	[Theory]
	[InlineData("9.0", "9.x")]
	[InlineData("9.0.0", "9.x")]
	[InlineData("9", "9.x")]
	[InlineData("9.5.2.10000", "9.x")]
	public void Matches_NxKey_HitsAnyMinorUnderThatMajor(string observed, string key)
	{
		Assert.True(VersionScopeMatcher.Matches(observed, key));
	}

	[Theory]
	[InlineData("8.9", "9.x")]
	[InlineData("90", "9.x")]
	[InlineData("19.0", "9.x")]
	public void Matches_NxKey_MissesDifferentMajor(string observed, string key)
	{
		Assert.False(VersionScopeMatcher.Matches(observed, key));
	}

	/// <summary>Prefix trap for the major-line form: "19.0" must not match "9.x".</summary>
	[Fact]
	public void Matches_NxKey_PrefixTrap_19DoesNotMatch9x()
	{
		Assert.False(VersionScopeMatcher.Matches("19.0.1", "9.x"));
	}

	/// <summary>
	/// A literal "x" in a non-final segment (Workspace ONE Access's documented
	/// `3-3-x` -&gt; `3.3.x` catalog key) is still the major-line form scoped by every
	/// concrete leading segment -- "3.3.x" matches "3.3.*", not "3.*".
	/// </summary>
	[Theory]
	[InlineData("3.3.0", "3.3.x")]
	[InlineData("3.3", "3.3.x")]
	[InlineData("3.3.5.9999", "3.3.x")]
	public void Matches_MultiSegmentXKey_HitsMatchingLeadingSegments(string observed, string key)
	{
		Assert.True(VersionScopeMatcher.Matches(observed, key));
	}

	[Theory]
	[InlineData("3.4.0", "3.3.x")]
	[InlineData("4.3.0", "3.3.x")]
	[InlineData("3.30", "3.3.x")]
	public void Matches_MultiSegmentXKey_MissesDifferentLeadingSegments(string observed, string key)
	{
		Assert.False(VersionScopeMatcher.Matches(observed, key));
	}

	// -- Unknown key forms fail closed ---------------------------------------------

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("x")]
	[InlineData("x.x")]
	[InlineData("9")]
	[InlineData("9.x.1")]
	[InlineData("9..x")]
	[InlineData("9.x.")]
	[InlineData(".9.x")]
	[InlineData("9.0.")]
	[InlineData("v8.0")]
	[InlineData("8.a")]
	[InlineData("8.08")]
	[InlineData("latest")]
	public void Matches_UnknownKeyForm_FailsClosedForAnyObservedVersion(string key)
	{
		Assert.False(VersionScopeMatcher.Matches("8.0.3", key));
		Assert.False(VersionScopeMatcher.Matches("9.0.0", key));
	}

	// -- Unparseable/blank observed version fails closed, for any key form ---------

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("unknown")]
	[InlineData("v8.0.3")]
	public void Matches_UnparseableObservedVersion_FailsClosed(string? observed)
	{
		Assert.False(VersionScopeMatcher.Matches(observed, "8.0"));
		Assert.False(VersionScopeMatcher.Matches(observed, "9.x"));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Matches_BlankOrNullCatalogKey_FailsClosed(string? key)
	{
		Assert.False(VersionScopeMatcher.Matches("8.0.3", key));
	}

	// -- Case sensitivity ------------------------------------------------------------

	[Theory]
	[InlineData("9.X")]
	[InlineData("9.x")]
	public void Matches_UppercaseXWildcard_IsRecognizedCaseInsensitively(string key)
	{
		Assert.True(VersionScopeMatcher.Matches("9.5.0", key));
		Assert.False(VersionScopeMatcher.Matches("8.5.0", key));
	}

	// -- Whitespace tolerance on both inputs -----------------------------------------

	[Fact]
	public void Matches_SurroundingWhitespace_IsTrimmedOnBothInputs()
	{
		Assert.True(VersionScopeMatcher.Matches("  8.0.3  ", "  8.0  "));
		Assert.True(VersionScopeMatcher.Matches(" 9.1.0", "9.x "));
	}
}
