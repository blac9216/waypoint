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
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Secrets;

/// <summary>
/// Epic #8 slice 1 (#178): the ADR-0005 crypto core. Round trip, every tampering
/// direction failing closed, the credential-context binding, deterministic key ids,
/// operator-actionable key-file failures, and both accepted key-file formats.
/// </summary>
public sealed class EnvelopeCryptoTests : IDisposable
{
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-key-test").FullName;

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	private string WriteKeyFile(byte[] content, string name = "master.key")
	{
		string path = Path.Combine(_keyDirectory, name);
		File.WriteAllBytes(path, content);
		return path;
	}

	private AesGcmEnvelopeCipher CreateCipher(out FileMasterKeyProvider provider, byte[]? key = null)
	{
		key ??= RandomNumberGenerator.GetBytes(32);
		provider = new FileMasterKeyProvider(WriteKeyFile(key, $"key-{Guid.NewGuid():N}"));
		return new AesGcmEnvelopeCipher(provider);
	}

	[Fact]
	public void RoundTrip_ReturnsThePlaintext_AndRecordsTheKeyId()
	{
		AesGcmEnvelopeCipher cipher = CreateCipher(out FileMasterKeyProvider provider);
		byte[] plaintext = Encoding.UTF8.GetBytes("invented-depot-token-value");

		SecretEnvelope envelope = cipher.Encrypt(plaintext, "credential:11111111-1111-1111-1111-111111111111");
		byte[] decrypted = cipher.Decrypt(envelope, "credential:11111111-1111-1111-1111-111111111111");

		Assert.Equal(plaintext, decrypted);
		Assert.Equal(provider.GetKey().KeyId, envelope.MasterKeyId);
		Assert.Equal("AES-256-GCM", envelope.Algorithm);
		Assert.NotEqual(plaintext, envelope.Ciphertext);
	}

	[Fact]
	public void EachEncrypt_UsesAFreshDataKeyAndNonce()
	{
		AesGcmEnvelopeCipher cipher = CreateCipher(out _);
		byte[] plaintext = Encoding.UTF8.GetBytes("same value");

		SecretEnvelope first = cipher.Encrypt(plaintext, "credential:x");
		SecretEnvelope second = cipher.Encrypt(plaintext, "credential:x");

		Assert.NotEqual(first.Ciphertext, second.Ciphertext);
		Assert.NotEqual(first.WrappedDataKey, second.WrappedDataKey);
	}

