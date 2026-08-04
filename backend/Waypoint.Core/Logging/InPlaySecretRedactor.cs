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

	// Key: the secret value. Value: how many concurrent Track handles hold it -- the
	// same credential decrypted by two overlapping jobs must stay redacted until the
	// LAST handle goes away.
	private readonly ConcurrentDictionary<string, int> _inPlay = new(StringComparer.Ordinal);

	// Key: the raw secret. Value: every needle Redact must look for -- the raw value
	// plus its JSON-escaped spellings (#152): a payload that went through
	// System.Text.Json carries `pa"ss` as `pa\u0022ss` (default encoder) or `pa\"ss`
	// (relaxed encoder), and an ordinal match on the raw value alone would let the
	// escaped occurrence through, or worse, redact only the unescaped half.
	private readonly ConcurrentDictionary<string, string[]> _needles = new(StringComparer.Ordinal);

	public IDisposable Track(string secretValue)
	{
		ArgumentNullException.ThrowIfNull(secretValue);
		if (secretValue.Length < MinimumSecretLength)
		{
			throw new ArgumentException(
				$"Secret values shorter than {MinimumSecretLength} characters cannot be tracked for redaction.", nameof(secretValue));
		}

		_inPlay.AddOrUpdate(secretValue, 1, (_, count) => count + 1);
		_needles.TryAdd(secretValue, DeriveNeedles(secretValue));
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
		string[] secrets = [.. _inPlay.Keys
			.SelectMany(secret => _needles.TryGetValue(secret, out string[]? needles) ? needles : [secret])
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
	/// have written into a payload. Both encoders are derived because both exist in this
	/// codebase's dependency surface: the default encoder writes \u0022-style escapes,
	/// the relaxed encoder writes \"-style.</summary>
	private static string[] DeriveNeedles(string secretValue)
	{
		return new[]
		{
			secretValue,
			JsonEncodedText.Encode(secretValue).ToString(),
			JsonEncodedText.Encode(secretValue, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
		}.Distinct(StringComparer.Ordinal).ToArray();
	}

	private void Untrack(string secretValue)
	{
		// Decrement-and-remove-at-zero, tolerant of a double Dispose (the second call
		// finds no entry, or a count another handle owns, and must not over-decrement --
		// TrackHandle guards the double call, this guards the removal race).
		while (_inPlay.TryGetValue(secretValue, out int count))
		{
			if (count <= 1)
			{
				if (_inPlay.TryRemove(new KeyValuePair<string, int>(secretValue, count)))
				{
					_needles.TryRemove(secretValue, out _);
					return;
				}
			}
			else if (_inPlay.TryUpdate(secretValue, count - 1, count))
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
