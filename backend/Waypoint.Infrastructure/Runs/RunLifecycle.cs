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

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #784: the terminal <c>runs.state</c> set was previously private to
/// <see cref="RunPurgeService"/> alone; promoted here (unchanged values/semantics)
/// so <see cref="RunRetentionHoldService"/> can require the same "completed" floor
/// (docs/api-contract.md's run state machine) for placing a hold without a second,
/// driftable copy of this list.
/// </summary>
internal static class RunLifecycle
{
	/// <summary>The three terminal <c>runs.state</c> values a purge (or a retention hold) is allowed against.</summary>
	public static readonly HashSet<string> TerminalRunStates = new(StringComparer.Ordinal)
	{
		"completed", "completed_with_failures", "aborted",
	};

	/// <summary>
	/// Compliance-owned run types -- the only ones that can carry the evidence graph
	/// a retention hold protects. Matches <c>RunHistoryDeletionService</c>'s existing
	/// <c>requires_domain_purge_first</c> classification (docs/api-contract.md's
	/// "/runs/{id}/history" note).
	/// </summary>
	public static readonly HashSet<string> ComplianceRunTypes = new(StringComparer.Ordinal)
	{
		"scan", "remediate",
	};
}
