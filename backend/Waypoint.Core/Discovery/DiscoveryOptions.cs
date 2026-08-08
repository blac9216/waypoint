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

namespace Waypoint.Core.Discovery;

/// <summary>
/// Configuration for the <c>discover</c> job handler and the target inventory cache
/// (issue #21, epic #13). <see cref="StaleAfterMinutes"/> answers domain-model.md open
/// question 3 ("Inventory staleness policy: hard max age before a scan forces
/// re-discovery?") with a default of 60 minutes -- long enough that a Start-a-Scan
/// screen visited minutes apart does not force a redundant discover job (a vSphere
/// AllLinked connect + full enumeration is not free), short enough that a scan does
/// not silently run against an inventory tree that is hours stale (a host added or
/// decommissioned since the last discover would be invisible to the checkbox tree).
/// Operator-tunable because "right" depends on how often the target site's inventory
/// actually churns.
///
/// This slice implements the threshold itself plus manual/on-demand refresh
/// (<c>POST /targets/{id}/discover</c>, callable any time regardless of staleness).
/// The auto-trigger-on-scan-initiation half of the acceptance criteria is deferred to
/// land with #23 (the scan slice) -- see the discovery PR's Split note and the
/// follow-up issue: the scan-initiation code path this would hook into does not exist
/// yet.
/// </summary>
public sealed class DiscoveryOptions
{
	public const string SectionName = "Discovery";

	/// <summary>
	/// How long a target's cached inventory is considered fresh before it counts as
	/// stale. Default 60 -- see class doc comment for the reasoning behind this default.
	/// </summary>
	public int StaleAfterMinutes { get; set; } = 60;
}
