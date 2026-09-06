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

using System.Globalization;
using System.Text.RegularExpressions;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IVmToolsVersionsFileParser"/>
public sealed partial class VmToolsVersionsFileParser : IVmToolsVersionsFileParser
{
	[GeneratedRegex(@"^\s*(\d+)(?:\.(\d+))?(?:\.(\d+))?")]
	private static partial Regex LeadingVersionSegments();

	public VmToolsVersionsFileParseResult Parse(string versionsFileContent)
	{
		ArgumentNullException.ThrowIfNull(versionsFileContent);

		List<VmToolsEsxVersionMapping> mappings = [];
		List<string> warnings = [];
		int sequence = 0;

		foreach (string line in versionsFileContent.Split('\n'))
		{
			string trimmed = line.Trim('\r', ' ', '\t');
			if (trimmed.Length == 0 || trimmed.StartsWith('#'))
			{
				continue;
			}

			// Whitespace-separated, but the last column (Tools build) is legitimately
			// blank on very old rows (#1030 finding 1) -- split on runs of whitespace so a
			// blank trailing column collapses rather than shifting the others.
			string[] columns = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (columns.Length is < 4 or > 5)
			{
				warnings.Add($"vmtools versions: skipped malformed row (expected 4-5 columns, found {columns.Length}): \"{trimmed}\"");
				continue;
			}

			string toolsVersionCode = columns[0];
			string esxiVersionDir = columns[1];
			// A row with only 4 whitespace-separated tokens is missing the ESXi-build
			// column entirely (#1030 finding 1: blank when column 2 is esx/0.0, this Tools
			// build not being bundled with any ESXi) -- the remaining two tokens are still
			// the Tools version and Tools build, just shifted left by the missing column.
			string? esxiBuild;
			string? toolsVersionRaw;
			string? toolsBuild;
			if (columns.Length == 5)
			{
				esxiBuild = columns[2];
				toolsVersionRaw = columns[3];
				toolsBuild = columns[4];
			}
			else
			{
				esxiBuild = null;
				toolsVersionRaw = columns[2];
				toolsBuild = columns[3];
			}

			(int? major, int? minor, int? patch) = ParseVersionSegments(toolsVersionRaw);

			mappings.Add(new VmToolsEsxVersionMapping(
				Id: Guid.NewGuid(),
				SequenceInFile: sequence,
				EsxiVersionDir: esxiVersionDir,
				EsxiBuild: esxiBuild,
				ToolsVersionCode: toolsVersionCode,
				ToolsVersionRaw: toolsVersionRaw,
				ToolsVersionMajor: major,
				ToolsVersionMinor: minor,
				ToolsVersionPatch: patch,
				ToolsBuild: toolsBuild,
				RawRow: trimmed));
			sequence++;
		}

		return new VmToolsVersionsFileParseResult(mappings, warnings);
	}

	/// <summary>
	/// A small local parser for a leading dotted-numeric version, e.g. "13.1.0" or
	/// "13.0". Returns all-null when the string does not start with a numeric segment
	/// (an unparseable version, issue AC2's release-recency-fallback case) -- the
	/// caller orders by <see cref="VmToolsEsxVersionMapping.SequenceInFile"/> (already
	/// newest-first by ESXi build) rather than crashing on a numeric comparison.
	///
	/// TODO(#1039): migrate to Waypoint.Core's shared version comparator once merged --
	/// this repeats the "string sort is wrong here" lesson #1030 documented (13.0.10 >
	/// 13.0.5) with the smallest parser that satisfies this issue's needs, rather than
	/// depending on #1039's still-unmerged branch.
	/// </summary>
	private static (int? Major, int? Minor, int? Patch) ParseVersionSegments(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return (null, null, null);
		}

		Match match = LeadingVersionSegments().Match(raw);
		if (!match.Success)
		{
			return (null, null, null);
		}

		int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
		int? minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
		int? patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null;
		return (major, minor, patch);
	}
}
