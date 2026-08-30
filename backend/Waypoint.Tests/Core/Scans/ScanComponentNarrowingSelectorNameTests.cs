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

using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Issue #1138 (round-1 review Spec #1/#2, round-2 Spec #1): the narrowed-selector name
/// rule is decided PER SELECTOR KIND, because the vendored
/// <c>dod-compliance-and-automation</c> content quotes the two kinds differently
/// (measured over vSphere 7.0 + 8.0): the ESX baselines interpolate the name UNQUOTED
/// (<c>Get-VMHost -Name #{vmhostName}</c> -- 740 files, 6 quoted), while the VM
/// baselines interpolate it into a SINGLE-QUOTED literal
/// (<c>Get-VM -Name '#{vmName}'</c> -- 277 files, 0 unquoted). These tables are the
/// rule's contract: for <c>esxi</c>, a conservative ALLOW-list where every rejected
/// CLASS is named (a deny-list would silently grow a hole the moment a class is
/// forgotten -- exactly what round 1 found); for <c>vm</c>, only the classes that are
/// genuinely hazardous inside a single-quoted literal, so a VM named
/// <c>Windows Server 2022 - test</c> keeps planning rather than being silently dropped
/// from coverage.
/// </summary>
public sealed class ScanComponentNarrowingSelectorNameTests
{
	/// <summary>
	/// Rejected for BOTH kinds: the classes whose hazard is independent of quoting.
	/// PowerCLI wildcards are a property of the <c>-Name</c> parameter itself, so they
	/// resolve to MORE than the narrowed object either way (the silent scope widening
	/// ADR-0023 forbids); control and non-ASCII characters are refused because their
	/// round-trip through the vendor input file and the remote PowerShell host's code
	/// page is unprovable, not because quoting fails.
	/// </summary>
	[Theory]
	[InlineData("web*", "wildcard")]
	[InlineData("web?", "wildcard")]
	[InlineData("[a]", "wildcard")]
	[InlineData("web[0-9]", "wildcard")]
	[InlineData("vm\u0000name", "control")]
	[InlineData("vm\u0007name", "control")]
	[InlineData("vm-café", "non-ASCII")]
	[InlineData("vm-中文", "non-ASCII")]
	// A TAB is whitespace under the strict esxi rule and outside the printable-ASCII
	// set the single-quoted vm rule accepts, so it loses coverage either way.
	[InlineData("vm\tname", "whitespace")]
	[InlineData("", "empty")]
	public void DescribeUnsafeSelectorName_RejectsForEveryKind_NamingTheClass(string name, string expectedPhrase)
	{
		foreach (string kind in new[] { CatalogSelectorKinds.Esxi, CatalogSelectorKinds.Vm })
		{
			string? description = ScanComponentNarrowing.DescribeUnsafeSelectorName(kind, name);

			Assert.NotNull(description);
			Assert.Contains(expectedPhrase, description, StringComparison.Ordinal);
			Assert.False(ScanComponentNarrowing.IsSafeSelectorName(kind, name));
		}
	}

