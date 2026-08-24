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
/// 0023; re-keyed per-target/per-purpose by issue #586, migration 0045). Structurally
/// mirrors <see cref="CredentialSecretStore"/> -- same AES-256-GCM envelope, same
/// fail-closed "audit commits in the transaction that reads the ciphertext" discipline,
/// same in-play redaction handoff -- but keyed by <c>(run_id, target_id, purpose)</c>
/// instead of <c>credential_id</c>, with no rotation (one write per key, at run
/// creation) and an <c>expires_at</c> bound <see cref="DeleteExpiredAsync"/> sweeps.
/// <c>expires_at</c> is a sliding window (issue #469): every successful
/// <see cref="DecryptAsync(Guid,RunSecretKey,Guid,string,CancellationToken)"/> pushes it
/// back out to now() + <see cref="RunSecretOptions.Expiry"/> for THAT ROW ONLY, so a run
/// stays covered for as long as something keeps decrypting a given (target, purpose)
/// credential, and only stops sliding -- and eventually gets swept -- once decrypt
/// activity for that specific row actually stops.
/// </summary>
public sealed partial class RunSecretStore : IRunSecretStore
{
	/// <summary>
	/// AAD context format: binds an envelope to its run AND its key (issue #586) --
	/// legacy rows (<see cref="RunSecretKey.Legacy"/>) keep the exact pre-#586 context
	/// string so an in-flight legacy row written before this upgrade still decrypts
	/// after it (the ciphertext's AAD was fixed at encrypt time and never changes).
	/// Mirrors how <see cref="CredentialSecretStore.ContextFor"/> binds to a credential.
	/// </summary>
	internal static string ContextFor(Guid runId, RunSecretKey key) => key.IsLegacy
		? $"run-secret:{runId:D}"
		: $"run-secret:{runId:D}:{key.TargetId:D}:{key.Purpose}";

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

	// Explicit back-compat overloads: IRunSecretStore's default-interface-method
	// shortcuts for RunSecretKey.Legacy only resolve for callers holding an
	// IRunSecretStore reference -- C# does not consider interface default methods
	// when resolving a call through the concrete class type. RunSecretStoreTests (and
	// every other caller that new()s this class directly rather than going through DI)
	// needs these declared here too, so both call shapes reach the same
	// RunSecretKey.Legacy-keyed behavior.
	public Task StoreAsync(Guid runId, RunSecretCredential credential, string actor, TimeSpan expiresIn, CancellationToken cancellationToken)
		=> StoreAsync(runId, RunSecretKey.Legacy, credential, actor, expiresIn, cancellationToken);

	public Task<DecryptedRunSecret?> DecryptAsync(Guid runId, Guid jobId, string actor, CancellationToken cancellationToken)
		=> DecryptAsync(runId, RunSecretKey.Legacy, jobId, actor, cancellationToken);

