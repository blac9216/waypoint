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
/// Configuration for the download-runner's managed-tool state mount (ADR-0015
/// decision 3, issue #441): the account-gated <c>vcf-download-tool</c> is never
/// baked into the runner image -- it is operator-installed onto this persistent
/// volume through the appliance (a future UI install flow, not this slice) and
/// travels in operator-created air-gap bundles. This slice establishes the
/// volume/interface a download job's tool-presence gate checks before it runs;
/// it introduces no install flow of its own.
/// </summary>
public sealed class ManagedToolOptions
{
	public const string SectionName = "ManagedTool";

	/// <summary>
	/// Root directory of the managed-tool state volume. Matches
	/// <c>deploy/docker-compose.yml</c>'s eventual <c>managed-tool</c> volume mount
	/// (wired by #442); tests point this at a temp directory.
	/// </summary>
	public string ToolStatePath { get; set; } = "/var/lib/waypoint/managed-tool";

	/// <summary>
	/// File name of the installed <c>vcf-download-tool</c> executable expected directly
	/// under <see cref="ToolStatePath"/>. A future install flow (ADR-0015) is
	/// responsible for placing it there under this exact name; this option only names
	/// where the tool-presence gate looks.
	/// </summary>
	public string ExecutableName { get; set; } = "vcf-download-tool";
}
