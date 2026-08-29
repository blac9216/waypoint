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

namespace Waypoint.Core.Runs;

/// <summary>
/// Configures <c>EvidenceRetentionSweepHostedService</c> (issue #1062, epic #726
/// sections 6/7) -- the periodic sweep that finds compliance runs past the
/// Admin-configured <see cref="IRetentionPolicyRepository"/> retention period and
/// drives each one through the existing <c>RunPurgeService.PurgeRunAsync</c> path
/// (ADR-0019), the SAME entry point <c>POST /runs/{id}/purge</c> uses. Deliberately
/// does NOT carry the retention period itself -- that is the Admin-configurable,
/// runtime-mutable <c>retention_policy</c> row, re-read fresh every pass so a
/// mid-flight policy change takes effect on the sweep's next tick without a restart.
///
/// <see cref="Enabled"/> defaults to <c>false</c>, same conservative posture
/// <see cref="RunHistoryRolloffOptions.Enabled"/> already documents for its own
/// sibling sweep -- an operator opts in once satisfied with the configured retention
/// period, rather than the appliance silently purging compliance evidence on a
/// default schedule the day this ships.
/// </summary>
public sealed class EvidenceRetentionSweepOptions
{
	public const string SectionName = "EvidenceRetentionSweep";

	/// <summary>Master switch. Off by default -- see this class's doc comment.</summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// Optional cap on how many eligible runs to purge-request in a single sweep
	/// pass -- bounds one pass's job-fan-out volume on a large backlog, same
	/// reasoning as <see cref="RunHistoryRolloffOptions.MaxRunsPerSweep"/>. Default 100
	/// (smaller than roll-off's 500: each candidate here drives a full
	/// <c>RunPurgeService.PurgeRunAsync</c> call, including an artifact-deletion job
	/// fan-out, not a single lightweight history-deletion call).
	/// </summary>
	public int MaxRunsPerSweep { get; set; } = 100;

	/// <summary>How often <c>EvidenceRetentionSweepHostedService</c> sweeps.</summary>
	public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);
}
