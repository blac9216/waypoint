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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// See <see cref="IRunSecretStore"/> for the contract; this is the Npgsql
/// implementation over <c>run_secrets</c> + <c>audit_log</c> (issue #434, migration
/// 0023). Structurally mirrors <see cref="CredentialSecretStore"/> -- same
/// AES-256-GCM envelope, same fail-closed "audit commits in the transaction that reads
/// the ciphertext" discipline, same in-play redaction handoff -- but keyed by
/// <c>run_id</c> instead of <c>credential_id</c>, with no rotation (one write, at run
/// creation) and an <c>expires_at</c> bound <see cref="DeleteExpiredAsync"/> sweeps.
/// <c>expires_at</c> is a sliding window (issue #469): every successful
/// <see cref="DecryptAsync"/> pushes it back out to now() + <see cref="RunSecretOptions.Expiry"/>,
/// so a run stays covered for as long as something keeps decrypting its secret, and only
/// stops sliding -- and eventually gets swept -- once decrypt activity actually stops.
/// </summary>
public sealed partial class RunSecretStore : IRunSecretStore
{
	/// <summary>AAD context format: binds an envelope to its run, exactly as <see cref="CredentialSecretStore.ContextFor"/> binds to a credential.</summary>
	internal static string ContextFor(Guid runId) => $"run-secret:{runId:D}";

	private readonly string _connectionString;
	private readonly IEnvelopeCipher _cipher;
	private readonly ISecretTracker _tracker;
	private readonly IOptions<RunSecretOptions> _options;
	private readonly ILogger<RunSecretStore> _logger;

	public RunSecretStore(string connectionString, IEnvelopeCipher cipher, ISecretTracker tracker, IOptions<RunSecretOptions> options, ILogger<RunSecretStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(cipher);
		ArgumentNullException.ThrowIfNull(tracker);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_cipher = cipher;
		_tracker = tracker;
		_options = options;
		_logger = logger;
	}

