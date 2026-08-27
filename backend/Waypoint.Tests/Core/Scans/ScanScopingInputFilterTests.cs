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
/// Issue #911 (closed by epic #726 Wave 3's #739/#740 slice): pure domain logic, no
/// database -- proves the reserved-key filter itself drops exactly the platform
/// selector-scoping keys, never touches an unrelated key, and degrades safely on a
/// document shape it does not understand.
/// </summary>
public sealed class ScanScopingInputFilterTests
{
	[Fact]
	public void Filter_KeyCollidesWithVmhostName_DropsKeyAndReportsIt()
	{
		ScanScopingFilterResult result = ScanScopingInputFilter.Filter("vmhostName: 'attacker-widened.example.internal'\n");

		Assert.Equal(["vmhostName"], result.DroppedKeys);
		Assert.DoesNotContain("attacker-widened", result.FilteredYaml, StringComparison.Ordinal);
	}

	[Fact]
	public void Filter_KeyCollidesWithVmName_DropsKeyAndReportsIt()
	{
		ScanScopingFilterResult result = ScanScopingInputFilter.Filter("vmName: 'attacker-widened-vm'\n");

		Assert.Equal(["vmName"], result.DroppedKeys);
		Assert.DoesNotContain("attacker-widened-vm", result.FilteredYaml, StringComparison.Ordinal);
	}

	[Fact]
	public void Filter_KeyCollidesWithVsphereSelectorKind_DropsKeyAndReportsIt()
	{
		ScanScopingFilterResult result = ScanScopingInputFilter.Filter("vsphereSelectorKind: 'vcenter'\n");

		Assert.Equal(["vsphereSelectorKind"], result.DroppedKeys);
	}

	[Fact]
	public void Filter_MixedReservedAndUnrelatedKeys_DropsOnlyReserved_KeepsUnrelated()
	{
		const string yaml = "vmhostName: 'attacker.example.internal'\ninvented_unrelated_input: 'kept-value'\n";

		ScanScopingFilterResult result = ScanScopingInputFilter.Filter(yaml);

		Assert.Equal(["vmhostName"], result.DroppedKeys);
		Assert.Contains("invented_unrelated_input", result.FilteredYaml, StringComparison.Ordinal);
		Assert.Contains("kept-value", result.FilteredYaml, StringComparison.Ordinal);
		Assert.DoesNotContain("attacker.example.internal", result.FilteredYaml, StringComparison.Ordinal);
	}

	[Fact]
	public void Filter_NoReservedKeys_ReturnsOriginalTextUnchanged_AndNoDrops()
	{
		const string yaml = "invented_target_ip: '198.51.100.42'\n";

		ScanScopingFilterResult result = ScanScopingInputFilter.Filter(yaml);

		Assert.Empty(result.DroppedKeys);
		Assert.Equal(yaml, result.FilteredYaml);
	}

	[Fact]
	public void Filter_EmptyMapping_ReturnsNoDrops()
	{
		ScanScopingFilterResult result = ScanScopingInputFilter.Filter("{}\n");

		Assert.Empty(result.DroppedKeys);
	}

	[Fact]
	public void Filter_NotATopLevelMapping_ReturnsUnfilteredWithNoDrops()
	{
		// A scalar/list document (not the expected InSpec-inputs mapping shape) is
		// returned untouched rather than rejected -- this filter only ever narrows a
		// mapping's keys, never rewrites or rejects a shape it does not understand.
		const string yaml = "- vmhostName\n- attacker.example.internal\n";

		ScanScopingFilterResult result = ScanScopingInputFilter.Filter(yaml);

		Assert.Empty(result.DroppedKeys);
		Assert.Equal(yaml, result.FilteredYaml);
	}

	[Fact]
	public void Filter_MalformedYaml_ReturnsUnfilteredWithNoDrops()
	{
		// Defensive fallback only -- a config doc is already validated as well-formed
		// YAML at save time (YamlDocumentValidator), so this path should not occur for a
		// real plan item, but the filter must never throw on it.
		const string malformed = "vmhostName: [unterminated\n";

		ScanScopingFilterResult result = ScanScopingInputFilter.Filter(malformed);

		Assert.Empty(result.DroppedKeys);
		Assert.Equal(malformed, result.FilteredYaml);
	}

	[Fact]
	public void ReservedScopingKeys_MatchesWaypointScanPsm1sGeneratedScopingFileKeys()
	{
		// Pins the closed set this filter guards -- must stay in lockstep with
		// WaypointScan.psm1's own generated $InputsContent keys
		// (vsphereSelectorKind/vmhostName/vmName) or the filter silently stops
		// protecting a real platform scoping key.
		Assert.Equal(
			new HashSet<string> { "vsphereSelectorKind", "vmhostName", "vmName" },
			new HashSet<string>(ScanScopingInputFilter.ReservedScopingKeys));
	}
}
