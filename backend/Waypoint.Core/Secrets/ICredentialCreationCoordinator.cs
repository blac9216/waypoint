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
/// Issue #188: <c>POST /credentials</c> with a secret must be atomic -- the metadata
/// row and the secret blob commit together or not at all, so a store failure (a bad
/// master key, a DB blip) never leaves an orphan metadata row with
/// <c>has_secret=false</c> that makes a client retry 409 <c>name_taken</c> for a
/// credential it believes was never created.
///
/// This lives in <c>Waypoint.Core</c> (no Npgsql dependency) so
/// <c>CredentialsController</c> can depend on an abstraction rather than a concrete
/// transaction type; the <c>Waypoint.Infrastructure</c> implementation is the only
/// place that opens the shared connection/transaction, mirroring
/// <c>ConfigDocRepository.SaveAsync</c>'s intra-class composition (issue #270) but
/// across the two different classes (<c>CredentialRepository</c>,
/// <c>CredentialSecretStore</c>) this create path touches.
/// </summary>
public interface ICredentialCreationCoordinator
{
	/// <summary>
	/// Creates the credential metadata row and, when <paramref name="secretValue"/> is
	/// non-null, stores the secret and stamps <c>rotated_at</c> -- all in one
	/// transaction. Returns null when <paramref name="name"/> is already taken (same
	/// contract as <c>CredentialRepository.CreateAsync</c>; the caller maps that to a
	/// 409). Any failure after the metadata insert (including a secret-store failure)
	/// rolls back the metadata row too, so a retry with the same name never sees a
	/// spurious conflict.
	/// </summary>
	Task<Guid?> CreateAsync(
		string name,
		string credentialType,
		string owner,
		bool sudoEnabled,
		string? username,
		byte[]? secretValue,
		string actor,
		CancellationToken cancellationToken);
}
