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
	/// The <c>run_secrets.expires_at</c> window set at <c>StoreAsync</c> time (from
	/// <c>now()</c>). This is a backstop for an ABANDONED run, not the expected
	/// lifetime -- the ordinary path deletes the row synchronously the moment the run
	/// reaches a terminal state (see <c>JobQueueRepository</c>'s run-completion paths),
	/// almost always well inside this window. 24 hours: generous next to any single
	/// scan run's realistic duration (minutes to low hours across a large target
	/// fan-out), short enough that a genuinely abandoned run's secret does not linger
	/// for days.
	/// </summary>
	public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(24);

	/// <summary>How often <see cref="RunSecretCleanupHostedService"/> sweeps for expired rows.</summary>
	public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
