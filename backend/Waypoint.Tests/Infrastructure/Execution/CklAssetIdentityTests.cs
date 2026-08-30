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
/// Issue #1068 / PR #1224 review round 1 finding 2: unit pins for the guard that stands
/// between an operator-authored target fact and the vendored <c>New-CklConvertArgs</c>,
/// which interpolates each fact into a double-quoted <c>saf convert hdf2ckl</c>
/// argument segment with no escaping. All fixture values are invented (AGENTS.md
/// sanitization).
/// </summary>
public sealed class CklAssetIdentityTests
{
	/// <summary>
	/// The reviewer's exact payload shape. Verified by the reviewer against the real
	/// vendored builder to yield
	/// <c>... -o "/w/out.ckl" --hostname "evil" -o "/w/pwned.ckl"</c> -- a second
	/// <c>-o</c> that redirects saf's CKL write inside the runner container.
	/// </summary>
	[Fact]
	public void TryAccept_ReviewersInjectionPayload_IsRejectedAndYieldsNoValue()
	{
		const string payload = "evil\" -o \"/w/pwned.ckl";

		Assert.False(CklAssetIdentity.TryAccept(payload, out string accepted));
		Assert.Equal(string.Empty, accepted);
		Assert.False(CklAssetIdentity.IsAcceptable(payload));
	}

	/// <summary>
	/// A bare double quote needs no crafted flag to be harmful -- it breaks the
	/// command line outright.
	/// </summary>
	[Theory]
	[InlineData("bad\"name")]
	[InlineData("\"")]
	[InlineData("trailing-quote\"")]
	public void TryAccept_AnyDoubleQuote_IsRejected(string value)
	{
		Assert.False(CklAssetIdentity.TryAccept(value, out string accepted));
		Assert.Equal(string.Empty, accepted);
	}

	/// <summary>An embedded control character can split or truncate the argument string.</summary>
	[Theory]
	[InlineData("host\nname")]
	[InlineData("host\rname")]
	[InlineData("host\tname")]
	[InlineData("host\u0000name")]
	[InlineData("host\u007Fname")]
	public void TryAccept_AnyControlCharacter_IsRejected(string value)
	{
		Assert.False(CklAssetIdentity.TryAccept(value, out string accepted));
		Assert.Equal(string.Empty, accepted);
	}

	/// <summary>
	/// A value beginning with <c>-</c> would be parsed by saf as a flag of its own
	/// even without escaping the quotes -- leading whitespace does not launder it.
	/// </summary>
	[Theory]
	[InlineData("-o")]
	[InlineData("--output")]
	[InlineData("  -o")]
	public void TryAccept_LeadingDash_IsRejected(string value)
	{
		Assert.False(CklAssetIdentity.TryAccept(value, out string accepted));
		Assert.Equal(string.Empty, accepted);
	}

	/// <summary>An absent fact is "not acceptable" too: there is nothing to stamp.</summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void TryAccept_AbsentFact_IsRejected(string? value)
	{
		Assert.False(CklAssetIdentity.TryAccept(value, out string accepted));
		Assert.Equal(string.Empty, accepted);
	}

	/// <summary>
	/// The values Waypoint actually threads through -- an operator-assigned target
	/// name, an FQDN, an IPv4/IPv6 literal, a MAC -- pass through UNCHANGED. The guard
	/// is a reject, not a sanitizer: it must never quietly rewrite a legitimate asset
	/// name, including one carrying a dash, a dot, a colon, or an underscore in a
	/// non-leading position.
	/// </summary>
	[Theory]
	[InlineData("invented-target-a")]
	[InlineData("invented-target-a.example.internal")]
	[InlineData("198.51.100.10")]
	[InlineData("2001:db8::1")]
	[InlineData("00:00:5E:00:53:01")]
	[InlineData("esxi_host-07")]
	[InlineData("host with spaces")]
	[InlineData("198.51.100.10:443")]
	public void TryAccept_LegitimateAssetFact_IsAcceptedVerbatim(string value)
	{
		Assert.True(CklAssetIdentity.TryAccept(value, out string accepted));
		Assert.Equal(value, accepted);
		Assert.True(CklAssetIdentity.IsAcceptable(value));
	}
}
