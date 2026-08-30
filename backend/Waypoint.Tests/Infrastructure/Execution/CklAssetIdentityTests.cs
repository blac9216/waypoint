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

using Waypoint.Infrastructure.Scans;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #1068 / PR #1224 review finding 2: unit pins for the guard that stands between
/// an operator-authored target fact and the vendored <c>New-CklConvertArgs</c>, which
/// interpolates each fact into a double-quoted <c>saf convert hdf2ckl</c> argument
/// segment with no escaping -- a string that <c>ProcessStartInfo.Arguments</c> then
/// re-parses with Windows-style quoting rules.
///
/// The case list lives in <see cref="CklAssetIdentityCaseTable"/> rather than here,
/// because the PowerShell mirror <c>Get-WaypointSafeCklAssetValue</c> is driven from the
/// same table (see
/// <c>WaypointConvertAssetIdentityArgumentTests.PowerShellMirror_AgreesWithCSharpGuard_OnEveryTableCase</c>);
/// that is what pins the two guards to each other. All values are invented (AGENTS.md
/// sanitization).
/// </summary>
public sealed class CklAssetIdentityTests
{
	/// <summary>
	/// The whole shared table, against the C# guard. An accepted value must come back
	/// UNCHANGED -- the guard is a reject, not a sanitizer: a mangled asset name in an
	/// eMASS-visible CKL is worse than an absent one, because it still looks
	/// authoritative. A rejected value must yield <see cref="string.Empty"/>.
	/// </summary>
	[Theory]
	[MemberData(nameof(CklAssetIdentityCaseTable.AsTheoryData), MemberType = typeof(CklAssetIdentityCaseTable))]
	public void TryAccept_MatchesTheSharedCaseTable(string value, bool expectedAccepted)
	{
		bool actual = CklAssetIdentity.TryAccept(value, out string accepted);

		Assert.Equal(expectedAccepted, actual);
		Assert.Equal(expectedAccepted ? value : string.Empty, accepted);
		Assert.Equal(expectedAccepted, CklAssetIdentity.IsAcceptable(value));
	}

	/// <summary>
	/// Review round 2's blocker, named explicitly so it cannot be lost in the table: a
	/// value ENDING in a backslash carries no quote, no control character and no
	/// leading dash -- it passed round 1's deny list -- yet it is exactly what escapes
	/// the vendored builder's closing quote once .NET parses the argument string.
	/// <c>WaypointConvertAssetIdentityArgumentTests</c> proves the consequence at the
	/// argv level; this pins the rejection at the source.
	/// </summary>
	[Fact]
	public void TryAccept_ReviewRound2TrailingBackslashPayload_IsRejected()
	{
		Assert.False(CklAssetIdentity.TryAccept(CklAssetIdentityCaseTable.TrailingBackslashHostname, out string hostname));
		Assert.Equal(string.Empty, hostname);
		Assert.False(CklAssetIdentity.TryAccept(CklAssetIdentityCaseTable.SecondOutputFlagHost, out string host));
		Assert.Equal(string.Empty, host);
	}

	/// <summary>
	/// Review round 1's payload, kept named for the same reason: a literal <c>"</c>
	/// closes the builder's quoted segment and appends a second <c>-o</c>.
	/// </summary>
	[Fact]
	public void TryAccept_ReviewRound1QuotePayload_IsRejected()
	{
		Assert.False(CklAssetIdentity.TryAccept(CklAssetIdentityCaseTable.QuoteInjectionPayload, out string accepted));
		Assert.Equal(string.Empty, accepted);
	}

	/// <summary>An absent fact is "not acceptable" too: there is nothing to stamp.</summary>
	[Fact]
	public void TryAccept_NullFact_IsRejected()
	{
		Assert.False(CklAssetIdentity.TryAccept(null, out string accepted));
		Assert.Equal(string.Empty, accepted);
	}
}
