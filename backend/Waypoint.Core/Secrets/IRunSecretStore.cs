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
/// A caller-supplied "my credentials" pair (ADR-0011 personal tier), the same shape
/// <see cref="EphemeralCredential"/> carried -- kept as a separate type because this one
/// crosses the <see cref="IRunSecretStore"/> boundary (persisted, envelope-encrypted)
/// rather than staying in process memory.
/// </summary>
public sealed record RunSecretCredential(string Username, string Secret);

/// <summary>
/// A decrypted run secret, in play: <see cref="Dispose"/> ends the redaction window
/// <see cref="IRunSecretStore.DecryptAsync"/> opened before returning this. Mirrors
/// <see cref="DecryptedSecret"/>'s shape but also carries <see cref="Username"/>, which
/// travels unencrypted alongside the ciphertext (same layout as
/// <c>credentials.username</c> beside <c>credential_secrets</c>).
/// </summary>
public sealed class DecryptedRunSecret : IDisposable
{
	private readonly IDisposable _redactionHandle;

	public DecryptedRunSecret(string username, string secret, IDisposable redactionHandle)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentNullException.ThrowIfNull(secret);
		ArgumentNullException.ThrowIfNull(redactionHandle);
		Username = username;
		Secret = secret;
		_redactionHandle = redactionHandle;
	}

	public string Username { get; }

	public string Secret { get; }

	public void Dispose() => _redactionHandle.Dispose();
}

/// <summary>
/// Issue #434 (epic #433): the encrypted, run-scoped replacement for
/// <see cref="IEphemeralCredentialCache"/>'s process-memory-only handoff. Same ADR-0011
/// contract -- "no personal rows, ever" in the reusable <c>credentials</c> /
/// <c>credential_secrets</c> tables -- but persisted in a dedicated <c>run_secrets</c>
/// table (one row per run, envelope-encrypted with the same AES-256-GCM primitives as
/// <see cref="ICredentialSecretStore"/>) so:
///
/// - a dedicated compliance runner (ADR-0013/0014), which shares no process memory with
///   the API, can decrypt it locally at the point of use;
/// - an API restart between <c>POST /runs</c> and a runner's claim of the job no longer
///   forces the caller to re-enter the credential (the AC this issue exists for).
///
/// Lifecycle differs from <see cref="ICredentialSecretStore"/> in two ways that matter:
/// there is no rotation (one write, at run creation, ever) and the row is
/// terminal/expiry bounded rather than living until an explicit delete -- see
/// <see cref="DeleteAsync"/> (backend deletes on terminal run completion) and
/// <see cref="DeleteExpiredAsync"/> (the cleanup sweep for abandoned/crashed runs that
/// never reach a terminal state).
/// </summary>
public interface IRunSecretStore
{
	/// <summary>
	/// Stores <paramref name="credential"/> for <paramref name="runId"/>, envelope-encrypted
	/// under an AAD context bound to the run. Audited as <c>secret.run_registered</c>
	/// (fail-closed: if the audit row cannot commit, the secret is not stored -- same
	/// discipline as <see cref="ICredentialSecretStore.StoreAsync"/>). One row per run;
	/// a second call for the same <paramref name="runId"/> throws rather than silently
	/// overwriting, since nothing in this flow ever legitimately re-registers a run's
	/// secret.
	/// </summary>
	Task StoreAsync(Guid runId, RunSecretCredential credential, string actor, TimeSpan expiresIn, CancellationToken cancellationToken);

	/// <summary>
	/// Decrypts the secret stored for <paramref name="runId"/> for use by the job
	/// identified by <paramref name="jobId"/>, returning both halves of the pair
	/// (<see cref="RunSecretCredential.Username"/> travels alongside the ciphertext,
	/// unencrypted, same as <c>credentials.username</c> sits beside
	/// <c>credential_secrets</c> for the stored tier). Audited as
	/// <c>secret.run_decrypted</c> in the same transaction as the ciphertext read
	/// (fail-closed, mirrors <see cref="ICredentialSecretStore.DecryptAsync"/>). Returns
	/// <c>null</c> if no row exists (never registered, already deleted on a prior
	/// terminal completion, or swept as expired) -- callers must treat that as
	/// "credential unavailable", never fall back to a stored credential (ADR-0011's "no
	/// personal rows, ever").
	/// </summary>
	Task<DecryptedRunSecret?> DecryptAsync(Guid runId, Guid jobId, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Deletes the row for <paramref name="runId"/> if present. Audited as
	/// <c>secret.run_deleted</c> when a row existed. The backend calls this the moment
	/// it observes the run reach a terminal state (completed, completed_with_failures,
	/// aborted) -- see <c>JobQueueRepository</c>'s run-completion paths.
	/// </summary>
	Task<bool> DeleteAsync(Guid runId, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// The cleanup sweep for abandoned/crashed runs (issue #434 AC): deletes every row
	/// whose <c>expires_at</c> has passed AND whose run is not still actively
	/// non-terminal-and-recently-active, auditing each as <c>secret.run_expired</c>.
	/// Implementations must not race a still-running job -- see
	/// <c>RunSecretStore.DeleteExpiredAsync</c>'s doc comment for the exact guard.
	/// Returns the number of rows deleted.
	/// </summary>
	Task<int> DeleteExpiredAsync(CancellationToken cancellationToken);
}
