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

using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Waypoint.Core.Logging;

/// <summary>
/// Registers a secret value as "in play" for the lifetime of the returned handle --
/// called wherever a secret is decrypted or otherwise materialized (ADR-0005 store,
/// run initiation for personal-tier credentials), disposed when the work that needed
/// it finishes. See <see cref="ISecretRedactor"/> for the control this feeds.
/// </summary>
public interface ISecretTracker
{
	/// <summary>
	/// Starts redacting <paramref name="secretValue"/> everywhere until the returned
	/// handle is disposed. Values shorter than <see cref="InPlaySecretRedactor.MinimumSecretLength"/>
	/// are rejected: redacting a 1-3 character fragment would corrupt nearly every log
	/// line while protecting nothing a brute force would not enumerate instantly.
	/// </summary>
	IDisposable Track(string secretValue);
}

/// <summary>
/// The real security.md control-1 scrubber, replacing the scaffold's
/// <c>NoOpSecretRedactor</c>: maintains the set of secret values currently in play and
/// replaces every occurrence in any text passed through <see cref="Redact"/>. One
/// instance is registered as both <see cref="ISecretRedactor"/> (the sinks' read side:
/// Serilog console/file lines, <c>job_events</c> payloads) and <see cref="ISecretTracker"/>
/// (the write side for code that decrypts).
///
/// Longest-first replacement is deliberate: when one tracked secret is a substring of
/// another (a token and the connection string embedding it), replacing the shorter one
/// first would split the longer one into two unredacted halves.
/// </summary>
public sealed class InPlaySecretRedactor : ISecretRedactor, ISecretTracker
{
	public const int MinimumSecretLength = 4;

	internal const string Replacement = "[REDACTED]";

	// Count: how many concurrent Track handles hold the secret -- the same credential
	// decrypted by two overlapping jobs must stay redacted until the LAST handle goes
	// away. Needles: every spelling Redact must look for -- the raw value plus its
	// JSON-escaped forms (#152): a payload that went through System.Text.Json carries
	// `pa"ss` as `pa\u0022ss` (default encoder) or `pa\"ss` (relaxed encoder), and an
	// ordinal match on the raw value alone would let the escaped occurrence through,
	// or worse, redact only the unescaped half.
	//
	// Count and needles live in ONE dictionary value on purpose (PR #155 round 1): a
	// two-dictionary split had an interleaving -- last-handle untrack removes the
	// count entry, a concurrent re-track's needle TryAdd no-ops against the not yet
	// removed needle entry, the untrack then deletes it -- that left a live tracked
	// secret with no escaped needles for the life of the new handle. One atomic value
	// makes that state unrepresentable. The record's value equality is what lets
	// Untrack's compare-exchange loops work: `with { Count = ... }` keeps the same
	// needles array reference, so equality is count + array identity.
	private sealed record TrackedSecret(int Count, string[] Needles);

	private readonly ConcurrentDictionary<string, TrackedSecret> _inPlay = new(StringComparer.Ordinal);

	public IDisposable Track(string secretValue)
	{
		ArgumentNullException.ThrowIfNull(secretValue);
		if (secretValue.Length < MinimumSecretLength)
		{
			throw new ArgumentException(
				$"Secret values shorter than {MinimumSecretLength} characters cannot be tracked for redaction.", nameof(secretValue));
		}

		// The add factory may run more than once under contention; DeriveNeedles is
		// pure, so a discarded extra derivation is harmless.
		_inPlay.AddOrUpdate(
			secretValue,
			static value => new TrackedSecret(1, DeriveNeedles(value)),
			static (_, existing) => existing with { Count = existing.Count + 1 });
		return new TrackHandle(this, secretValue);
	}

