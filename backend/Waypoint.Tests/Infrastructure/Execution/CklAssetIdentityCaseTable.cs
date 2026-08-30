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

using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #1068 / PR #1224 review round 2 finding 2: the ONE table of CKL asset-identity
/// values, and the single place that says which of them are acceptable. Both halves of
/// the guard are driven from it -- <c>CklAssetIdentity.TryAccept</c> by
/// <see cref="CklAssetIdentityTests"/>, and the PowerShell mirror
/// <c>Get-WaypointSafeCklAssetValue</c> by
/// <c>WaypointConvertAssetIdentityArgumentTests.PowerShellMirror_AgreesWithCSharpGuard_OnEveryTableCase</c>,
/// which invokes the real <c>WaypointScan.psm1</c> through the in-process executor. The
/// two implementations were previously only asserted to be "the same rule" by comments
/// (and were not: the mirror missed the C1 control range); nothing but a shared table
/// keeps them honest. Adding a case here binds both guards at once.
///
/// All values are invented (AGENTS.md sanitization): RFC 2606 <c>example.internal</c>
/// names, RFC 5737 / RFC 3849 documentation addresses.
/// </summary>
public static class CklAssetIdentityCaseTable
{
	/// <summary>A single value plus the verdict BOTH guards must reach for it.</summary>
	/// <param name="Label">Human-readable case name (diagnostics only).</param>
	/// <param name="Value">The candidate asset fact.</param>
	/// <param name="Accepted">True when the guard must pass the value through unchanged.</param>
	public sealed record GuardCase(string Label, string Value, bool Accepted);

	/// <summary>
	/// The reviewer's round-2 payload: a target name ENDING in a backslash. It contains
	/// no quote, no control character and no leading dash, so the round-1 deny list
	/// passed it -- yet <c>ProcessStartInfo.Arguments</c> treats the vendored builder's
	/// closing <c>"</c> as escaped, the token keeps absorbing, and the next field's
	/// contents fall out as separate argv tokens.
	/// </summary>
	public const string TrailingBackslashHostname = @"target-a\";

	/// <summary>
	/// The host paired with <see cref="TrailingBackslashHostname"/> in the reviewer's
	/// proof: once the quoting realigns, argv gains a second <c>-o</c> plus an
	/// attacker-chosen path.
	/// </summary>
	public const string SecondOutputFlagHost = "x -o /w/pwned.ckl";

	/// <summary>Round 1's payload: closes the quoted segment outright.</summary>
	public const string QuoteInjectionPayload = "evil\" -o \"/w/pwned.ckl";

	private static readonly GuardCase[] AllCases =
	[
		// Accepted: the whole character surface of the four facts Waypoint stamps.
		new("plain target name", "invented-target-a", true),
		new("fqdn", "invented-target-a.example.internal", true),
		new("ipv4 literal", "198.51.100.10", true),
		new("ipv6 literal", "2001:db8::1", true),
		new("mac", "00:00:5E:00:53:01", true),
		new("underscore and digits", "esxi_host-07", true),
		new("internal spaces", "invented host 07", true),
		new("host and port", "198.51.100.10:443", true),
		new("uppercase", "INVENTED-TARGET-A", true),

		// Rejected: the two payloads the review actually built and ran.
		new("round-1 quote injection", QuoteInjectionPayload, false),
		new("round-2 trailing backslash", TrailingBackslashHostname, false),
		new("round-2 paired host", SecondOutputFlagHost, false),

		// Rejected: everything else outside the allow-list. A deny list would have to
		// enumerate these correctly against a parser this repo does not own; an
		// allow-list gets them for free, which is the point of the class.
		new("bare double quote", "\"", false),
		new("embedded double quote", "bad\"name", false),
		new("single quote", "o'brien-host", false),
		new("backslash mid-value", @"domain\host", false),
		new("backtick", "host`name", false),
		new("dollar", "host$name", false),
		new("semicolon", "host;name", false),
		new("pipe", "host|name", false),
		new("ampersand", "host&name", false),
		new("less than", "host<name", false),
		new("greater than", "host>name", false),
		new("parentheses", "host(name)", false),
		new("asterisk", "host*name", false),
		new("question mark", "host?name", false),
		new("brackets", "host[0]", false),
		new("braces", "host{0}", false),
		new("hash", "host#name", false),
		new("percent", "host%name", false),
		new("tilde", "host~name", false),
		new("caret", "host^name", false),
		new("bang", "host!name", false),
		new("equals", "host=name", false),
		new("comma", "host,name", false),
		new("slash", "host/name", false),
		new("at sign", "host@name", false),
		new("plus", "host+name", false),

		// Rejected: control characters. C0, DEL, and the C1 range the round-1 mirror
		// missed (U+0085 NEL is also char.IsWhiteSpace in .NET).
		new("newline", "host\nname", false),
		new("carriage return", "host\rname", false),
		new("tab", "host\tname", false),
		new("nul", "host\u0000name", false),
		new("del", "host\u007Fname", false),
		new("c1 nel", "host\u0085name", false),
		new("c1 upper", "host\u0090name", false),

		// Rejected: non-ASCII. A non-ASCII asset name is a real possibility, but it is
		// a REJECT (fact omitted, WARN emitted), never a silently transliterated stamp.
		new("non-ascii latin", "hôte-invente", false),
		new("non-ascii cjk", "主机", false),

		// Rejected: a value saf would read as a flag of its own, with or without
		// leading whitespace to launder it.
		new("leading dash", "-o", false),
		new("leading double dash", "--output", false),
		new("leading dash after spaces", "  -o", false),

		// Rejected: nothing to stamp.
		new("empty", "", false),
		new("whitespace only", "   ", false),
	];

	/// <summary>Every case, for callers that iterate rather than use <c>[MemberData]</c>.</summary>
	public static IReadOnlyList<GuardCase> Cases => AllCases;

	/// <summary>The same table as xunit theory data: (value, expected verdict).</summary>
	public static TheoryData<string, bool> AsTheoryData()
	{
		TheoryData<string, bool> data = [];
		foreach (GuardCase testCase in AllCases)
		{
			data.Add(testCase.Value, testCase.Accepted);
		}

		return data;
	}
}
