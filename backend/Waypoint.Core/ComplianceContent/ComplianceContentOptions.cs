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

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// Configuration for the <c>content-pull</c> handler's working-tree mount (ADR-0017: a
/// compliance-runner-only persistent volume, read-only to scan/discover/credential-test
/// and writable only by content-pull/content-import execution).
/// </summary>
public sealed class ComplianceContentOptions
{
	public const string SectionName = "ComplianceContent";

	/// <summary>
	/// Root directory of the compliance-content working tree. Matches
	/// <c>deploy/compose.yaml</c>'s eventual <c>compliance-content</c> volume
	/// mount; tests point this at a temp directory.
	/// </summary>
	public string ContentPath { get; set; } = "/var/lib/waypoint/compliance-content";
}
