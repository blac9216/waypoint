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
/// Identifies one row in the run-scoped ad hoc secret store (issue #586, epic #582):
/// either the LEGACY run-wide shape (<see cref="TargetId"/>/<see cref="Purpose"/> both
/// null, issue #434's original one-row-per-run contract, persisted as
/// <c>run_secrets.target_id IS NULL AND purpose = '_legacy'</c>, migration 0045), or a
/// fully-scoped per-target/per-purpose row (both set, a real
/// <see cref="CredentialPurposes"/> member). Never one without the other -- the schema's
/// <c>run_secrets_legacy_shape_check</c> CHECK backstops the same rule this type's
/// factories enforce.
/// </summary>
public readonly struct RunSecretKey : IEquatable<RunSecretKey>
{
	private RunSecretKey(Guid? targetId, string? purpose)
	{
		TargetId = targetId;
		Purpose = purpose;
	}

	/// <summary>The pre-#586 run-wide key: one shared ad hoc credential for every job in the run.</summary>
	public static readonly RunSecretKey Legacy = new(null, null);

	/// <summary>A per-target/per-purpose key (issue #586).</summary>
	public static RunSecretKey For(Guid targetId, string purpose)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
		if (!CredentialPurposes.IsValid(purpose))
		{
			throw new ArgumentException($"'{purpose}' is not a valid credential purpose.", nameof(purpose));
		}

		return new RunSecretKey(targetId, purpose);
	}

	public Guid? TargetId { get; }

	public string? Purpose { get; }

	public bool IsLegacy => TargetId is null;

	public bool Equals(RunSecretKey other) => TargetId == other.TargetId && Purpose == other.Purpose;

	public override bool Equals(object? obj) => obj is RunSecretKey other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(TargetId, Purpose);

	public static bool operator ==(RunSecretKey left, RunSecretKey right) => left.Equals(right);

	public static bool operator !=(RunSecretKey left, RunSecretKey right) => !left.Equals(right);
}

/// <summary>
/// Issue #434 (epic #433), re-keyed per-target/per-purpose by issue #586 (epic #582):
/// the encrypted, run-scoped replacement for <see cref="IEphemeralCredentialCache"/>'s
/// process-memory-only handoff. Same ADR-0011 contract -- "no personal rows, ever" in
/// the reusable <c>credentials</c> / <c>credential_secrets</c> tables -- but persisted in
/// a <c>run_secrets</c> table (envelope-encrypted with the same AES-256-GCM primitives as
/// <see cref="ICredentialSecretStore"/>) so:
///
/// - a dedicated compliance runner (ADR-0013/0014), which shares no process memory with
///   the API, can decrypt it locally at the point of use;
/// - an API restart between <c>POST /runs</c> and a runner's claim of the job no longer
///   forces the caller to re-enter the credential (the AC this issue exists for).
///
/// A run may now carry MULTIPLE rows -- one per (target, purpose), <see cref="RunSecretKey"/>
/// -- so a heterogeneous multi-target scan can supply a distinct ad hoc credential per
/// target/purpose without cross-target leakage (issue #586 AC). The pre-#586 shape (one
/// flat credential shared by every job in the run, <see cref="RunSecretKey.Legacy"/>)
/// keeps working unchanged for wire callers that have not adopted per-target overrides
/// yet (issue #586's wire-compat mapping; the wizard UI itself is issue #587).
///
/// Lifecycle differs from <see cref="ICredentialSecretStore"/> in two ways that matter:
/// there is no rotation (one write per key, at run creation, ever) and rows are
/// terminal/expiry bounded rather than living until an explicit delete -- see
/// <see cref="DeleteAsync"/> (backend deletes ALL of a run's rows, any key, on terminal
/// run completion) and <see cref="DeleteExpiredAsync"/> (the cleanup sweep for
/// abandoned/crashed runs that never reach a terminal state).
/// </summary>
public interface IRunSecretStore
{
	/// <summary>
	/// Stores <paramref name="credential"/> for <paramref name="runId"/> under
	/// <paramref name="key"/>, envelope-encrypted under an AAD context bound to the run
	/// AND the key (so a target/purpose row can never be decrypted under a sibling row's
	/// context even if ciphertexts were somehow swapped). Audited as
	/// <c>secret.run_registered</c> (fail-closed: if the audit row cannot commit, the
	/// secret is not stored -- same discipline as <see cref="ICredentialSecretStore.StoreAsync"/>).
	/// One row per (run, key); a second call for the same pair throws rather than
	/// silently overwriting, since nothing in this flow ever legitimately re-registers a
	/// run's secret. Multiple DIFFERENT keys under the same run coexist freely -- that is
	/// the whole point of issue #586's re-keying.
	/// </summary>
	Task StoreAsync(Guid runId, RunSecretKey key, RunSecretCredential credential, string actor, TimeSpan expiresIn, CancellationToken cancellationToken);