	public string Redact(string line)
	{
		if (string.IsNullOrEmpty(line) || _inPlay.IsEmpty)
		{
			return line;
		}

		// Snapshot, then order longest-first (see the type comment). The snapshot makes
		// a concurrent Untrack mid-redaction harmless: the worst case is redacting a
		// value whose handle was just disposed, never missing one that is still live.
		string[] secrets = [.. _inPlay.Values
			.SelectMany(tracked => tracked.Needles)
			.Distinct(StringComparer.Ordinal)
			.OrderByDescending(needle => needle.Length)];
		StringBuilder? builder = null;
		foreach (string secret in secrets)
		{
			if (builder is null)
			{
				if (!line.Contains(secret, StringComparison.Ordinal))
				{
					continue;
				}

				builder = new StringBuilder(line);
			}

			builder.Replace(secret, Replacement);
		}

		return builder?.ToString() ?? line;
	}

	/// <summary>The raw value plus each distinct JSON-escaped spelling a serializer could
	/// have written into a payload, one level (#152) and two levels (#156) deep. Both
	/// encoders are derived at level 1 because both exist in this codebase's dependency
	/// surface: the default encoder writes \u0022-style escapes, the relaxed encoder
	/// writes \"-style.
	///
	/// Level 2 covers a secret that transits two JSON serialization layers before the
	/// sink -- tool output that is already JSON (an HTTP error body, a serialized
	/// object) quoted inside a log line that this codebase's own Emit() (the executor's
	/// stream handler) then serializes a second time into the job_events payload. Layer
	/// 1 is NOT guaranteed to use the same encoder as layer 2: Emit() always uses the
	/// default encoder, but layer 1 can be produced upstream by anything that escapes
	/// JSON relaxed-style -- notably PowerShell's own ConvertTo-Json, which a vendor
	/// module may call on tool output before it reaches this process (\"-style,
	/// matching JavaScriptEncoder.UnsafeRelaxedJsonEscaping's spelling, not the default
	/// encoder's \u0022-style). So level 2 is the cross product of {default, relaxed}
	/// applied to each level-1 form, not "the same encoder twice" -- four combinations,
	/// deduped to three because default(default-escaped) and relaxed(default-escaped)
	/// coincide (the default-escaped form has no raw &lt;&gt;&amp;' characters left for
	/// the relaxed encoder to treat differently). Still bounded, and deliberately NOT a
	/// deeper cross-product (three, four, ... levels): each additional level doubles the
	/// reachable set for a threat this codebase's call sites only compose twice (tool
	/// output already being JSON, quoted once into a log line, serialized once more into
	/// the payload). At most 6 needles per tracked secret (raw + 2 level-1 + 3 level-2)
	/// regardless of how many escapable characters the secret contains, deduplicated
	/// further for secrets where escaping is a no-op.</summary>
	private static string[] DeriveNeedles(string secretValue)
	{
		string defaultEscaped = JsonEncodedText.Encode(secretValue).ToString();
		string relaxedEscaped = JsonEncodedText.Encode(secretValue, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

		return new[]
		{
			secretValue,
			defaultEscaped,
			relaxedEscaped,
			JsonEncodedText.Encode(defaultEscaped).ToString(),
			JsonEncodedText.Encode(defaultEscaped, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
			JsonEncodedText.Encode(relaxedEscaped).ToString(),
			JsonEncodedText.Encode(relaxedEscaped, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
		}.Distinct(StringComparer.Ordinal).ToArray();
	}

	private void Untrack(string secretValue)
	{
		// Decrement-and-remove-at-zero, tolerant of a double Dispose (the second call
		// finds no entry, or a count another handle owns, and must not over-decrement --
		// TrackHandle guards the double call, this guards the removal race).
		while (_inPlay.TryGetValue(secretValue, out TrackedSecret? tracked))
		{
			if (tracked.Count <= 1)
			{
				if (_inPlay.TryRemove(new KeyValuePair<string, TrackedSecret>(secretValue, tracked)))
				{
					return;
				}
			}
			else if (_inPlay.TryUpdate(secretValue, tracked with { Count = tracked.Count - 1 }, tracked))
			{
				return;
			}
		}
	}

	private sealed class TrackHandle : IDisposable
	{
		private InPlaySecretRedactor? _owner;
		private readonly string _secretValue;

		public TrackHandle(InPlaySecretRedactor owner, string secretValue)
		{
			_owner = owner;
			_secretValue = secretValue;
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _owner, null)?.Untrack(_secretValue);
		}
	}
}
