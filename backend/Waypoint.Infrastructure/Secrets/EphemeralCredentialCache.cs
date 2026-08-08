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
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// See <see cref="IEphemeralCredentialCache"/> for the contract. Process-memory only --
/// deliberately no table backs this (ADR-0011: "personal credentials are not stored in
/// v1", not "stored somewhere less obvious"). A process restart, like a TTL expiry,
/// loses every pending entry; that is the intended fail-closed behavior for a resumed
/// job whose ephemeral secret only ever lived in memory.
/// </summary>
public sealed partial class EphemeralCredentialCache : IEphemeralCredentialCache
{
	/// <summary>
	/// How long an unclaimed entry survives before a sweep discards it. Generous next to
	/// a job's normal queue-to-claim latency (seconds), tight enough that a credential
	/// typed for a run nobody ever dispatches does not linger in memory for the life of
	/// the process.
	/// </summary>
	internal static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);

	private sealed record Entry(EphemeralCredential Credential, IDisposable RedactionHandle, DateTimeOffset ExpiresAt);

	private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
	private readonly ISecretTracker _tracker;
	private readonly string? _connectionString;
	private readonly ILogger<EphemeralCredentialCache> _logger;

	public EphemeralCredentialCache(ISecretTracker tracker, string? connectionString, ILogger<EphemeralCredentialCache> logger)
	{
		ArgumentNullException.ThrowIfNull(tracker);
		ArgumentNullException.ThrowIfNull(logger);
		_tracker = tracker;
		_connectionString = connectionString;
		_logger = logger;
	}

	public void Put(Guid jobId, Guid? runId, EphemeralCredential credential, string actor)
	{
		ArgumentNullException.ThrowIfNull(credential);
		ArgumentException.ThrowIfNullOrWhiteSpace(credential.Secret);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		SweepExpired();

		// Track BEFORE the entry is visible to any reader -- same "track before it can go
		// anywhere" discipline as CredentialSecretStore.DecryptAsync (control 1).
		IDisposable redaction = _tracker.Track(credential.Secret);
		Entry entry = new(credential, redaction, DateTimeOffset.UtcNow + EntryLifetime);
		if (!_entries.TryAdd(jobId, entry))
		{
			// A second Put for the same job id would leak the first entry's redaction
			// handle; fail loudly instead -- this should never happen since job ids are
			// freshly minted by fan-out.
			redaction.Dispose();
			throw new InvalidOperationException($"An ephemeral credential is already registered for job '{jobId}'.");
		}

		LogRegistered(jobId, runId, actor);
		_ = WriteAuditBestEffortAsync(jobId, runId, actor);
	}

	public EphemeralCredential? TryTake(Guid jobId)
	{
		SweepExpired();

		if (!_entries.TryRemove(jobId, out Entry? entry))
		{
			return null;
		}

		entry.RedactionHandle.Dispose();
		return entry.Credential;
	}

	private void SweepExpired()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		foreach (KeyValuePair<Guid, Entry> pair in _entries)
		{
			if (pair.Value.ExpiresAt <= now && _entries.TryRemove(pair.Key, out Entry? removed))
			{
				removed.RedactionHandle.Dispose();
				LogExpired(pair.Key);
			}
		}
	}

	/// <summary>
	/// Records intake as an audit row (<c>secret.ephemeral_registered</c>,
	/// <c>credential_id</c> NULL -- there is no stored credential to attribute to) on a
	/// best-effort basis: unlike <see cref="Waypoint.Core.Secrets.ICredentialSecretStore.DecryptAsync"/>'s
	/// fail-closed pairing with a real decrypt, failing to audit an ephemeral
	/// registration must not block the run -- the secret was supplied directly by the
	/// same caller the run itself is attributed to, so there is no autonomous-decrypt
	/// risk being compensated for here. A write failure is logged, not thrown.
	/// </summary>
	private async Task WriteAuditBestEffortAsync(Guid jobId, Guid? runId, string actor)
	{
		if (string.IsNullOrWhiteSpace(_connectionString))
		{
			return;
		}

		try
		{
			await using NpgsqlConnection connection = new(_connectionString);
			await connection.OpenAsync().ConfigureAwait(false);
			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO audit_log (event_type, actor, credential_id, job_id, run_id, detail)
				VALUES ($1, $2, NULL, $3, $4, '{}'::jsonb)
				""", connection);
			insert.Parameters.AddWithValue("secret.ephemeral_registered");
			insert.Parameters.AddWithValue(actor);
			insert.Parameters.AddWithValue(jobId);
			insert.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
			await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
		{
			LogAuditWriteFailed(jobId, exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Ephemeral credential registered for job {JobId} (run {RunId}) by {Actor}")]
	private partial void LogRegistered(Guid jobId, Guid? runId, string actor);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Ephemeral credential for job {JobId} expired unclaimed")]
	private partial void LogExpired(Guid jobId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write secret.ephemeral_registered audit row for job {JobId}")]
	private partial void LogAuditWriteFailed(Guid jobId, Exception exception);
}