	/// <summary>Back-compat shortcut for <see cref="RunSecretKey.Legacy"/> -- the pre-#586 one-row-per-run shape.</summary>
	Task StoreAsync(Guid runId, RunSecretCredential credential, string actor, TimeSpan expiresIn, CancellationToken cancellationToken)
		=> StoreAsync(runId, RunSecretKey.Legacy, credential, actor, expiresIn, cancellationToken);

	/// <summary>
	/// Decrypts the secret stored for <paramref name="runId"/> under <paramref name="key"/>
	/// for use by the job identified by <paramref name="jobId"/>, returning both halves of
	/// the pair (<see cref="RunSecretCredential.Username"/> travels alongside the
	/// ciphertext, unencrypted, same as <c>credentials.username</c> sits beside
	/// <c>credential_secrets</c> for the stored tier). Audited as
	/// <c>secret.run_decrypted</c> with run/job/target/purpose attribution in the same
	/// transaction as the ciphertext read (fail-closed, mirrors
	/// <see cref="ICredentialSecretStore.DecryptAsync"/>) -- least-privilege: this reads
	/// and slides exactly the one (run, key) row a caller names, never any sibling
	/// target/purpose row on the same run. Returns <c>null</c> if no row exists (never
	/// registered, already deleted on a prior terminal completion, or swept as expired)
	/// -- callers must treat that as "credential unavailable", never fall back to a
	/// stored credential (ADR-0011's "no personal rows, ever").
	/// </summary>
	Task<DecryptedRunSecret?> DecryptAsync(Guid runId, RunSecretKey key, Guid jobId, string actor, CancellationToken cancellationToken);

	/// <summary>Back-compat shortcut for <see cref="RunSecretKey.Legacy"/>.</summary>
	Task<DecryptedRunSecret?> DecryptAsync(Guid runId, Guid jobId, string actor, CancellationToken cancellationToken)
		=> DecryptAsync(runId, RunSecretKey.Legacy, jobId, actor, cancellationToken);

	/// <summary>
	/// Deletes EVERY row for <paramref name="runId"/> -- legacy shape, per-target/per-purpose
	/// rows, or any mix -- if present. Audited once per row actually deleted (each as
	/// <c>secret.run_deleted</c>, carrying that row's own target/purpose attribution). The
	/// backend calls this the moment it observes the run reach a terminal state
	/// (completed, completed_with_failures, aborted) -- see <c>JobQueueRepository</c>'s
	/// run-completion paths -- deliberately run-wide (not per-key) because a terminal run
	/// has no job left that could still need any of its ad hoc credentials, regardless of
	/// how many distinct (target, purpose) rows it accumulated.
	/// </summary>
	Task<bool> DeleteAsync(Guid runId, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// The cleanup sweep for abandoned/crashed runs (issue #434 AC): deletes every row
	/// whose <c>expires_at</c> has passed AND whose run is not still actively
	/// non-terminal-and-recently-active, auditing each as <c>secret.run_expired</c>.
	/// Implementations must not race a still-running job -- see
	/// <c>RunSecretStore.DeleteExpiredAsync</c>'s doc comment for the exact guard. Sweeps
	/// every row shape (legacy and per-target/per-purpose) uniformly -- expiry is a
	/// per-row property, not a per-run one. Returns the number of rows deleted.
	/// </summary>
	Task<int> DeleteExpiredAsync(CancellationToken cancellationToken);
}
