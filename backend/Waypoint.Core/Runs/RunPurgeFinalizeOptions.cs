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
/// Issue #1013: configuration for the API-side purge-finalization sweep
/// (<c>RunPurgeFinalizeHostedService</c>) that closes the gap between the
/// compliance-runner's artifact-purge job reporting <c>artifacts_phase = 'done'</c>
/// and the purge's tombstone/<c>purged_at</c> finalization -- which migration 0042's
/// least-privilege posture keeps API-only. Always enabled (no <c>Enabled</c> flag,
/// unlike <see cref="RunHistoryRolloffOptions"/>): without this sweep an
/// artifact-bearing purge NEVER finalizes on its own, so disabling it would
/// reintroduce exactly the stuck lifecycle the issue fixes; the sweep is also a
/// cheap indexed-primary-key read of a table that is empty except while a purge is
/// mid-flight.
/// </summary>
public sealed class RunPurgeFinalizeOptions
{
	public const string SectionName = "RunPurgeFinalize";

	/// <summary>
	/// How often the API process checks for purges whose phases are both done but
	/// which still lack their tombstone. Bounded low (seconds, not hours) because an
	/// operator is typically watching <c>GET /runs/{id}/purge</c> right after their
	/// POST -- the artifact job itself usually takes longer than one sweep interval,
	/// so this is the dominant added latency between "job done" and "Completed".
	/// </summary>
	public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);
}
