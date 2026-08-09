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

using Waypoint.Core.Auth;

namespace Waypoint.Tests.Core;

public sealed class Pbkdf2PasswordHasherTests
{
	private const string Password = "correct-horse-battery-staple";

	[Fact]
	public void Verify_WithCorrectPassword_ReturnsTrue()
	{
		string stored = Pbkdf2PasswordHasher.Hash(Password);

		Assert.True(Pbkdf2PasswordHasher.Verify(Password, stored));
	}

	[Fact]
	public void Verify_WithWrongPassword_ReturnsFalse()
	{
		string stored = Pbkdf2PasswordHasher.Hash(Password);

		Assert.False(Pbkdf2PasswordHasher.Verify("wrong-password", stored));
	}

	[Fact]
	public void Hash_CalledTwiceForSamePassword_ProducesDifferentStoredValues()
	{
		string first = Pbkdf2PasswordHasher.Hash(Password);
		string second = Pbkdf2PasswordHasher.Hash(Password);

		Assert.NotEqual(first, second);
	}

	[Fact]
	public void Hash_CalledTwiceForSamePassword_BothVerifySuccessfully()
	{
		string first = Pbkdf2PasswordHasher.Hash(Password);
		string second = Pbkdf2PasswordHasher.Hash(Password);

		Assert.True(Pbkdf2PasswordHasher.Verify(Password, first));
		Assert.True(Pbkdf2PasswordHasher.Verify(Password, second));
	}

	[Fact]
	public void Hash_ProducesSelfDescribingFormat_ThatRoundTripsThroughVerify()
	{
		string stored = Pbkdf2PasswordHasher.Hash(Password);
		string[] parts = stored.Split('$');

		Assert.Equal(5, parts.Length);
		Assert.Equal("pbkdf2", parts[0]);
		Assert.Equal("sha256", parts[1]);
		Assert.Equal(Pbkdf2PasswordHasher.DefaultIterations.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[2]);
		Assert.True(Pbkdf2PasswordHasher.IsWellFormed(stored));
		Assert.True(Pbkdf2PasswordHasher.Verify(Password, stored));
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-a-hash")]
	[InlineData("pbkdf2$sha256$0$salt$hash")]
	[InlineData("pbkdf2$sha256$600000$not-base64!!$also-not-base64!!")]
	[InlineData("bcrypt$sha256$600000$c2FsdA==$aGFzaA==")]
	[InlineData("pbkdf2$sha256$600000$c2FsdA==")]
	public void Verify_WithMalformedStoredHash_ReturnsFalseRatherThanThrowing(string malformed)
	{
		Assert.False(Pbkdf2PasswordHasher.Verify(Password, malformed));
	}

	[Fact]
	public void IsWellFormed_ForGarbageValue_ReturnsFalse()
	{
		Assert.False(Pbkdf2PasswordHasher.IsWellFormed("legacy-sha256-hex-digest"));
	}

	[Fact]
	public void Verify_HonorsIterationCountEncodedInStoredHash_NotJustTheCurrentDefault()
	{
		// A lower iteration count than DefaultIterations must still verify correctly —
		// Verify reads the parameters from the stored string, it does not assume today's
		// constant, so a future bump to DefaultIterations cannot break hashes already
		// on disk/in config.
		const int LowerIterations = 1_000;
		byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
		byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
			Password,
			salt,
			LowerIterations,
			System.Security.Cryptography.HashAlgorithmName.SHA256,
			32);
		string stored = string.Join('$', "pbkdf2", "sha256", LowerIterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));

		Assert.True(Pbkdf2PasswordHasher.Verify(Password, stored));
	}
}
