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

using System.Text.Encodings.Web;
using System.Text.Json;
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
	private static readonly JsonSerializerOptions RelaxedEncoderOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

	/// <summary>#152: a payload that went through a JSON serializer carries the secret in
	/// escaped spelling; the ordinal match on the raw value alone would miss it (default
	/// encoder) or redact only the unescaped half (relaxed encoder).</summary>
	[Fact]
	public void ASecretWithJsonEscapableCharacters_IsRedactedInSerializedPayloads()
	{
		InPlaySecretRedactor redactor = new();
		const string canary = "pa\"ss\\wo<rd-invented";
		using IDisposable handle = redactor.Track(canary);

		string defaultEncoded = JsonSerializer.Serialize(new { line = $"auth {canary} rejected" });
		string relaxedEncoded = JsonSerializer.Serialize(
			new { line = $"auth {canary} rejected" }, RelaxedEncoderOptions);

		foreach (string payload in new[] { defaultEncoded, relaxedEncoded })
		{
			string redacted = redactor.Redact(payload);
			Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
			Assert.DoesNotContain("invented", redacted, StringComparison.Ordinal);
			Assert.DoesNotContain("ss", redacted, StringComparison.Ordinal);
		}
	}

	/// <summary>PR #155 round 1: churning Track/Dispose of the same secret on other
	/// threads must never strip a live handle of its escaped needles -- count and
	/// needles are one atomic value, so the two-dictionary interleaving that could
	/// leave a live secret matching only its raw spelling is unrepresentable.</summary>
	[Fact]
	public async Task ChurningOverlappingTracks_NeverLoseTheEscapedNeedles()
	{
		InPlaySecretRedactor redactor = new();
		const string canary = "chu\"rn-canary-invented";
		string escapedPayload = JsonSerializer.Serialize(new { line = canary });
		using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));

		Task churn = Task.Run(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				redactor.Track(canary).Dispose();
			}
		});

		while (!stop.IsCancellationRequested)
		{
			using IDisposable live = redactor.Track(canary);
			string redacted = redactor.Redact(escapedPayload);
			Assert.DoesNotContain("rn-canary-invented", redacted, StringComparison.Ordinal);
		}

		await churn;
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
