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

using Waypoint.Core.ComplianceContent.SemanticImport;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Builds one invented, miniature <see cref="VendorContentEntry"/> vendor-content
/// checkout per <see cref="CatalogParityRow"/>/<see cref="CatalogParityComponent"/> pair,
/// shaped exactly to <c>VendorHierarchyInterpreter</c>'s documented path grammar
/// (<c>&lt;family&gt;/&lt;version&gt;/&lt;release&gt;/inspec/&lt;baseline&gt;/[leaf]</c>).
/// Every path segment, manifest field, and control filename below is fabricated for this
/// test suite -- never exported from the sibling repository or any real lab (CLAUDE.md).
///
/// This builder is deliberately reusable by later #749 slices (planner/job-count parity,
/// command construction, etc. per the issue's remainder) -- it produces the same
/// vendor-content shape those slices will also need to feed through the pipeline, so it
/// lives in this shared namespace rather than duplicated per test class.
/// </summary>
public static class ParityFixtureBuilder
{
	private const string BaselineDirectory = "baseline";

	/// <summary>
	/// Builds one <see cref="VendorContentEntry"/> per documented leaf component for one
	/// matrix row. Deliberately does NOT also synthesize the split-family aggregate
	/// profile at the bare baseline directory: this suite's contract is leaf-component
	/// derivation (docs/compliance-parity.md's per-component columns), and
	/// <c>VendorHierarchyInterpreter.BuildNamedSplit</c>'s aggregate candidate for
	/// named-service-split families (VCSA/NSX) carries <c>selector_kind = service</c>
	/// with a null <c>selector_name</c> -- vocabulary-invalid per
	/// <c>CatalogVocabularyValidator</c>, so <c>SemanticImportReconciler</c> correctly
	/// quarantines it today. That is a real, pre-existing gap in the shipped importer
	/// (every VCSA/NSX pull's aggregate node lands in the rejected list), reported as a
	/// deferred finding in this PR rather than silently worked around by this fixture
	/// builder or fixed here (out of this slice's scope -- see the PR body).
	/// </summary>
	public static IReadOnlyList<VendorContentEntry> BuildEntries(CatalogParityRow row)
	{
		List<VendorContentEntry> entries = [];

		bool isWholeAppliance = row.SelectorKind == "target" && row.Components.Count == 1 && row.Components[0].SelectorName is null;

		foreach (CatalogParityComponent component in row.Components)
		{
			string leafSegment = component.SelectorName ?? component.ComponentKey;
			string profileKey = isWholeAppliance
				? $"{row.FixtureDirectoryLiteral}/{row.ProductVersionKey}/{row.ReleaseKey}/inspec/{BaselineDirectory}"
				: $"{row.FixtureDirectoryLiteral}/{row.ProductVersionKey}/{row.ReleaseKey}/inspec/{BaselineDirectory}/{leafSegment}";

			string[] controls = [$"{component.ComponentKey}-control-1.rb", $"{component.ComponentKey}-control-2.rb"];
			string manifestVersion = row.ReleaseKey;
			string[] inputs = [$"{component.ComponentKey}_target_input"];

			entries.Add(new VendorContentEntry(
				profileKey,
				ParityManifests.Manifest(component.ComponentKey, title: component.DisplayName, version: manifestVersion, inputs: inputs),
				HasControlsDirectory: true,
				HasFilesDirectory: false,
				ControlFileNames: controls));
		}

		return entries;
	}
}

/// <summary>Tiny invented <c>inspec.yml</c> text builder shared by every parity fixture.</summary>
internal static class ParityManifests
{
	public static string Manifest(string name, string? title = null, string? version = null, string[]? inputs = null)
	{
		List<string> lines = ["name: " + name];
		if (title is not null)
		{
			lines.Add("title: " + title);
		}

		if (version is not null)
		{
			lines.Add("version: " + version);
		}

		if (inputs is { Length: > 0 })
		{
			lines.Add("inputs:");
			foreach (string input in inputs)
			{
				lines.Add("  - name: " + input);
				lines.Add("    type: String");
			}
		}

		return string.Join('\n', lines) + "\n";
	}
}
