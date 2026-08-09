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

namespace Waypoint.Core.Auth;

/// <summary>
/// Salted, iterated password hashing for the dev-grade local admin credential
/// (<see cref="Waypoint.Core.Configuration.LocalAuthOptions.AdminPasswordHash"/>).
///
/// <para>
/// Uses PBKDF2-HMAC-SHA256 via the .NET BCL (<see cref="Rfc2898DeriveBytes"/>) rather
/// than bcrypt or Argon2: PBKDF2 is FIPS 140-validated (relevant for this DoD-audience
/// appliance) and needs no new dependency — bcrypt/Argon2 have no BCL implementation.
/// </para>
/// <para>
/// The stored format is self-describing — <c>pbkdf2$sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>
/// — so the iteration count (and algorithm) can be raised later without a silent format
/// break: <see cref="Verify"/> reads the parameters out of the stored string instead of
/// assuming today's constants.
/// </para>
/// <para>
/// This entire type is replaced by Keycloak OIDC in issue #29 along with the rest of
/// <see cref="ILocalAuthenticationService"/> — it exists only to close the credential
/// weakness recorded in issue #62 for however long local auth remains in the tree.
/// </para>
/// </summary>
public static class Pbkdf2PasswordHasher
{
	/// <summary>Algorithm tag in the stored format.</summary>
	private const string Prefix = "pbkdf2";

	/// <summary>Hash-function tag in the stored format.</summary>
	private const string Algorithm = "sha256";

	/// <summary>
	/// OWASP-current (2023 cheat sheet) minimum for PBKDF2-HMAC-SHA256: 600,000 rounds.
	/// </summary>
	public const int DefaultIterations = 600_000;

	/// <summary>128-bit random salt — generous relative to the 32-bit minimum RFC 8018 recommends.</summary>
	private const int SaltSizeBytes = 16;

	/// <summary>256-bit derived key, matching the SHA-256 PRF's output size.</summary>
	private const int HashSizeBytes = 32;

	/// <summary>
	/// Hashes <paramref name="password"/> with a fresh random salt and
	/// <see cref="DefaultIterations"/> rounds, returning the self-describing stored form.
	/// This is what an operator runs once (see <c>Waypoint.Api --hash-password</c>) to
	/// produce the value that goes in <c>LocalAuth__AdminPasswordHash</c>.
	/// </summary>
	public static string Hash(string password)
	{
		ArgumentNullException.ThrowIfNull(password);

		byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
		byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
			password,
			salt,
			DefaultIterations,
			HashAlgorithmName.SHA256,
			HashSizeBytes);

		return Format(DefaultIterations, salt, hash);
	}

	/// <summary>
	/// Verifies <paramref name="password"/> against a stored value produced by
	/// <see cref="Hash"/>. Reads the iteration count and salt out of
	/// <paramref name="storedHash"/> rather than assuming <see cref="DefaultIterations"/>,
	/// so a future bump to the constant does not invalidate hashes already on disk/in
	/// config. Returns <see langword="false"/> — never throws — for a malformed stored
	/// value, so an operator's typo in <c>LocalAuth__AdminPasswordHash</c> fails closed
	/// like an unset one does.
	/// </summary>
	public static bool Verify(string password, string storedHash)
	{
		ArgumentNullException.ThrowIfNull(password);

		if (string.IsNullOrEmpty(storedHash))
		{
			return false;
		}

		if (!TryParse(storedHash, out int iterations, out byte[]? salt) || salt is null)
		{
			return false;
		}

		byte[] candidate = Rfc2898DeriveBytes.Pbkdf2(
			password,
			salt,
			iterations,
			HashAlgorithmName.SHA256,
			HashSizeBytes);

		// The stored hash bytes are re-derived (not string-compared) so the constant-time
		// check operates on fixed-length raw bytes rather than variable-content base64.
		byte[] storedHashBytes = ExtractHashBytes(storedHash);
		return storedHashBytes.Length == candidate.Length
			&& CryptographicOperations.FixedTimeEquals(candidate, storedHashBytes);
	}

	/// <summary>True when <paramref name="value"/> parses as a well-formed stored hash.</summary>
	public static bool IsWellFormed(string value)
	{
		return TryParse(value, out _, out _);
	}

	private static string Format(int iterations, byte[] salt, byte[] hash)
	{
		return string.Join(
			'$',
			Prefix,
			Algorithm,
			iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
			Convert.ToBase64String(salt),
			Convert.ToBase64String(hash));
	}

	private static bool TryParse(string storedHash, out int iterations, out byte[]? salt)
	{
		iterations = 0;
		salt = null;

		string[] parts = storedHash.Split('$');
		if (parts.Length != 5
			|| !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
			|| !string.Equals(parts[1], Algorithm, StringComparison.Ordinal))
		{
			return false;
		}

		if (!int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out iterations)
			|| iterations <= 0)
		{
			return false;
		}

		try
		{
			salt = Convert.FromBase64String(parts[3]);
			_ = Convert.FromBase64String(parts[4]);
		}
		catch (FormatException)
		{
			return false;
		}

		return true;
	}

	private static byte[] ExtractHashBytes(string storedHash)
	{
		string[] parts = storedHash.Split('$');
		return Convert.FromBase64String(parts[4]);
	}
}