	/// <summary>
	/// Rejected for <c>esxi</c> ONLY: the classes whose hazard exists solely because the
	/// ESX baselines interpolate the value unquoted. Each row is proved BOTH ways --
	/// rejected under the strict <c>esxi</c> rule and accepted under the <c>vm</c> rule
	/// -- so a future edit cannot quietly re-generalise the strict rule back onto
	/// <c>vm</c> (round-2 finding #1) without a red test.
	/// </summary>
	[Theory]
	// PowerShell metacharacters -- in an unquoted argument these terminate the
	// statement, execute a subexpression, pipe, background, or group. Inside a
	// single-quoted literal every one of them is an ordinary character.
	[InlineData("vm;whoami", "metacharacter")]
	[InlineData("vm$(hostname)", "metacharacter")]
	[InlineData("vm`ndone", "metacharacter")]
	[InlineData("vm|out-null", "metacharacter")]
	[InlineData("vm&whoami", "metacharacter")]
	[InlineData("vm(1)", "metacharacter")]
	[InlineData("vm{1}", "metacharacter")]
	[InlineData("vm>out.txt", "metacharacter")]
	[InlineData("vm<in.txt", "metacharacter")]
	[InlineData("vm\"quoted\"", "metacharacter")]
	[InlineData("vm#comment", "metacharacter")]
	[InlineData("vm,other", "metacharacter")]
	[InlineData("vm=1", "metacharacter")]
	[InlineData("vm^caret", "metacharacter")]
	[InlineData("vm!bang", "metacharacter")]
	[InlineData("vm%percent", "metacharacter")]
	[InlineData("vm~tilde", "metacharacter")]
	// Whitespace -- splits the UNQUOTED value into more than one argument. This is the
	// coverage regression round 2 caught: `Windows Server 2022 - test` is the ordinary
	// shape of a Windows VM name and must keep planning.
	[InlineData("esxi host one", "whitespace")]
	[InlineData("Windows Server 2022 - test", "whitespace")]
	[InlineData("  ", "whitespace")]
	// Anything else outside [A-Za-z0-9._-] -- the strict allow-list refuses by default
	// rather than enumerating every remaining punctuation mark.
	[InlineData("dc01/vm-a", "outside the safe set")]
	[InlineData("dc01\\vm-a", "outside the safe set")]
	[InlineData("vm:1", "outside the safe set")]
	[InlineData("vm@host", "outside the safe set")]
	[InlineData("vm+one", "outside the safe set")]
	public void DescribeUnsafeSelectorName_RejectsForEsxiOnly_AcceptedForVm(string name, string expectedPhrase)
	{
		string? esxi = ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Esxi, name);
		Assert.NotNull(esxi);
		Assert.Contains(expectedPhrase, esxi, StringComparison.Ordinal);
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.Esxi, name));

		Assert.Null(ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Vm, name));
		Assert.True(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.Vm, name));
	}

	/// <summary>
	/// A newline is rejected for BOTH kinds: it is whitespace (splitting the unquoted
	/// esxi argument) and, for <c>vm</c>, it is also outside the printable-ASCII set the
	/// single-quoted rule accepts, so it can never reach a skip detail as a literal
	/// line break either.
	/// </summary>
	[Fact]
	public void DescribeUnsafeSelectorName_Newline_RejectedForBothKinds()
	{
		Assert.Contains("whitespace", ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Esxi, "vm\nname")!, StringComparison.Ordinal);
		Assert.Contains("whitespace", ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Vm, "vm\nname")!, StringComparison.Ordinal);
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.Vm, "vm\nname"));
	}

	/// <summary>
	/// The single quote is the one metacharacter that stays hazardous for <c>vm</c>: it
	/// breaks OUT of the vendor's <c>'#{vmName}'</c> literal. Rejected for both kinds,
	/// and for <c>vm</c> the detail says so specifically rather than blaming a
	/// non-existent unquoted interpolation.
	/// </summary>
	[Fact]
	public void DescribeUnsafeSelectorName_SingleQuote_RejectedForBothKinds()
	{
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.Esxi, "o'clock-vm"));
		Assert.Contains("single quote", ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Vm, "o'clock-vm")!, StringComparison.Ordinal);
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.Vm, "o'clock-vm"));
	}

	/// <summary>
	/// Accepted for BOTH kinds: the ordinary vSphere object-name forms that MUST keep
	/// planning -- FQDNs, short host names, and the hyphen/underscore/digit shapes real
	/// inventories use. A rule that rejected these would trade #1138's silent widening
	/// for a silent coverage loss.
	/// </summary>
	[Theory]
	[InlineData("esx01.lab.example.internal")]
	[InlineData("esxi-01.example.internal")]
	[InlineData("esxi01")]
	[InlineData("vm_app_01")]
	[InlineData("VM-App-01")]
	[InlineData("192.0.2.10")]
	[InlineData("a")]
	[InlineData("web-server-2026.dc01.example.internal")]
	public void DescribeUnsafeSelectorName_AcceptsOrdinaryObjectName_ForEveryKind(string name)
	{
		foreach (string kind in new[] { CatalogSelectorKinds.Esxi, CatalogSelectorKinds.Vm })
		{
			Assert.Null(ScanComponentNarrowing.DescribeUnsafeSelectorName(kind, name));
			Assert.True(ScanComponentNarrowing.IsSafeSelectorName(kind, name));
		}
	}

	/// <summary>
	/// An unrecognised selector kind falls back to the STRICT rule -- a new kind must
	/// opt in to the relaxed one deliberately, never inherit it by default.
	/// </summary>
	[Fact]
	public void DescribeUnsafeSelectorName_UnknownKind_UsesTheStrictRule()
	{
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.VCenter, "Windows Server 2022 - test"));
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(null, "Windows Server 2022 - test"));
		Assert.True(ScanComponentNarrowing.IsSafeSelectorName(null, "esx01.lab.example.internal"));
	}

	/// <summary>A null name is unsafe, never an unhandled exception at plan-compile time.</summary>
	[Fact]
	public void DescribeUnsafeSelectorName_Null_IsUnsafe()
	{
		Assert.NotNull(ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Vm, null));
		Assert.False(ScanComponentNarrowing.IsSafeSelectorName(CatalogSelectorKinds.Vm, null));
	}

	/// <summary>
	/// The FIRST offending character decides the reported class, so a name carrying more
	/// than one hazard reports deterministically rather than depending on enumeration
	/// order.
	/// </summary>
	[Fact]
	public void DescribeUnsafeSelectorName_MultipleHazards_ReportsTheFirst()
	{
		Assert.Contains("wildcard", ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Esxi, "web* ; $x")!, StringComparison.Ordinal);
		Assert.Contains("whitespace", ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Esxi, "web vm*")!, StringComparison.Ordinal);
		// Under the vm rule the space is fine, so the wildcard is the first hazard.
		Assert.Contains("wildcard", ScanComponentNarrowing.DescribeUnsafeSelectorName(CatalogSelectorKinds.Vm, "web vm*")!, StringComparison.Ordinal);
	}
}
