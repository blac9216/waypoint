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

using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Issue #1138 (round-1 review Spec #1/#2): the narrowed-selector name rule is a
/// conservative ALLOW-list, not a deny-list, because the value is interpolated UNQUOTED
/// into a PowerCLI <c>-Name</c> argument by vendor Ruby. These tables are the rule's
/// contract: every rejected CLASS is named (a deny-list would silently grow a hole the
/// moment a class is forgotten -- which is exactly what round 1 found), and the accepted
/// forms prove the allow-list is not so tight that ordinary vSphere object names stop
/// planning.
/// </summary>
public sealed class ScanComponentNarrowingSelectorNameTests
{
	/// <summary>
	/// Rejected: one row per hazardous character class, with the phrase the skip detail
	/// must carry so an operator can tell a scope-widening wildcard apart from a merely
	/// awkward name.
	/// </summary>
	[Theory]
	// PowerCLI wildcards -- `-Name` is a wildcard-matching parameter, so these resolve
	// to MORE than the narrowed object (the silent scope widening ADR-0023 forbids).
	[InlineData("web*", "wildcard")]
	[InlineData("web?", "wildcard")]
	[InlineData("[a]", "wildcard")]
	[InlineData("web[0-9]", "wildcard")]
	// PowerShell metacharacters -- in an unquoted argument these terminate the
	// statement, execute a subexpression, pipe, background, or group.
	[InlineData("vm;whoami", "metacharacter")]
	[InlineData("vm$(hostname)", "metacharacter")]
	[InlineData("vm`ndone", "metacharacter")]
	[InlineData("vm|out-null", "metacharacter")]
	[InlineData("vm&whoami", "metacharacter")]
	[InlineData("vm(1)", "metacharacter")]
	[InlineData("vm{1}", "metacharacter")]
	[InlineData("vm>out.txt", "metacharacter")]
	[InlineData("vm<in.txt", "metacharacter")]
	[InlineData("o'clock-vm", "metacharacter")]
	[InlineData("vm\"quoted\"", "metacharacter")]
	[InlineData("vm#comment", "metacharacter")]
	[InlineData("vm,other", "metacharacter")]
	[InlineData("vm=1", "metacharacter")]
	[InlineData("vm^caret", "metacharacter")]
	[InlineData("vm!bang", "metacharacter")]
	[InlineData("vm%percent", "metacharacter")]
	[InlineData("vm~tilde", "metacharacter")]
	// Whitespace -- splits the unquoted value into more than one argument.
	[InlineData("esxi host one", "whitespace")]
	[InlineData("vm\tname", "whitespace")]
	[InlineData("vm\nname", "whitespace")]
	[InlineData("  ", "whitespace")]
	// Control and non-ASCII -- unprovable round-trip through the vendor input file and
	// the remote PowerShell host's code page.
	[InlineData("vm\u0000name", "control")]
	[InlineData("vm\u0007name", "control")]
	[InlineData("vm-café", "non-ASCII")]
	[InlineData("vm-中文", "non-ASCII")]
	// Anything else outside [A-Za-z0-9._-] -- the allow-list refuses by default rather
	// than enumerating every remaining punctuation mark.
	[InlineData("dc01/vm-a", "outside the safe set")]
	[InlineData("dc01\\vm-a", "outside the safe set")]
	[InlineData("vm:1", "outside the safe set")]
	[InlineData("vm@host", "outside the safe set")]
	[InlineData("vm+one", "outside the safe set")]
	// Nothing to narrow to at all.
	[InlineData("", "empty")]
	public void DescribeUnsafeSelectorName_RejectsHazardousClass_NamingIt(string name, string expectedPhrase)
	{
		string? description = ScanComponentNarrowing.DescribeUnsafeSelectorName(name);

		Assert.NotNull(description);
		Assert.Contains(expectedPhrase, description, StringComparison.Ordinal);
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(name));
	}

	/// <summary>
	/// Accepted: the ordinary vSphere object-name forms that MUST keep planning --
	/// FQDNs, short host names, and the hyphen/underscore/digit shapes real inventories
	/// use. A rule that rejected these would trade #1138's silent widening for a silent
	/// coverage loss.
	/// </summary>
	[Theory]
	[InlineData("esxi-01.example.internal")]
	[InlineData("esxi01")]
	[InlineData("vm_app_01")]
	[InlineData("VM-App-01")]
	[InlineData("192.0.2.10")]
	[InlineData("a")]
	[InlineData("web-server-2026.dc01.example.internal")]
	public void DescribeUnsafeSelectorName_AcceptsOrdinaryObjectName(string name)
	{
		Assert.Null(ScanComponentNarrowing.DescribeUnsafeSelectorName(name));
		Assert.True(ScanComponentNarrowing.IsSafeSelectorName(name));
	}

	/// <summary>A null name is unsafe, never an unhandled exception at plan-compile time.</summary>
	[Fact]
	public void DescribeUnsafeSelectorName_Null_IsUnsafe()
	{
		Assert.NotNull(ScanComponentNarrowing.DescribeUnsafeSelectorName(null));
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(null));
	}

	/// <summary>
	/// The FIRST offending character decides the reported class, so a name carrying more
	/// than one hazard reports deterministically rather than depending on enumeration
	/// order.
	/// </summary>
	[Fact]
	public void DescribeUnsafeSelectorName_MultipleHazards_ReportsTheFirst()
	{
		Assert.Contains("wildcard", ScanComponentNarrowing.DescribeUnsafeSelectorName("web* ; $x")!, StringComparison.Ordinal);
		Assert.Contains("whitespace", ScanComponentNarrowing.DescribeUnsafeSelectorName("web vm*")!, StringComparison.Ordinal);
	}
}
