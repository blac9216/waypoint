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

using System.Reflection;
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

	/// <summary>#156: a secret that transits two JSON serialization layers before the
	/// sink -- e.g. tool output that is already JSON, quoted inside a log line that is
	/// itself serialized into a job_events payload -- is escaped twice
	/// (`pa"ss` -&gt; `pa\"ss` -&gt; `pa\\\"ss` / `pa\\u0022ss`). One level of needles
	/// (#152) matches neither doubly-escaped spelling; the second level must.</summary>
	[Fact]
	public void ASecretWithJsonEscapableCharacters_IsRedactedThroughTwoSerializationLayers()
	{
		InPlaySecretRedactor redactor = new();
		const string canary = "pa\"ss\\wo<rd-invented";
		using IDisposable handle = redactor.Track(canary);

		// Layer 1: the canary lands in a string field and gets JSON-escaped once.
		string layer1Default = JsonSerializer.Serialize(new { line = $"auth {canary} rejected" });
		string layer1Relaxed = JsonSerializer.Serialize(
			new { line = $"auth {canary} rejected" }, RelaxedEncoderOptions);

		// Layer 2: that already-escaped JSON string is itself quoted into another
		// payload (the realistic composition -- same encoder both times) and
		// JSON-escaped a second time, producing `pa\\\"ss` / `pa\\u0022ss` spellings.
		string layer2Default = JsonSerializer.Serialize(new { wrapped = layer1Default });
		string layer2Relaxed = JsonSerializer.Serialize(new { wrapped = layer1Relaxed }, RelaxedEncoderOptions);

		foreach (string payload in new[] { layer2Default, layer2Relaxed })
		{
			string redacted = redactor.Redact(payload);
			Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
			Assert.DoesNotContain("invented", redacted, StringComparison.Ordinal);
			Assert.DoesNotContain("ss", redacted, StringComparison.Ordinal);
		}
	}

	/// <summary>#156: needle derivation must stay bounded per tracked secret -- a
	/// deeper cross-product (three, four, ... escaping levels) would grow
	/// combinatorially. Raw + 2 level-1 (default, relaxed) + up to 3 distinct level-2
	/// (the {default, relaxed} x {default-escaped, relaxed-escaped} cross product,
	/// deduped) = 6 is the ceiling regardless of how many escapable characters the
	/// secret contains.</summary>
	[Fact]
	public void NeedleCount_StaysBoundedRegardlessOfEscapableCharacters()
	{
		const string heavilyEscapable = "a\"b\\c<d>e&f'gh-invented";

		FieldInfo? field = typeof(InPlaySecretRedactor).GetField("_inPlay", BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);

		InPlaySecretRedactor redactor = new();
		using IDisposable handle = redactor.Track(heavilyEscapable);

		object inPlay = field!.GetValue(redactor)!;
		System.Collections.IDictionary dictionary = (System.Collections.IDictionary)inPlay;
		object tracked = dictionary[heavilyEscapable]!;
		PropertyInfo? needlesProperty = tracked.GetType().GetProperty("Needles");
		Assert.NotNull(needlesProperty);
		string[] needles = (string[])needlesProperty!.GetValue(tracked)!;

		Assert.True(needles.Length <= 6, $"Expected at most 6 needles, got {needles.Length}.");
	}

	/// <summary>Over-redaction guard: text that merely resembles a partial escaped
	/// spelling of a tracked secret (same characters, different value) must survive
	/// untouched -- needles stay anchored to the actual tracked value, not to a
	/// generic escape pattern.</summary>
	[Fact]
	public void NearMissText_ResemblingAnEscapedNeedle_IsNotRedacted()
	{
		InPlaySecretRedactor redactor = new();
		const string canary = "pa\"ss\\wo<rd-invented";
		using IDisposable handle = redactor.Track(canary);

		// Shares the `\"` escape spelling and some characters with the canary's
		// escaped forms, but is not a substring occurrence of any derived needle.
		const string innocent = "totally unrelated text containing \\\" and pa\\u0022ther-invented, not the secret";

		Assert.Equal(innocent, redactor.Redact(innocent));
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
