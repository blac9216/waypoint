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

using Waypoint.Core.Logging;
using Xunit;

namespace Waypoint.Tests.Core;

/// <summary>
/// The security.md control-1 scrubber's contract: a tracked secret disappears from
/// every line for exactly as long as it is in play, including the two cases that
/// break naive implementations -- one secret embedded in another, and one secret
/// held by two overlapping jobs.
/// </summary>
public sealed class InPlaySecretRedactorTests
{
	[Fact]
	public void TrackedSecret_IsReplacedEverywhere_UntilDisposed()
	{
		InPlaySecretRedactor redactor = new();
		using (redactor.Track("hunter2-example-value"))
		{
			Assert.Equal(
				"password=[REDACTED] retry with [REDACTED]",
				redactor.Redact("password=hunter2-example-value retry with hunter2-example-value"));
		}

		Assert.Equal("password=hunter2-example-value", redactor.Redact("password=hunter2-example-value"));
	}

	/// <summary>Replacing the shorter secret first would split the longer one into two
	/// unredacted halves -- the ordering the type comment promises must actually hold.</summary>
	[Fact]
	public void SecretEmbeddedInALongerSecret_LongerOneWinsFirst()
	{
		InPlaySecretRedactor redactor = new();
		using IDisposable token = redactor.Track("tok-abc123");
		using IDisposable connectionString = redactor.Track("Server=db;Password=tok-abc123;Timeout=5");

		string redacted = redactor.Redact("failed using 'Server=db;Password=tok-abc123;Timeout=5' (token tok-abc123)");

		Assert.DoesNotContain("tok-abc123", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("Server=db", redacted, StringComparison.Ordinal);
	}

	/// <summary>The same credential decrypted by two overlapping jobs must stay redacted until the LAST handle goes away.</summary>
	[Fact]
	public void OverlappingTracksOfTheSameValue_RedactUntilTheLastDispose()
	{
		InPlaySecretRedactor redactor = new();
		IDisposable first = redactor.Track("shared-secret-value");
		IDisposable second = redactor.Track("shared-secret-value");

		first.Dispose();
		Assert.Equal("[REDACTED]", redactor.Redact("shared-secret-value"));

		second.Dispose();
		Assert.Equal("shared-secret-value", redactor.Redact("shared-secret-value"));
	}

	[Fact]
	public void DoubleDispose_DoesNotUnredactAnotherHandlesValue()
	{
		InPlaySecretRedactor redactor = new();
		IDisposable first = redactor.Track("shared-secret-value");
		using IDisposable second = redactor.Track("shared-secret-value");

		first.Dispose();
		first.Dispose();

		Assert.Equal("[REDACTED]", redactor.Redact("shared-secret-value"));
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	public void SecretsShorterThanTheMinimum_AreRejected(string tooShort)
	{
		InPlaySecretRedactor redactor = new();
		Assert.Throws<ArgumentException>(() => redactor.Track(tooShort));
	}

	[Fact]
	public void NoTrackedSecrets_ReturnsTheInstance_NotACopy()
	{
		InPlaySecretRedactor redactor = new();
		string line = "nothing secret here";
		Assert.Same(line, redactor.Redact(line));
	}
}
