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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #434 against real Postgres: <see cref="RunSecretStore"/>'s store/decrypt round
/// trip, fail-closed audit in the decrypt transaction, wrong-master-key behavior,
/// not-found handling (never a stored-credential fallback), one-row-per-run rejection
/// of a second store, delete, and the expiry cleanup sweep. Mirrors
/// <see cref="CredentialSecretStoreTests"/>'s structure, adapted for the run-scoped
/// (not credential-scoped) key and the terminal/expiry lifecycle
/// <see cref="ICredentialSecretStore"/> does not have.
/// </summary>
[Collection("Postgres")]
public sealed class RunSecretStoreTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-run-secret-store-test").FullName;
	private InPlaySecretRedactor _redactor = null!;
	private RunSecretStore _store = null!;
	private Guid _runId;

	public RunSecretStoreTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_redactor = new InPlaySecretRedactor();
		_store = CreateStore(WriteKeyFile());
		_runId = await SeedRunAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	private string WriteKeyFile()
	{
		string path = Path.Combine(_keyDirectory, $"key-{Guid.NewGuid():N}");
		File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
		return path;
	}

	private RunSecretStore CreateStore(string keyPath)
	{
		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(keyPath));
		return new RunSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<RunSecretStore>.Instance);
	}

	[Fact]
	public async Task StoreDecryptRoundTrip_AuditsBothSides_AndRedactsWhileTheHandleLives()
	{
		const string secretValue = "invented-adhoc-token-e5f6";
		const string username = "adhoc-user@example.internal";
		await _store.StoreAsync(_runId, new RunSecretCredential(username, secretValue), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		Guid jobId = await SeedJobAsync(_runId);
		using (DecryptedRunSecret handle = (await _store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None))!)
		{
			Assert.Equal(username, handle.Username);
			Assert.Equal(secretValue, handle.Secret);

			// Control 1: holding the handle IS being redacted.
			Assert.Equal("[REDACTED]", _redactor.Redact(secretValue));
		}

		// Disposing the handle ends the in-play window.
		Assert.Equal(secretValue, _redactor.Redact(secretValue));

		Assert.Equal(1, await CountAuditAsync("secret.run_registered", _runId));
		Assert.Equal(1, await CountAuditAsync("secret.run_decrypted", _runId));
	}

	/// <summary>
	/// Unlike the predecessor single-shot in-memory cache (and unlike
	/// <see cref="CredentialSecretStoreTests"/>'s DELETE-based tests), decrypt is not
	/// single-shot -- a second decrypt while the row still exists succeeds again. This
	/// is what makes retry/lease-recovery work while a run is non-terminal (issue #434
	/// AC).
	/// </summary>
	[Fact]
	public async Task Decrypt_IsNotSingleShot_ASecondDecryptStillSucceeds()
	{
		const string secretValue = "invented-repeatable-canary";
		await _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", secretValue), "tester", TimeSpan.FromHours(1), CancellationToken.None);
		Guid jobId = await SeedJobAsync(_runId);

		using (DecryptedRunSecret? first = await _store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None))
		{
			Assert.Equal(secretValue, first!.Secret);
		}

		using DecryptedRunSecret? second = await _store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None);
		Assert.NotNull(second);
		Assert.Equal(secretValue, second!.Secret);
		Assert.Equal(2, await CountAuditAsync("secret.run_decrypted", _runId));
	}

	[Fact]
	public async Task ASecondStoreForTheSameRun_Throws()
	{
		await _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "first-invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "second-invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None));

		// The rejected second write left the first value intact -- one row, original value.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(_runId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

		Guid jobId = await SeedJobAsync(_runId);
		using DecryptedRunSecret? handle = await _store.DecryptAsync(_runId, jobId, "tester", CancellationToken.None);
		Assert.Equal("first-invented", handle!.Secret);
	}

	[Fact]
	public async Task AWrongMasterKey_FailsClosed_WithTheOperatorError_AndStillAudits()
	{
		await _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "invented-value"), "tester", TimeSpan.FromHours(1), CancellationToken.None);
		Guid jobId = await SeedJobAsync(_runId);

		RunSecretStore otherStore = CreateStore(WriteKeyFile());
		await Assert.ThrowsAsync<MasterKeyUnavailableException>(
			() => otherStore.DecryptAsync(_runId, jobId, "tester", CancellationToken.None));

		// The decrypt attempt was audited even though it failed: the audit commits
		// with the ciphertext read, before the crypto can succeed or fail.
		Assert.Equal(1, await CountAuditAsync("secret.run_decrypted", _runId));
	}

	[Fact]
	public async Task NoStoredSecret_ReturnsNull_NeverThrowsOrFallsBack()
	{
		Guid jobId = await SeedJobAsync(_runId);
		DecryptedRunSecret? result = await _store.DecryptAsync(_runId, jobId, "tester", CancellationToken.None);
		Assert.Null(result);
		Assert.Equal(0, await CountAuditAsync("secret.run_decrypted", _runId));
	}

	[Fact]
	public async Task Delete_RemovesTheSecret_AndAuditsOnlyWhenSomethingExisted()
	{
		await _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		Assert.True(await _store.DeleteAsync(_runId, "tester", CancellationToken.None));
		Assert.False(await _store.DeleteAsync(_runId, "tester", CancellationToken.None));
		Assert.Equal(1, await CountAuditAsync("secret.run_deleted", _runId));

		Guid jobId = await SeedJobAsync(_runId);
		Assert.Null(await _store.DecryptAsync(_runId, jobId, "tester", CancellationToken.None));
	}

	[Fact]
	public async Task AnEmptyUsernameOrSecret_IsRejected()
	{
		await Assert.ThrowsAsync<ArgumentException>(
			() => _store.StoreAsync(_runId, new RunSecretCredential("", "value"), "tester", TimeSpan.FromHours(1), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(
			() => _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", ""), "tester", TimeSpan.FromHours(1), CancellationToken.None));
	}

	[Fact]
	public async Task ANonPositiveExpiry_IsRejected()
	{
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "value"), "tester", TimeSpan.Zero, CancellationToken.None));
	}

	/// <summary>
	/// The expiry cleanup sweep (issue #434 AC "expiry + cleanup sweep for abandoned
	/// runs"): a row whose expires_at is in the past is deleted and audited as
	/// secret.run_expired; a row not yet expired survives untouched.
	/// </summary>
	[Fact]
	public async Task DeleteExpiredAsync_RemovesOnlyPastExpiry_AndAuditsEachAsExpired()
	{
		Guid expiredRunId = await SeedRunAsync();
		Guid freshRunId = await SeedRunAsync();

		// StoreAsync always computes expires_at as now() + expiresIn with a positive
		// span, so seed the already-expired row directly to simulate an abandoned run
		// whose expiry window has elapsed.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
				VALUES ($1, 'user@example.internal', E'\\x00', E'\\x00', 'test-key', 'AES-256-GCM', now() - interval '1 minute')
				""", connection);
			insert.Parameters.AddWithValue(expiredRunId);
			await insert.ExecuteNonQueryAsync();
		}

		await _store.StoreAsync(freshRunId, new RunSecretCredential("user@example.internal", "still-fresh"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		int deleted = await _store.DeleteExpiredAsync(CancellationToken.None);

		Assert.Equal(1, deleted);
		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();
		await using (NpgsqlCommand expiredCount = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", verify))
		{
			expiredCount.Parameters.AddWithValue(expiredRunId);
			Assert.Equal(0L, (long)(await expiredCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand freshCount = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", verify))
		{
			freshCount.Parameters.AddWithValue(freshRunId);
			Assert.Equal(1L, (long)(await freshCount.ExecuteScalarAsync())!);
		}

		Assert.Equal(1, await CountAuditAsync("secret.run_expired", expiredRunId));
		Assert.Equal(0, await CountAuditAsync("secret.run_expired", freshRunId));
	}

	[Fact]
	public async Task DeleteExpiredAsync_NothingExpired_ReturnsZero_NoAudit()
	{
		await _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		int deleted = await _store.DeleteExpiredAsync(CancellationToken.None);

		Assert.Equal(0, deleted);
		Assert.Equal(0, await CountAuditAsync("secret.run_expired", _runId));
	}

	/// <summary>
	/// <see cref="RunSecretCleanupHostedService"/> wires <see cref="RunSecretStore.DeleteExpiredAsync"/>
	/// behind its periodic sweep -- this exercises the service's own sweep pass (not
	/// just the store method underneath it), mirroring how
	/// <c>LeaseRecoveryHostedServiceTests</c> exercises <c>LeaseRecoveryHostedService.SweepAsync</c>
	/// rather than only <c>JobQueueRepository.RecoverExpiredLeasesAsync</c>.
	/// </summary>
	[Fact]
	public async Task CleanupHostedService_SweepOnceAsync_DeletesExpiredRows()
	{
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
				VALUES ($1, 'user@example.internal', E'\\x00', E'\\x00', 'test-key', 'AES-256-GCM', now() - interval '1 minute')
				""", connection);
			insert.Parameters.AddWithValue(_runId);
			await insert.ExecuteNonQueryAsync();
		}

		RunSecretCleanupHostedService service = new(
			_store,
			Microsoft.Extensions.Options.Options.Create(new RunSecretOptions()),
			NullLogger<RunSecretCleanupHostedService>.Instance);

		await service.SweepOnceAsync(CancellationToken.None);

		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", verify);
		count.Parameters.AddWithValue(_runId);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}', 'running') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, has_run_secret) VALUES ($1, 'scan', 1, 'queued', true) RETURNING id", connection);
		insert.Parameters.AddWithValue(runId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<long> CountAuditAsync(string eventType, Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new(
			"SELECT count(*) FROM audit_log WHERE event_type = $1 AND run_id = $2", connection);
		count.Parameters.AddWithValue(eventType);
		count.Parameters.AddWithValue(runId);
		return (long)(await count.ExecuteScalarAsync())!;
	}
}
