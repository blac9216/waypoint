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

using System.Management.Automation;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Infrastructure.PowerShell;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// Issue #1016: the <c>Get-WaypointComplianceContentEntries</c> output parsing that used
/// to live directly in <c>ContentPullJobHandler</c>'s phase-2 loop (issue #729), now
/// shared with <see cref="ContentCheckJobHandler"/> -- the chunked check phase moved to
/// its own fanned-out job type, but the PowerShell command and its output shape are
/// unchanged, so this is a pure extraction, not a behavior change.
/// </summary>
public static class ContentCheckEntryParser
{
	/// <summary>
	/// Parses one <c>ContentEntries</c> row into a <see cref="VendorContentEntry"/> for
	/// the semantic importer. A missing/blank ProfileKey drops the row rather than
	/// failing the whole chunk -- same "one malformed row must not fail the whole pull"
	/// discipline the pre-#1016 handler applied.
	/// </summary>
	public static VendorContentEntry? TryParseContentEntry(object? item)
	{
		if (item is not PSObject psObject)
		{
			return null;
		}

		string? profileKey = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["ProfileKey"]?.Value);
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			return null;
		}

		string? rawYaml = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["RawYaml"]?.Value);
		bool hasControlsDirectory = PowerShellValueUnwrap.Unwrap(psObject.Properties["HasControlsDirectory"]?.Value) is true;
		bool hasFilesDirectory = PowerShellValueUnwrap.Unwrap(psObject.Properties["HasFilesDirectory"]?.Value) is true;
		bool inspecCheckRan = PowerShellValueUnwrap.Unwrap(psObject.Properties["InspecCheckRan"]?.Value) is true;
		bool inspecCheckPassed = PowerShellValueUnwrap.Unwrap(psObject.Properties["InspecCheckPassed"]?.Value) is true;
		string? inspecCheckDetail = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["InspecCheckDetail"]?.Value);

		List<string> controlFileNames = [];
		foreach (object? rawName in PowerShellValueUnwrap.UnwrapEach(psObject.Properties["ControlFileNames"]?.Value))
		{
			if (rawName is string name && !string.IsNullOrWhiteSpace(name))
			{
				controlFileNames.Add(name);
			}
		}

		return new VendorContentEntry(
			profileKey, rawYaml, hasControlsDirectory, hasFilesDirectory, controlFileNames,
			inspecCheckRan, inspecCheckPassed, inspecCheckDetail);
	}

	/// <summary>
	/// Parses a profile row's Controls array. A missing Controls property (e.g. an
	/// older module build, or a profile with no controls/ directory at all) yields an
	/// empty list, not a failure.
	/// </summary>
	public static List<ProfileControlUpsert> TryParseControls(object? item)
	{
		List<ProfileControlUpsert> controls = [];
		if (item is not PSObject psObject)
		{
			return controls;
		}

		foreach (object? rawControl in PowerShellValueUnwrap.UnwrapEach(psObject.Properties["Controls"]?.Value))
		{
			if (rawControl is not PSObject controlObject)
			{
				continue;
			}

			string? controlId = PowerShellValueUnwrap.UnwrapAs<string>(controlObject.Properties["ControlId"]?.Value);
			if (string.IsNullOrWhiteSpace(controlId))
			{
				continue;
			}

			string? title = PowerShellValueUnwrap.UnwrapAs<string>(controlObject.Properties["Title"]?.Value);
			string? severity = PowerShellValueUnwrap.UnwrapAs<string>(controlObject.Properties["Severity"]?.Value);
			controls.Add(new ProfileControlUpsert(
				controlId,
				string.IsNullOrWhiteSpace(title) ? null : title,
				string.IsNullOrWhiteSpace(severity) ? null : severity));
		}

		return controls;
	}
}