	public async Task StoreAsync(Guid runId, RunSecretKey key, RunSecretCredential credential, string actor, TimeSpan expiresIn, CancellationToken cancellationToken)
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
			envelope = _cipher.Encrypt(secretBytes, ContextFor(runId, key));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(secretBytes);
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// No ON CONFLICT: a second Store for the same (run, key) would mean either a bug
		// (fan-out calling this twice for the same target/purpose instead of once) or,
		// worse, silently replacing an in-flight job's already-resolved secret out from
		// under it -- fail loudly instead. A DIFFERENT key on the same run is a distinct
		// row (migration 0045's (run_id, target_id, purpose) uniqueness) and always
		// succeeds -- that is the whole point of issue #586's re-keying: multiple ad hoc
		// credentials coexist on one run without colliding.
		await using (NpgsqlCommand insert = new(
			"""
			INSERT INTO run_secrets (run_id, target_id, purpose, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, now() + $9)
			""", connection, transaction))
		{
			insert.Parameters.AddWithValue(runId);
			insert.Parameters.AddWithValue((object?)key.TargetId ?? DBNull.Value);
			insert.Parameters.AddWithValue(key.Purpose ?? "_legacy");
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
				throw new InvalidOperationException(
					key.IsLegacy
						? $"A run secret is already registered for run '{runId}'."
						: $"A run secret is already registered for run '{runId}', target '{key.TargetId}', purpose '{key.Purpose}'.",
					exception);
			}
		}

		await WriteAuditAsync(connection, transaction, "secret.run_registered", actor, runId, null, key,
			System.Text.Json.JsonSerializer.Serialize(new { master_key_id = envelope.MasterKeyId }), cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogSecretStored(runId, actor, envelope.MasterKeyId);
	}

	public async Task<DecryptedRunSecret?> DecryptAsync(Guid runId, RunSecretKey key, Guid jobId, string actor, CancellationToken cancellationToken)
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
		//
		// Least-privilege (issue #586 AC): scoped to run_id AND target_id/purpose, so a
		// decrypt for one (target, purpose) never reads -- or slides the expiry of -- a
		// sibling row on the same run. `target_id IS NOT DISTINCT FROM $2` (rather than
		// `= $2`) is required because the legacy key's target_id is NULL, and plain `=`
		// never matches NULL.
		bool found;
		SecretEnvelope envelope = null!;
		string storedUsername = null!;
		await using (NpgsqlCommand select = new(
			"""
			SELECT username, ciphertext, data_key_wrapped, master_key_id, algorithm
			FROM run_secrets
			WHERE run_id = $1 AND target_id IS NOT DISTINCT FROM $2 AND purpose = $3
			""",
			connection, transaction))
		{
			select.Parameters.AddWithValue(runId);
			select.Parameters.AddWithValue((object?)key.TargetId ?? DBNull.Value);
			select.Parameters.AddWithValue(key.Purpose ?? "_legacy");
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
		// Scoped to exactly the decrypted row (same predicate as the SELECT above): a
		// decrypt of one (target, purpose) must not resurrect a sibling row's expiry
		// that genuinely stopped being used. This is what lets a long multi-stage run
		// survive past the 8h default without losing its secret mid-flight: every retry,
		// stage requeue, or lease-recovery decrypt slides that row's window again, so
		// only a row that stops decrypting entirely (i.e. genuinely abandoned) ever lets
		// its window run out.
		await using (NpgsqlCommand slide = new(
			"""
			UPDATE run_secrets SET expires_at = now() + $4
			WHERE run_id = $1 AND target_id IS NOT DISTINCT FROM $2 AND purpose = $3
			""", connection, transaction))
		{
			slide.Parameters.AddWithValue(runId);
			slide.Parameters.AddWithValue((object?)key.TargetId ?? DBNull.Value);
			slide.Parameters.AddWithValue(key.Purpose ?? "_legacy");
			slide.Parameters.AddWithValue(_options.Value.Expiry);
			await slide.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await WriteAuditAsync(connection, transaction, "secret.run_decrypted", actor, runId, jobId, key,
			System.Text.Json.JsonSerializer.Serialize(new { master_key_id = envelope.MasterKeyId }), cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		byte[] plaintext = _cipher.Decrypt(envelope, ContextFor(runId, key));
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

	/// <summary>
	/// Deletes EVERY row for <paramref name="runId"/> regardless of key -- the legacy
	/// row, any number of per-target/per-purpose rows, or both at once (a run mixing the
	/// wire-compat legacy shape with #586 overrides is not a supported combination
	/// RunCreationService produces, but this delete is correct for it anyway: "no job
	/// left that could need any of this run's ad hoc credentials" holds regardless of
	/// how many rows/keys accumulated). One audit row per deleted row, each carrying
	/// that row's own target/purpose attribution -- this is what makes the unconditional
	/// per-terminal-completion call site (<c>JobQueueRepository.DeleteRunSecretIfPresentAsync</c>,
	/// issue #642's lesson) provably cover both the pre-#586 and post-#586 shapes without
	/// the caller needing to know which one a given run has.
	/// </summary>
	public async Task<bool> DeleteAsync(Guid runId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		List<(Guid? TargetId, string Purpose)> deletedKeys = [];
		await using (NpgsqlCommand delete = new(
			"DELETE FROM run_secrets WHERE run_id = $1 RETURNING target_id, purpose", connection, transaction))
		{
			delete.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				deletedKeys.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1)));
			}
		}

		foreach ((Guid? targetId, string purpose) in deletedKeys)
		{
			RunSecretKey key = targetId is { } t ? RunSecretKey.For(t, purpose) : RunSecretKey.Legacy;
			await WriteAuditAsync(connection, transaction, "secret.run_deleted", actor, runId, null, key, "{}", cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		if (deletedKeys.Count > 0)
		{
			LogSecretDeleted(runId, actor);
		}

		return deletedKeys.Count > 0;
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

		// Per-ROW, not per-run (issue #586): a run with multiple (target, purpose) rows
		// can have some expire independently of others (each row's expires_at slides
		// only when THAT row is decrypted) -- id is the row's own surrogate key
		// (migration 0045), the unit FOR UPDATE SKIP LOCKED and the subsequent DELETE
		// both operate on.
		List<(Guid Id, Guid RunId, Guid? TargetId, string Purpose)> expiredRows = [];
		await using (NpgsqlCommand select = new(
			"SELECT id, run_id, target_id, purpose FROM run_secrets WHERE expires_at <= now() FOR UPDATE SKIP LOCKED",
			connection, transaction))
		{
			await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				expiredRows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3)));
			}
		}

		if (expiredRows.Count == 0)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return 0;
		}

		int deleted;
		await using (NpgsqlCommand delete = new(
			"DELETE FROM run_secrets WHERE id = ANY($1)", connection, transaction))
		{
			delete.Parameters.AddWithValue(expiredRows.Select(r => r.Id).ToArray());
			deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		foreach ((Guid _, Guid runId, Guid? targetId, string purpose) in expiredRows)
		{
			RunSecretKey key = targetId is { } t ? RunSecretKey.For(t, purpose) : RunSecretKey.Legacy;
			await WriteAuditAsync(connection, transaction, "secret.run_expired", "system:run-secret-cleanup", runId, null, key, "{}", cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		if (deleted > 0)
		{
			LogExpiredSwept(deleted);
		}

		return deleted;
	}

	/// <summary>
	/// Writes one <c>audit_log</c> row for a run-secret lifecycle event, carrying
	/// <paramref name="key"/>'s target/purpose attribution in <c>detail</c> (issue #586
	/// AC "every decrypt is ... durably attributed") alongside the pre-existing
	/// <paramref name="jobId"/>/<paramref name="runId"/> columns -- <c>audit_log</c> has
	/// no dedicated target/purpose column, so this rides the existing JSONB
	/// <c>detail</c> the same way <c>master_key_id</c> already does, merged into
	/// <paramref name="detailJson"/> rather than a second INSERT.
	/// </summary>
	private static async Task WriteAuditAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, string actor,
		Guid runId, Guid? jobId, RunSecretKey key, string detailJson, CancellationToken cancellationToken)
	{
		using System.Text.Json.JsonDocument parsedDetail = System.Text.Json.JsonDocument.Parse(detailJson);
		Dictionary<string, object?> merged = [];
		foreach (System.Text.Json.JsonProperty property in parsedDetail.RootElement.EnumerateObject())
		{
			merged[property.Name] = property.Value.Clone();
		}

		merged["target_id"] = key.TargetId;
		merged["purpose"] = key.Purpose ?? "_legacy";
		string mergedJson = System.Text.Json.JsonSerializer.Serialize(merged);

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO audit_log (event_type, actor, credential_id, job_id, run_id, detail)
			VALUES ($1, $2, NULL, $3, $4, $5::jsonb)
			""", connection, transaction);
		insert.Parameters.AddWithValue(eventType);
		insert.Parameters.AddWithValue(actor);
		insert.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(mergedJson);
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
