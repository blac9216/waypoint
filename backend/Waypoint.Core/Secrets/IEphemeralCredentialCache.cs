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
/// A caller-supplied "my credentials" pair (ADR-0011 personal tier): username plus
/// secret, prompted at run initiation and never persisted. Distinct from
/// <see cref="DecryptedSecret"/> -- that type wraps a value that came out of
/// <c>credential_secrets</c>; this one never went in.
/// </summary>
public sealed record EphemeralCredential(string Username, string Secret);

/// <summary>
/// Process-memory-only holding pen for ephemeral personal credentials between
/// <c>POST /runs</c> (where the caller supplies them) and the job that consumes them
/// (issue #276, fourth slice of the #23 split; #274 wires the scan execution path that
/// eventually calls <see cref="TryTake"/>). Nothing here is written to
/// <c>credentials</c>, <c>jobs.payload</c>, <c>job_events</c>, or any log line --
/// <see cref="Put"/> registers the secret with <see cref="Waypoint.Core.Logging.ISecretTracker"/>
/// before returning so the in-play redaction window opens at intake, not first use.
///
/// Entries are single-shot and time-bounded: <see cref="TryTake"/> removes and
/// untracks on first successful read, and an entry no handler ever claims expires and
/// is swept regardless. A job resumed after its cache entry is gone (process restart,
/// TTL, or a prior claim) has no ephemeral secret available -- the consuming handler
/// must treat that as an auth-style failure, never fall back to a stored credential
/// (see docs/security.md and the ADR-0011 "no personal rows, ever" rule).
/// </summary>
public interface IEphemeralCredentialCache
{
	/// <summary>
	/// Registers <paramref name="credential"/> for <paramref name="jobId"/>, tracking it
	/// with the in-play redactor immediately. <paramref name="actor"/> is recorded on a
	/// best-effort audit row (<c>secret.ephemeral_registered</c>, <c>credential_id</c>
	/// NULL) -- there is no stored credential row to attribute to.
	/// </summary>
	void Put(Guid jobId, Guid? runId, EphemeralCredential credential, string actor);

	/// <summary>
	/// Removes and returns the credential registered for <paramref name="jobId"/>,
	/// ending its in-play redaction window. Returns <c>null</c> if no entry exists
	/// (never registered, already taken, or expired) -- callers must treat that the
	/// same as "credential unavailable", not silently proceed unauthenticated.
	/// </summary>
	EphemeralCredential? TryTake(Guid jobId);
}