	[Theory]
	[InlineData("ciphertext")]
	[InlineData("wrappedKey")]
	public void TamperingWithEitherLayer_FailsClosed(string target)
	{
		AesGcmEnvelopeCipher cipher = CreateCipher(out _);
		SecretEnvelope envelope = cipher.Encrypt(Encoding.UTF8.GetBytes("invented"), "credential:x");

		byte[] tampered = target == "ciphertext" ? (byte[])envelope.Ciphertext.Clone() : (byte[])envelope.WrappedDataKey.Clone();
		tampered[^1] ^= 0x01;
		SecretEnvelope mutated = target == "ciphertext"
			? envelope with { Ciphertext = tampered }
			: envelope with { WrappedDataKey = tampered };

		Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(mutated, "credential:x"));
	}

	/// <summary>The AAD binding: a valid envelope moved onto another credential must not decrypt.</summary>
	[Fact]
	public void AnEnvelopeSwappedOntoAnotherCredential_FailsClosed()
	{
		AesGcmEnvelopeCipher cipher = CreateCipher(out _);
		SecretEnvelope envelope = cipher.Encrypt(Encoding.UTF8.GetBytes("invented"), "credential:aaaa");

		Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(envelope, "credential:bbbb"));
	}

	[Fact]
	public void AWrongMasterKey_SurfacesAsTheOperatorError_NotATagMismatch()
	{
		byte[] originalKey = RandomNumberGenerator.GetBytes(32);
		AesGcmEnvelopeCipher cipher = CreateCipher(out _, originalKey);
		SecretEnvelope envelope = cipher.Encrypt(Encoding.UTF8.GetBytes("invented"), "credential:x");

		AesGcmEnvelopeCipher otherCipher = CreateCipher(out _);
		MasterKeyUnavailableException error = Assert.Throws<MasterKeyUnavailableException>(
			() => otherCipher.Decrypt(envelope, "credential:x"));
		Assert.Contains(envelope.MasterKeyId, error.Message, StringComparison.Ordinal);
		Assert.Contains("rotated", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void KeyId_IsDeterministicPerKey_AndDiffersBetweenKeys()
	{
		byte[] key = RandomNumberGenerator.GetBytes(32);
		FileMasterKeyProvider first = new(WriteKeyFile(key, "a.key"));
		FileMasterKeyProvider second = new(WriteKeyFile(key, "b.key"));
		FileMasterKeyProvider different = new(WriteKeyFile(RandomNumberGenerator.GetBytes(32), "c.key"));

		Assert.Equal(first.GetKey().KeyId, second.GetKey().KeyId);
		Assert.StartsWith("wpk-", first.GetKey().KeyId, StringComparison.Ordinal);
		Assert.NotEqual(first.GetKey().KeyId, different.GetKey().KeyId);
	}

	[Fact]
	public void AHexKeyFile_IsAccepted_AndMatchesItsRawEquivalent()
	{
		byte[] key = RandomNumberGenerator.GetBytes(32);
		FileMasterKeyProvider raw = new(WriteKeyFile(key, "raw.key"));
		FileMasterKeyProvider hex = new(WriteKeyFile(
			Encoding.ASCII.GetBytes(Convert.ToHexString(key).ToLowerInvariant() + "\n"), "hex.key"));

		Assert.Equal(raw.GetKey().KeyId, hex.GetKey().KeyId);
		Assert.Equal(raw.GetKey().Key, hex.GetKey().Key);
	}

	[Fact]
	public void NoEnvironmentVariable_FailsClosed_NamingTheVariableAndTheFix()
	{
		FileMasterKeyProvider provider = new(keyFilePath: "");
		MasterKeyUnavailableException error = Assert.Throws<MasterKeyUnavailableException>(() => provider.GetKey());
		Assert.Contains(FileMasterKeyProvider.KeyFileEnvironmentVariable, error.Message, StringComparison.Ordinal);
		Assert.Contains("openssl rand -hex 32", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AMissingFile_FailsClosed_NamingThePath()
	{
		string missing = Path.Combine(_keyDirectory, "does-not-exist.key");
		FileMasterKeyProvider provider = new(missing);
		MasterKeyUnavailableException error = Assert.Throws<MasterKeyUnavailableException>(() => provider.GetKey());
		Assert.Contains(missing, error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(16)]
	[InlineData(31)]
	[InlineData(33)]
	[InlineData(64)] // 64 raw non-hex bytes: wrong, even though 64 hex CHARS would be right
	public void AWrongSizedKeyFile_FailsClosed(int byteCount)
	{
		// 0xFF bytes cannot be mistaken for ASCII hex, so the 64-byte case exercises
		// the "right length, not hex" path rather than accidentally parsing.
		byte[] content = new byte[byteCount];
		Array.Fill(content, (byte)0xFF);
		FileMasterKeyProvider provider = new(WriteKeyFile(content, $"wrong-{byteCount}.key"));
		MasterKeyUnavailableException error = Assert.Throws<MasterKeyUnavailableException>(() => provider.GetKey());
		Assert.Contains("32 raw bytes or 64 hex characters", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// #407: a transient read failure (file momentarily missing/unreadable, e.g. a
	/// mount race) must not poison the provider for process lifetime -- once the file
	/// becomes readable, the very next call succeeds without a restart.
	/// </summary>
	[Fact]
	public void ATransientFailure_DoesNotBlockASubsequentSuccess()
	{
		string path = Path.Combine(_keyDirectory, "late.key");
		FileMasterKeyProvider provider = new(path);
		Assert.Throws<MasterKeyUnavailableException>(() => provider.GetKey());

		// The file appearing later must fix the provider on the next access -- no
		// restart required.
		byte[] key = RandomNumberGenerator.GetBytes(32);
		File.WriteAllBytes(path, key);
		MasterKey loaded = provider.GetKey();
		Assert.Equal(key, loaded.Key);
	}

	/// <summary>
	/// #407: once a load succeeds, the result is cached forever -- the file is never
	/// re-read on subsequent calls. Proven by deleting the file after the first
	/// successful call: if GetKey() re-read the file, it would now throw.
	/// </summary>
	[Fact]
	public void ASuccessfulLoad_IsCachedForever_TheFileIsReadAtMostOnce()
	{
		string path = Path.Combine(_keyDirectory, "cached.key");
		byte[] key = RandomNumberGenerator.GetBytes(32);
		File.WriteAllBytes(path, key);
		FileMasterKeyProvider provider = new(path);

		MasterKey first = provider.GetKey();
		File.Delete(path);

		// If GetKey() touched the filesystem again here, it would throw
		// MasterKeyUnavailableException for the now-missing file. It must not.
		MasterKey second = provider.GetKey();
		Assert.Same(first, second);
		Assert.Equal(key, second.Key);
	}

	/// <summary>
	/// #407: two threads racing the first access while the load is failing must both
	/// observe the failure with no torn/partial state, and once the file becomes
	/// readable, later concurrent access must converge on one cached key.
	/// </summary>
	[Fact]
	public void ConcurrentFirstAccess_DuringAFailure_ThenAfterRecovery_ConvergesCleanly()
	{
		string path = Path.Combine(_keyDirectory, "concurrent.key");
		FileMasterKeyProvider provider = new(path);

		const int ThreadCount = 8;
		using Barrier barrier = new(ThreadCount);
		MasterKeyUnavailableException?[] failures = new MasterKeyUnavailableException?[ThreadCount];

		Parallel.For(0, ThreadCount, index =>
		{
			barrier.SignalAndWait();
			failures[index] = Assert.Throws<MasterKeyUnavailableException>(() => provider.GetKey());
		});

		Assert.All(failures, failure => Assert.NotNull(failure));

		byte[] key = RandomNumberGenerator.GetBytes(32);
		File.WriteAllBytes(path, key);

		MasterKey[] results = new MasterKey[ThreadCount];
		using Barrier recoveryBarrier = new(ThreadCount);
		Parallel.For(0, ThreadCount, index =>
		{
			recoveryBarrier.SignalAndWait();
			results[index] = provider.GetKey();
		});

		Assert.All(results, result => Assert.Same(results[0], result));
		Assert.Equal(key, results[0].Key);
	}
}
