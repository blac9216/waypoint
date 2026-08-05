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
/// Supplies the appliance master key (ADR-0005). The key comes from a mounted file
/// only -- never an environment variable's value (security.md control 6: env vars
/// leak via /proc, inspect output, and error reports; a file mount does not).
/// Implementations load lazily and fail closed: a process with no usable master key
/// starts fine and refuses secret operations with an operator-actionable error.
/// </summary>
public interface IMasterKeyProvider
{
	/// <summary>The key material and its stable identifier. Throws <see cref="MasterKeyUnavailableException"/> when no usable key exists.</summary>
	MasterKey GetKey();
}

/// <summary>32 bytes of key material plus the deterministic id recorded per secret (<c>credential_secrets.master_key_id</c>) so rotation is representable.</summary>
public sealed record MasterKey(byte[] Key, string KeyId);

/// <summary>
/// AES-256-GCM envelope encryption (ADR-0005, AWX pattern): a random per-secret data
/// key encrypts the value; the master key wraps the data key. The associated-data
/// context binds both layers to their credential row, so a valid blob copied onto
/// another credential fails authentication instead of decrypting.
/// </summary>
public interface IEnvelopeCipher
{
	SecretEnvelope Encrypt(byte[] plaintext, string associatedContext);

	/// <summary>Throws <see cref="MasterKeyUnavailableException"/> for a missing/rotated master key and <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/> for tampered or context-swapped input.</summary>
	byte[] Decrypt(SecretEnvelope envelope, string associatedContext);
}

/// <summary>The stored shape: maps 1:1 onto <c>credential_secrets</c>' ciphertext / data_key_wrapped / master_key_id / algorithm columns.</summary>
public sealed record SecretEnvelope(byte[] Ciphertext, byte[] WrappedDataKey, string MasterKeyId, string Algorithm);

/// <summary>No usable master key. The message is written for the operator: it names the env var, the expected format, and what to fix.</summary>
public sealed class MasterKeyUnavailableException : Exception
{
	public MasterKeyUnavailableException(string message)
		: base(message)
	{
	}

	public MasterKeyUnavailableException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
