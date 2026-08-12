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

namespace Waypoint.Core.Secrets;

/// <summary>
/// Tuning for the <see cref="IRunSecretStore"/> lifecycle (issue #434), bound from the
/// <c>RunSecrets</c> configuration section.
/// </summary>
public sealed class RunSecretOptions
{
	public const string SectionName = "RunSecrets";

	/// <summary>
	/// The <c>run_secrets.expires_at</c> window, set at <c>StoreAsync</c> time (from
	/// <c>now()</c>) and then a SLIDING window thereafter (issue #469): every
	/// successful <c>RunSecretStore.DecryptAsync</c> pushes <c>expires_at</c> back out
	/// to <c>now() + Expiry</c> in the same transaction as its audit write. This is a
	/// backstop for an ABANDONED run, not the expected lifetime -- the ordinary path
	/// deletes the row synchronously the moment the run reaches a terminal state (see
	/// <c>JobQueueRepository</c>'s run-completion paths), almost always well inside this
	/// window. 8 hours: because the window now slides on every decrypt (retry, stage
	/// requeue, and lease-recovery all decrypt again), a long multi-stage run keeps
	/// itself alive for as long as it keeps making progress, so the default no longer
	/// has to be generous enough to cover an entire run's worst-case duration up front --
	/// it only has to outlast the gap between two consecutive decrypts. Short enough that
	/// a genuinely abandoned run's secret (one that stops decrypting entirely) does not
	/// linger for a full day, as the prior fixed-at-creation 24h default did.
	/// </summary>
	public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(8);

	/// <summary>How often <see cref="RunSecretCleanupHostedService"/> sweeps for expired rows.</summary>
	public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
