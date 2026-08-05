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

using System.Security.Cryptography;
using System.Text;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// The ADR-0005 envelope, concretely. Both layers are AES-256-GCM with a fresh
/// random 12-byte nonce and the same associated-data context:
///
///   ciphertext       = nonce || tag || AESGCM(dataKey,   plaintext, aad=context)
///   data_key_wrapped = nonce || tag || AESGCM(masterKey, dataKey,   aad=context)
///
/// The shared AAD is what binds a blob to its credential: swap a valid envelope
/// onto another credential row and BOTH layers fail their tag check. Decrypt
/// verifies the envelope's <c>master_key_id</c> against the current key first, so
/// "wrong key" surfaces as the operator-actionable
/// <see cref="MasterKeyUnavailableException"/> rather than a bare tag mismatch.
/// </summary>
public sealed class AesGcmEnvelopeCipher : IEnvelopeCipher
{
	public const string AlgorithmName = "AES-256-GCM";

	private const int NonceSize = 12;
	private const int TagSize = 16;
	private const int DataKeySize = 32;

	private readonly IMasterKeyProvider _masterKeyProvider;

	public AesGcmEnvelopeCipher(IMasterKeyProvider masterKeyProvider)
	{
		ArgumentNullException.ThrowIfNull(masterKeyProvider);
		_masterKeyProvider = masterKeyProvider;
	}

	public SecretEnvelope Encrypt(byte[] plaintext, string associatedContext)
	{
		ArgumentNullException.ThrowIfNull(plaintext);
		ArgumentException.ThrowIfNullOrWhiteSpace(associatedContext);

		MasterKey masterKey = _masterKeyProvider.GetKey();
		byte[] dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
		try
		{
			byte[] ciphertext = Seal(dataKey, plaintext, associatedContext);
			byte[] wrappedKey = Seal(masterKey.Key, dataKey, associatedContext);
			return new SecretEnvelope(ciphertext, wrappedKey, masterKey.KeyId, AlgorithmName);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(dataKey);
		}
	}

	public byte[] Decrypt(SecretEnvelope envelope, string associatedContext)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		ArgumentException.ThrowIfNullOrWhiteSpace(associatedContext);

		MasterKey masterKey = _masterKeyProvider.GetKey();
		if (!string.Equals(envelope.MasterKeyId, masterKey.KeyId, StringComparison.Ordinal))
		{
			throw new MasterKeyUnavailableException(
				$"This secret was sealed with master key '{envelope.MasterKeyId}' but the mounted key is '{masterKey.KeyId}'. " +
				"Either the wrong key file is mounted, or the key was rotated without re-wrapping stored secrets.");
		}

		if (!string.Equals(envelope.Algorithm, AlgorithmName, StringComparison.Ordinal))
		{
			throw new MasterKeyUnavailableException(
				$"This secret uses algorithm '{envelope.Algorithm}', which this build does not support ('{AlgorithmName}' only).");
		}

		byte[] dataKey = Open(masterKey.Key, envelope.WrappedDataKey, associatedContext);
		try
		{
			return Open(dataKey, envelope.Ciphertext, associatedContext);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(dataKey);
		}
	}

	private static byte[] Seal(byte[] key, byte[] plaintext, string associatedContext)
	{
		byte[] output = new byte[NonceSize + TagSize + plaintext.Length];
		Span<byte> nonce = output.AsSpan(0, NonceSize);
		Span<byte> tag = output.AsSpan(NonceSize, TagSize);
		Span<byte> sealedBytes = output.AsSpan(NonceSize + TagSize);

		RandomNumberGenerator.Fill(nonce);
		using AesGcm aes = new(key, TagSize);
		aes.Encrypt(nonce, plaintext, sealedBytes, tag);
		return output;
	}

	private static byte[] Open(byte[] key, byte[] blob, string associatedContext)
	{
		if (blob.Length < NonceSize + TagSize)
		{
			throw new AuthenticationTagMismatchException();
		}

		ReadOnlySpan<byte> nonce = blob.AsSpan(0, NonceSize);
		ReadOnlySpan<byte> tag = blob.AsSpan(NonceSize, TagSize);
		ReadOnlySpan<byte> sealedBytes = blob.AsSpan(NonceSize + TagSize);

		byte[] plaintext = new byte[sealedBytes.Length];
		using AesGcm aes = new(key, TagSize);
		aes.Decrypt(nonce, sealedBytes, tag, plaintext);
		return plaintext;
	}
}
