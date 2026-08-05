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
/// The ADR-0005 write-only secret store over <c>credential_secrets</c>. Values go in
/// and are decrypted for use -- they are never read back out through any query API,
/// and rotation replaces the single row in place.
///
/// Two deliberate couplings on the decrypt path (epic #8 slice 2):
/// - **Audit is fail-closed** (security.md design goal 3, "detect use"): the
///   <c>audit_log</c> row commits in the same transaction that read the ciphertext;
///   if the audit cannot be written, the secret is not handed out.
/// - **Redaction is automatic** (control 1): the returned handle has already
///   <c>Track()</c>ed the value with the in-play redactor -- holding the handle IS
///   being redacted; disposing it ends the in-play window.
/// </summary>
public interface ICredentialSecretStore
{
	/// <summary>Stores or replaces (rotates) the credential's secret. Audited as <c>secret.stored</c>.</summary>
	Task StoreAsync(Guid credentialId, byte[] secretValue, string actor, CancellationToken cancellationToken);

	/// <summary>
	/// Decrypts for use. Audited as <c>secret.decrypted</c> with actor/job/run
	/// attribution in the same transaction. Throws
	/// <see cref="CredentialSecretNotFoundException"/> when no secret is stored and
	/// <see cref="MasterKeyUnavailableException"/> when the mounted key is missing,
	/// wrong, or rotated without a re-wrap.
	/// </summary>
	Task<DecryptedSecret> DecryptAsync(Guid credentialId, string actor, Guid? jobId, Guid? runId, CancellationToken cancellationToken);

	/// <summary>Deletes the stored secret if present. Audited as <c>secret.deleted</c> when a row existed.</summary>
	Task<bool> DeleteAsync(Guid credentialId, string actor, CancellationToken cancellationToken);
}

/// <summary>
/// A decrypted value, in play: the constructor path has already registered it with
/// the redactor, and <see cref="Dispose"/> ends that window. The value is a string
/// because its consumers (PowerShell parameter binding, HTTP clients) require one --
/// managed strings cannot be zeroed, which is the accepted ADR-0005 trade recorded
/// on #182.
/// </summary>
public sealed class DecryptedSecret : IDisposable
{
	private readonly IDisposable _redactionHandle;

	public DecryptedSecret(string value, IDisposable redactionHandle)
	{
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(redactionHandle);
		Value = value;
		_redactionHandle = redactionHandle;
	}

	public string Value { get; }

	public void Dispose()
	{
		_redactionHandle.Dispose();
	}
}

/// <summary>No secret is stored for the credential -- distinct from a crypto failure so callers can say "not configured" rather than "broken".</summary>
public sealed class CredentialSecretNotFoundException : Exception
{
	public CredentialSecretNotFoundException(Guid credentialId)
		: base($"No secret is stored for credential '{credentialId}'.")
	{
		CredentialId = credentialId;
	}

	public Guid CredentialId { get; }
}
