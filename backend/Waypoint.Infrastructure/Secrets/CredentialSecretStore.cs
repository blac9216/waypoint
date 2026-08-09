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
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>See <see cref="ICredentialSecretStore"/> for the contract; this is the Npgsql implementation over <c>credential_secrets</c> + <c>audit_log</c>.</summary>
public sealed partial class CredentialSecretStore : ICredentialSecretStore
{
	/// <summary>AAD context format shared with everything that seals for a credential: binds an envelope to its row.</summary>
	internal static string ContextFor(Guid credentialId) => $"credential:{credentialId:D}";

	private readonly string _connectionString;
	private readonly IEnvelopeCipher _cipher;
	private readonly ISecretTracker _tracker;
	private readonly ILogger<CredentialSecretStore> _logger;

	public CredentialSecretStore(string connectionString, IEnvelopeCipher cipher, ISecretTracker tracker, ILogger<CredentialSecretStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(cipher);
		ArgumentNullException.ThrowIfNull(tracker);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_cipher = cipher;
		_tracker = tracker;
		_logger = logger;
	}

	public async Task StoreAsync(Guid credentialId, byte[] secretValue, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		string masterKeyId = await StoreAsync(connection, transaction, credentialId, secretValue, actor, _cipher, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogSecretStored(credentialId, actor, masterKeyId);
	}

	/// <summary>
	/// Connection-scoped core of <see cref="StoreAsync(Guid,byte[],string,CancellationToken)"/>
	/// -- does not open a connection/transaction or commit; the caller owns both so
	/// this can be composed with the credential metadata insert (issue #188, via
	/// <see cref="Waypoint.Core.Secrets.ICredentialCreationCoordinator"/>). The "no
	/// audit, no secret" fail-closed guarantee (security.md goal 3) is unaffected by
	/// this composition: the audit INSERT and the ciphertext upsert still commit or
	/// roll back together, whichever transaction they end up sharing -- a store
	/// failure during create rolls back the (never-committed) audit row along with
	/// the (never-committed) metadata row, which is correct: nothing worth auditing
	/// happened.
	/// </summary>
	internal static async Task<string> StoreAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid credentialId, byte[] secretValue, string actor,
		IEnvelopeCipher cipher, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(secretValue);
		if (secretValue.Length == 0)
		{
			throw new ArgumentException("An empty secret cannot be stored.", nameof(secretValue));
		}

		SecretEnvelope envelope = cipher.Encrypt(secretValue, ContextFor(credentialId));

		// Rotation replaces the row in place (ADR-0005: one blob per credential,
		// write-only -- no version history to leak from).
		await using (NpgsqlCommand upsert = new(
			"""
			INSERT INTO credential_secrets (credential_id, ciphertext, data_key_wrapped, master_key_id, algorithm)
			VALUES ($1, $2, $3, $4, $5)
			ON CONFLICT (credential_id) DO UPDATE SET
				ciphertext = EXCLUDED.ciphertext,
				data_key_wrapped = EXCLUDED.data_key_wrapped,
				master_key_id = EXCLUDED.master_key_id,
				algorithm = EXCLUDED.algorithm
			""", connection, transaction))
		{
			upsert.Parameters.AddWithValue(credentialId);
			upsert.Parameters.AddWithValue(envelope.Ciphertext);
			upsert.Parameters.AddWithValue(envelope.WrappedDataKey);
			upsert.Parameters.AddWithValue(envelope.MasterKeyId);
			upsert.Parameters.AddWithValue(envelope.Algorithm);
			await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await WriteAuditAsync(connection, transaction, "secret.stored", actor, credentialId, null, null,
			System.Text.Json.JsonSerializer.Serialize(new { master_key_id = envelope.MasterKeyId }), cancellationToken).ConfigureAwait(false);

		return envelope.MasterKeyId;
	}

	public async Task<DecryptedSecret> DecryptAsync(Guid credentialId, string actor, Guid? jobId, Guid? runId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// The audit row and the ciphertext read share one transaction: if the audit
		// INSERT cannot commit, no value leaves this method (security.md goal 3 --
		// "detect use" is fail-closed, not best-effort).
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		SecretEnvelope envelope;
		await using (NpgsqlCommand select = new(
			"SELECT ciphertext, data_key_wrapped, master_key_id, algorithm FROM credential_secrets WHERE credential_id = $1",
			connection, transaction))
		{
			select.Parameters.AddWithValue(credentialId);
			await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				throw new CredentialSecretNotFoundException(credentialId);
			}

			envelope = new SecretEnvelope(
				(byte[])reader[0], (byte[])reader[1], reader.GetString(2), reader.GetString(3));
		}

		await WriteAuditAsync(connection, transaction, "secret.decrypted", actor, credentialId, jobId, runId,
			System.Text.Json.JsonSerializer.Serialize(new { master_key_id = envelope.MasterKeyId }), cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		byte[] plaintext = _cipher.Decrypt(envelope, ContextFor(credentialId));
		try
		{
			string value = Encoding.UTF8.GetString(plaintext);

			// Track BEFORE the value can go anywhere: the handle owns the redaction
			// window (control 1). If tracking throws (a sub-minimum-length secret),
			// nothing has been handed out yet.
			IDisposable redaction = _tracker.Track(value);
			LogSecretDecrypted(credentialId, actor, jobId);
			return new DecryptedSecret(value, redaction);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	public async Task<bool> DeleteAsync(Guid credentialId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		int deleted;
		await using (NpgsqlCommand delete = new(
			"DELETE FROM credential_secrets WHERE credential_id = $1", connection, transaction))
		{
			delete.Parameters.AddWithValue(credentialId);
			deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (deleted > 0)
		{
			await WriteAuditAsync(connection, transaction, "secret.deleted", actor, credentialId, null, null, "{}", cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return deleted > 0;
	}

	private static async Task WriteAuditAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, string actor,
		Guid credentialId, Guid? jobId, Guid? runId, string detailJson, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO audit_log (event_type, actor, credential_id, job_id, run_id, detail)
			VALUES ($1, $2, $3, $4, $5, $6::jsonb)
			""", connection, transaction);
		insert.Parameters.AddWithValue(eventType);
		insert.Parameters.AddWithValue(actor);
		insert.Parameters.AddWithValue(credentialId);
		insert.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
		insert.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
		insert.Parameters.AddWithValue(detailJson);
		await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Secret stored for credential {CredentialId} by {Actor} under master key {MasterKeyId}")]
	private partial void LogSecretStored(Guid credentialId, string actor, string masterKeyId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Secret decrypted for credential {CredentialId} by {Actor} (job {JobId})")]
	private partial void LogSecretDecrypted(Guid credentialId, string actor, Guid? jobId);
}