	public async Task StoreAsync(Guid runId, RunSecretCredential credential, string actor, TimeSpan expiresIn, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(credential);
		ArgumentException.ThrowIfNullOrWhiteSpace(credential.Username);
		ArgumentException.ThrowIfNullOrWhiteSpace(credential.Secret);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);
		if (expiresIn <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(expiresIn), expiresIn, "expiresIn must be positive.");
		}

		byte[] secretBytes = Encoding.UTF8.GetBytes(credential.Secret);
		SecretEnvelope envelope;
		try
		{
			envelope = _cipher.Encrypt(secretBytes, ContextFor(runId));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretBytes);
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// No ON CONFLICT: a second Store for a run id would mean either a bug (fan-out
		// calling this per target instead of once per run) or, worse, silently
		// replacing an in-flight run's secret out from under jobs that already resolved
		// the first one -- fail loudly instead.
		await using (NpgsqlCommand insert = new(
			"""
			INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
			VALUES ($1, $2, $3, $4, $5, $6, now() + $7)
			""", connection, transaction))
		{
			insert.Parameters.AddWithValue(runId);
			insert.Parameters.AddWithValue(credential.Username);
			insert.Parameters.AddWithValue(envelope.Ciphertext);
			insert.Parameters.AddWithValue(envelope.WrappedDataKey);
			insert.Parameters.AddWithValue(envelope.MasterKeyId);
			insert.Parameters.AddWithValue(envelope.Algorithm);
			insert.Parameters.AddWithValue(expiresIn);
			try
			{
				await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
			{
				throw new InvalidOperationException($"A run secret is already registered for run '{runId}'.", exception);
			}
		}

		await WriteAuditAsync(connection, transaction, "secret.run_registered", actor, runId, null,
			System.Text.Json.JsonSerializer.Serialize(new { master_key_id = envelope.MasterKeyId }), cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogSecretStored(runId, actor, envelope.MasterKeyId);
	}

	public async Task<DecryptedRunSecret?> DecryptAsync(Guid runId, Guid jobId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Same fail-closed pairing as CredentialSecretStore.DecryptAsync: the audit row
		// and the ciphertext read share one transaction, so a value only ever leaves
		// this method if the audit committed.
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// The reader/command below are fully disposed by the time the `await using`
		// block closes -- rolling back while a reader is still open throws
		// NpgsqlOperationInProgressException (the same trap JobQueueRepository.AdvanceStateAsync's
		// comment documents), so the no-match rollback happens strictly after this
		// scope, never from inside it.
		bool found;
		SecretEnvelope envelope = null!;
		string storedUsername = null!;
		await using (NpgsqlCommand select = new(
			"SELECT username, ciphertext, data_key_wrapped, master_key_id, algorithm FROM run_secrets WHERE run_id = $1",
			connection, transaction))
		{
			select.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			if (found)
			{
				storedUsername = reader.GetString(0);
				envelope = new SecretEnvelope((byte[])reader[1], (byte[])reader[2], reader.GetString(3), reader.GetString(4));
			}
		}

		if (!found)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return null;
		}

		// Sliding window (issue #469): a successful decrypt is activity, so push
		// expires_at back out to now() + Expiry in the SAME transaction as the audit
		// write below -- one commit, one definition of "this row is still in use".
		// This is what lets a long multi-stage run survive past the 8h default without
		// losing its secret mid-flight: every retry, stage requeue, or lease-recovery
		// decrypt slides the window again, so only a run that stops decrypting entirely
		// (i.e. genuinely abandoned) ever lets the window run out.
		await using (NpgsqlCommand slide = new(
			"UPDATE run_secrets SET expires_at = now() + $2 WHERE run_id = $1", connection, transaction))
		{
			slide.Parameters.AddWithValue(runId);
			slide.Parameters.AddWithValue(_options.Value.Expiry);
			await slide.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await WriteAuditAsync(connection, transaction, "secret.run_decrypted", actor, runId, jobId,
			System.Text.Json.JsonSerializer.Serialize(new { master_key_id = envelope.MasterKeyId }), cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		byte[] plaintext = _cipher.Decrypt(envelope, ContextFor(runId));
		try
		{
			string value = Encoding.UTF8.GetString(plaintext);

			// Track BEFORE the value can go anywhere (control 1), same discipline as
			// CredentialSecretStore. The username is not secret material (it never goes
			// through the cipher), so only the decrypted secret value is tracked.
			IDisposable redaction = _tracker.Track(value);
			LogSecretDecrypted(runId, actor, jobId);
			return new DecryptedRunSecret(storedUsername, value, redaction);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	public async Task<bool> DeleteAsync(Guid runId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		int deleted;
		await using (NpgsqlCommand delete = new("DELETE FROM run_secrets WHERE run_id = $1", connection, transaction))
		{
			delete.Parameters.AddWithValue(runId);
			deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (deleted > 0)
		{
			await WriteAuditAsync(connection, transaction, "secret.run_deleted", actor, runId, null, "{}", cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		if (deleted > 0)
		{
			LogSecretDeleted(runId, actor);
		}

		return deleted > 0;
	}

	/// <summary>
	/// The abandoned-run cleanup sweep (issue #434 AC "expiry + cleanup sweep for
	/// abandoned runs"). Deletes rows whose <c>expires_at</c> has passed -- that gate
	/// is the ONLY cleanup trigger; there is no separate run-state check (issue #452:
	/// this summary previously implied one, which the query has never implemented). A
	/// run that reaches a contract-terminal state (completed, completed_with_failures,
	/// aborted) already had its row deleted synchronously by
	/// <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository"/>'s run-completion
	/// paths, so any row still present once <c>expires_at</c> passes belongs to a run
	/// that is abandoned in one of two senses: genuinely stuck (pending/running with no
	/// worker ever making progress -- a crashed API before dispatch, or a runner that
	/// claimed the job and then died before ANY terminal write, which lease recovery
	/// will eventually resolve) or simply no longer decrypting its secret. The window is
	/// sliding (issue #469: <see cref="DecryptAsync"/> pushes <c>expires_at</c> back out
	/// to now() + <see cref="RunSecretOptions.Expiry"/> on every successful decrypt), so
	/// a still-active run's row keeps outrunning this sweep on its own; only a row whose
	/// run has stopped decrypting for a full <see cref="RunSecretOptions.Expiry"/> window
	/// ever reaches this query.
	///
	/// The race this guards against: a sweep pass and a job's terminal write both
	/// touching the same row concurrently is harmless either way (DELETE is
	/// idempotent -- the loser affects zero rows), but deleting out from under a job
	/// that has not yet reached its FIRST claim (still 'queued'/'pending', within its
	/// expiry window) would be a correctness bug, not just wasted audit noise -- hence
	/// the <c>expires_at &lt;= now()</c> gate is the only cleanup trigger; there is no
	/// separate "run looks stuck" heuristic here that could fire early.
	/// </summary>
	public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		List<Guid> expiredRunIds = [];
		await using (NpgsqlCommand select = new(
			"SELECT run_id FROM run_secrets WHERE expires_at <= now() FOR UPDATE SKIP LOCKED",
			connection, transaction))
		{
			await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				expiredRunIds.Add(reader.GetGuid(0));
			}
		}

		if (expiredRunIds.Count == 0)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return 0;
		}

		int deleted;
		await using (NpgsqlCommand delete = new(
			"DELETE FROM run_secrets WHERE run_id = ANY($1)", connection, transaction))
		{
			delete.Parameters.AddWithValue(expiredRunIds.ToArray());
			deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		foreach (Guid runId in expiredRunIds)
		{
			await WriteAuditAsync(connection, transaction, "secret.run_expired", "system:run-secret-cleanup", runId, null, "{}", cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		if (deleted > 0)
		{
			LogExpiredSwept(deleted);
		}

		return deleted;
	}

	private static async Task WriteAuditAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, string actor,
		Guid runId, Guid? jobId, string detailJson, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO audit_log (event_type, actor, credential_id, job_id, run_id, detail)
			VALUES ($1, $2, NULL, $3, $4, $5::jsonb)
			""", connection, transaction);
		insert.Parameters.AddWithValue(eventType);
		insert.Parameters.AddWithValue(actor);
		insert.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(detailJson);
		await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Run secret stored for run {RunId} by {Actor} under master key {MasterKeyId}")]
	private partial void LogSecretStored(Guid runId, string actor, string masterKeyId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run secret decrypted for run {RunId} by {Actor} (job {JobId})")]
	private partial void LogSecretDecrypted(Guid runId, string actor, Guid jobId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run secret deleted for run {RunId} by {Actor}")]
	private partial void LogSecretDeleted(Guid runId, string actor);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run secret cleanup swept {Count} expired row(s)")]
	private partial void LogExpiredSwept(int count);
}
