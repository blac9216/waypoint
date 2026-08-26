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

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Builds invented, minimal <see cref="VendorContentEntry"/> fixtures for
/// <see cref="VendorHierarchyInterpreterTests"/>/<see cref="SemanticImportReconcilerTests"/>.
/// No vendor content, real path, or real command output is ever embedded here -- every
/// profile key and manifest below is fabricated for this test suite only.
/// </summary>
internal static class VendorContentEntryBuilder
{
	/// <summary>An executable-leaf-shaped entry: has a manifest, a controls/ dir, and at least one control file.</summary>
	public static VendorContentEntry Leaf(string profileKey, string yaml, params string[] controlFiles) =>
		new(profileKey, yaml, HasControlsDirectory: controlFiles.Length > 0, HasFilesDirectory: false, controlFiles);

	/// <summary>An aggregate-shaped entry: has a manifest but no controls/ dir (groups leaves instead of executing).</summary>
	public static VendorContentEntry Aggregate(string profileKey, string yaml) =>
		new(profileKey, yaml, HasControlsDirectory: false, HasFilesDirectory: false, []);

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
