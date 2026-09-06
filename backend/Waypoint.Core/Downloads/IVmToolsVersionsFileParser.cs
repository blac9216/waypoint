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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Result of parsing the upstream VMTools <c>versions</c> file (research #1030 finding
/// 1): well-formed rows in <see cref="Mappings"/>, and every skipped malformed line in
/// <see cref="Warnings"/> -- captured, never silently dropped (the #1446 lesson this
/// repo already applies to the ESX patch-store parser).
/// </summary>
public sealed record VmToolsVersionsFileParseResult(
	IReadOnlyList<VmToolsEsxVersionMapping> Mappings,
	IReadOnlyList<string> Warnings);

/// <summary>
/// Parses the upstream VMTools <c>versions</c> file: 5 whitespace-separated columns,
/// a <c>#</c>-prefixed comment header, rows ordered newest-first by ESXi build (research
/// #1030 finding 1). Column 2 (<c>esx/&lt;version&gt;</c>) is the literal join key to the
/// <c>esx/</c> tree -- <c>esx/0.0</c> means "not (yet) bundled with any ESXi" and is
/// captured like any other row, never treated as an error. A row with the wrong column
/// count is skipped and recorded as a <see cref="VmToolsVersionsFileParseResult.Warnings"/>
/// entry rather than aborting the whole file or crashing the caller.
/// </summary>
public interface IVmToolsVersionsFileParser
{
	VmToolsVersionsFileParseResult Parse(string versionsFileContent);
}
