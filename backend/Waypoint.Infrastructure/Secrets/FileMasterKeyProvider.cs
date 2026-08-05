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
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// Loads the master key from the file named by <c>WAYPOINT_MASTER_KEY_FILE</c>
/// (deploy mounts it; ADR-0005). Accepted formats: exactly 32 raw bytes, or 64 hex
/// characters (optionally newline-terminated) -- the hex form is what
/// <c>openssl rand -hex 32</c> produces, the raw form what <c>head -c32</c> does.
///
/// Lazy and fail-closed on purpose: construction never throws (a Testing host or a
/// disconnected instance without secrets must still boot), the first
/// <see cref="GetKey"/> does the load, and every failure names the env var and the
/// fix. The result -- including a failure -- is cached: a key that was wrong at
/// first use does not silently become right without a restart, which keeps the
/// failure mode deterministic for the operator.
/// </summary>
public sealed class FileMasterKeyProvider : IMasterKeyProvider
{
	public const string KeyFileEnvironmentVariable = "WAYPOINT_MASTER_KEY_FILE";

	private readonly Lazy<MasterKey> _key;

	public FileMasterKeyProvider(string? keyFilePath = null)
	{
		_key = new Lazy<MasterKey>(
			() => Load(keyFilePath ?? Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable)),
			LazyThreadSafetyMode.ExecutionAndPublication);
	}

	public MasterKey GetKey() => _key.Value;

	private static MasterKey Load(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new MasterKeyUnavailableException(
				$"No master key is configured: set {KeyFileEnvironmentVariable} to the path of a mounted key file " +
				"(32 raw bytes, or 64 hex characters, e.g. from 'openssl rand -hex 32'). " +
				"The key must be provided as a file mount, never as an environment variable value.");
		}

		byte[] raw;
		try
		{
			raw = File.ReadAllBytes(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new MasterKeyUnavailableException(
				$"The master key file named by {KeyFileEnvironmentVariable} ('{path}') could not be read: {exception.Message}", exception);
		}

		// #182: Normalize runs INSIDE the wipe scope, so a malformed file's content is
		// zeroed on the throw path too. Two documented limitations remain: the hex path
		// materializes the key as a managed string (unwipeable -- the ADR-0005 trade
		// recorded on #182), and a file of exactly 32 bytes is always treated as raw,
		// even if those bytes happen to be ASCII hex characters (raw takes precedence;
		// a 32-char hex string is not a valid 64-char hex key anyway).
		try
		{
			byte[] key = Normalize(raw, path);

			// Key id: first 8 bytes of SHA-256 of the key material, hex. Deterministic
			// per key, reveals nothing recoverable, and short enough to read in a log
			// line or a credential_secrets row.
			byte[] digest = SHA256.HashData(key);
			string keyId = $"wpk-{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
			return new MasterKey(key, keyId);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(raw);
		}
	}

	private static byte[] Normalize(byte[] raw, string path)
	{
		if (raw.Length == 32)
		{
			return (byte[])raw.Clone();
		}

		// 64 hex chars, tolerating trailing whitespace/newline from shell redirection.
		// Decoded byte-by-byte from the raw buffer on purpose (#182 AC2): a managed
		// string of the key material could never be wiped; these nibbles live only in
		// the caller-wiped raw buffer and the returned key.
		int length = raw.Length;
		while (length > 0 && raw[length - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
		{
			length--;
		}

		if (length == 64)
		{
			byte[] key = new byte[32];
			for (int index = 0; index < 32; index++)
			{
				int high = HexNibble(raw[index * 2]);
				int low = HexNibble(raw[(index * 2) + 1]);
				if (high < 0 || low < 0)
				{
					CryptographicOperations.ZeroMemory(key);
					key = null!;
					break;
				}

				key[index] = (byte)((high << 4) | low);
			}

			if (key is not null)
			{
				return key;
			}
		}

		throw new MasterKeyUnavailableException(
			$"The master key file named by {KeyFileEnvironmentVariable} ('{path}') has the wrong format: expected exactly " +
			$"32 raw bytes or 64 hex characters, found {raw.Length} byte(s). Generate one with 'openssl rand -hex 32'.");
	}

	private static int HexNibble(byte character) => character switch
	{
		>= (byte)'0' and <= (byte)'9' => character - '0',
		>= (byte)'a' and <= (byte)'f' => character - 'a' + 10,
		>= (byte)'A' and <= (byte)'F' => character - 'A' + 10,
		_ => -1,
	};
}
