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
using System.Text.Json;
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

	private RunSecretStore CreateStore(string keyPath) => CreateStore(keyPath, new RunSecretOptions());

	private RunSecretStore CreateStore(string keyPath, RunSecretOptions options)
	{
		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(keyPath));
		return new RunSecretStore(_fixture.ConnectionString, cipher, _redactor, Microsoft.Extensions.Options.Options.Create(options), NullLogger<RunSecretStore>.Instance);
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
	/// Issue #586: two DIFFERENT (target, purpose) keys on the SAME run coexist as two
	/// separate rows, each independently round-tripping its own username/secret --
	/// proves the re-keying's headline claim ("multiple ad hoc credentials coexist
	/// without cross-target/purpose access") at the store level, before any HTTP layer
	/// is involved.
	/// </summary>
	[Fact]
	public async Task StoreDecryptRoundTrip_TwoKeysOnSameRun_CoexistWithoutCrossAccess()
	{
		Guid targetA = await SeedTargetAsync();
		Guid targetB = await SeedTargetAsync();
		RunSecretKey keyA = RunSecretKey.For(targetA, CredentialPurposes.VSphereApi);
		RunSecretKey keyB = RunSecretKey.For(targetB, CredentialPurposes.VSphereApi);

		await _store.StoreAsync(_runId, keyA, new RunSecretCredential("user-a@example.internal", "invented-value-a"), "tester", TimeSpan.FromHours(1), CancellationToken.None);
		await _store.StoreAsync(_runId, keyB, new RunSecretCredential("user-b@example.internal", "invented-value-b"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		Guid jobId = await SeedJobAsync(_runId);
		using (DecryptedRunSecret? handleA = await _store.DecryptAsync(_runId, keyA, jobId, "engine", CancellationToken.None))
		{
			Assert.NotNull(handleA);
			Assert.Equal("user-a@example.internal", handleA!.Username);
			Assert.Equal("invented-value-a", handleA.Secret);
		}

		using (DecryptedRunSecret? handleB = await _store.DecryptAsync(_runId, keyB, jobId, "engine", CancellationToken.None))
		{
			Assert.NotNull(handleB);
			Assert.Equal("user-b@example.internal", handleB!.Username);
			Assert.Equal("invented-value-b", handleB.Secret);
		}

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(_runId);
		Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #586: a second Store for the SAME (run, key) throws, exactly like the
	/// legacy shape's one-row-per-run rejection -- but a DIFFERENT key on the same run
	/// is unaffected (proven by the coexistence test above); this test isolates the
	/// "same key twice" failure mode specifically.
	/// </summary>
	[Fact]
	public async Task Store_SameRunAndKeyTwice_Throws_ButDoesNotAffectOtherKeys()
	{
		Guid targetId = await SeedTargetAsync();
		RunSecretKey key = RunSecretKey.For(targetId, CredentialPurposes.VSphereApi);
		await _store.StoreAsync(_runId, key, new RunSecretCredential("user@example.internal", "first-invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _store.StoreAsync(_runId, key, new RunSecretCredential("user@example.internal", "second-invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None));

		// A different purpose on the SAME target is a different key -- unaffected.
		RunSecretKey otherPurposeKey = RunSecretKey.For(targetId, CredentialPurposes.VcsaSsh);
		await _store.StoreAsync(_runId, otherPurposeKey, new RunSecretCredential("user2@example.internal", "third-invented"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		Guid jobId = await SeedJobAsync(_runId);
		using DecryptedRunSecret? handle = await _store.DecryptAsync(_runId, key, jobId, "tester", CancellationToken.None);
		Assert.Equal("first-invented", handle!.Secret);
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

	/// <summary>
	/// Issue #469: a successful decrypt slides <c>expires_at</c> back out to
	/// now() + <see cref="RunSecretOptions.Expiry"/>, in the same transaction as the
	/// decrypt audit. Seeds the row already close to expiry (StoreAsync's own minimum
	/// positive TimeSpan check rules out zero/negative, so seed directly) and asserts
	/// the post-decrypt value moved forward relative to the pre-decrypt value.
	/// </summary>
	[Fact]
	public async Task Decrypt_SlidesExpiresAt_ForwardByTheConfiguredExpiry()
	{
		RunSecretOptions options = new() { Expiry = TimeSpan.FromHours(2) };
		RunSecretStore store = CreateStore(WriteKeyFile(), options);
		await store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "invented-sliding-value"), "tester", TimeSpan.FromMinutes(1), CancellationToken.None);

		DateTime beforeDecrypt = await GetExpiresAtAsync(_runId);

		Guid jobId = await SeedJobAsync(_runId);
		using DecryptedRunSecret? handle = await store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None);
		Assert.NotNull(handle);

		DateTime afterDecrypt = await GetExpiresAtAsync(_runId);
		Assert.True(afterDecrypt > beforeDecrypt,
			$"Decrypt did not slide expires_at forward: before={beforeDecrypt:o}, after={afterDecrypt:o}.");

		// The slid value should land close to now + 2h (the configured Expiry), not
		// merely "later than the 1-minute StoreAsync window" -- proves the slide uses
		// RunSecretOptions.Expiry rather than re-applying the original expiresIn.
		TimeSpan untilExpiry = afterDecrypt - DateTime.UtcNow;
		Assert.True(untilExpiry > TimeSpan.FromMinutes(90) && untilExpiry < TimeSpan.FromHours(3),
			$"Slid expires_at was not ~2h out: {untilExpiry}.");
	}

	/// <summary>
	/// Issue #469: an abandoned run -- one that stops decrypting entirely -- gets no
	/// activity to slide its window, so it is still swept once its (last-set)
	/// expires_at passes. Sliding only helps a row that keeps generating decrypts.
	/// </summary>
	[Fact]
	public async Task AnAbandonedRun_WithNoFurtherActivity_IsStillSweptAfterItsWindowPasses()
	{
		Guid jobId = await SeedJobAsync(_runId);
		await _store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "invented-abandoned"), "tester", TimeSpan.FromMinutes(1), CancellationToken.None);

		// One decrypt (e.g. the initial claim), then nothing else ever touches this run.
		using (DecryptedRunSecret? handle = await _store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None))
		{
			Assert.NotNull(handle);
		}

		// Simulate the window having elapsed with no further activity by backdating
		// expires_at directly, rather than sleeping in a test.
		await SetExpiresAtAsync(_runId, DateTime.UtcNow.AddMinutes(-1));

		int deleted = await _store.DeleteExpiredAsync(CancellationToken.None);

		Assert.Equal(1, deleted);
		Assert.Equal(0L, await CountRowsAsync(_runId));
		Assert.Equal(1, await CountAuditAsync("secret.run_expired", _runId));
	}

	/// <summary>
	/// Issue #469: a run with periodic decrypt activity (retries / stage requeues /
	/// lease-recovery decrypts, standing in for real multi-stage progress) survives
	/// past what would have been its ORIGINAL fixed-at-creation expiry, because each
	/// decrypt slides the window back out.
	/// </summary>
	[Fact]
	public async Task ARunWithPeriodicActivity_SurvivesPastItsOriginalCreationWindow()
	{
		RunSecretOptions options = new() { Expiry = TimeSpan.FromMinutes(10) };
		RunSecretStore store = CreateStore(WriteKeyFile(), options);
		await store.StoreAsync(_runId, new RunSecretCredential("user@example.internal", "invented-long-runner"), "tester", TimeSpan.FromMinutes(1), CancellationToken.None);
		Guid jobId = await SeedJobAsync(_runId);

		DateTime originalExpiry = await GetExpiresAtAsync(_runId);

		// Activity happens, then the row is artificially pushed to just past what the
		// ORIGINAL (1-minute) creation window would have been -- if expiry were still
		// fixed-at-creation, the row would already be gone. Decrypting again here
		// stands in for the next stage's activity sliding the window back out.
		await SetExpiresAtAsync(_runId, originalExpiry.AddSeconds(-1));
		using (DecryptedRunSecret? handle = await store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None))
		{
			Assert.NotNull(handle);
		}

		DateTime slidExpiry = await GetExpiresAtAsync(_runId);
		Assert.True(slidExpiry > originalExpiry,
			"A decrypt after the original creation window should have slid expires_at further out, not left it to lapse.");

		// A sweep run "now" (well within the slid 10-minute window) must not collect it.
		int deleted = await store.DeleteExpiredAsync(CancellationToken.None);
		Assert.Equal(0, deleted);
		Assert.Equal(1L, await CountRowsAsync(_runId));
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

	/// <summary>
	/// Issue #586: expiry is a PER-ROW property, not per-run -- two per-target/per-purpose
	/// rows on the SAME run can expire independently. Sweeping deletes only the one whose
	/// window has passed, leaves the other (still fresh) row untouched, and audits each
	/// deleted row with its own target/purpose attribution.
	/// </summary>
	[Fact]
	public async Task DeleteExpiredAsync_PerTargetPurposeRows_ExpireIndependentlyOnTheSameRun()
	{
		Guid targetA = await SeedTargetAsync();
		Guid targetB = await SeedTargetAsync();
		RunSecretKey keyA = RunSecretKey.For(targetA, CredentialPurposes.VSphereApi);
		RunSecretKey keyB = RunSecretKey.For(targetB, CredentialPurposes.VSphereApi);

		await _store.StoreAsync(_runId, keyA, new RunSecretCredential("user-a@example.internal", "invented-expiring"), "tester", TimeSpan.FromMinutes(1), CancellationToken.None);
		await _store.StoreAsync(_runId, keyB, new RunSecretCredential("user-b@example.internal", "invented-fresh"), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		// Backdate only target A's row -- target B's stays within its 1-hour window.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand update = new(
				"UPDATE run_secrets SET expires_at = now() - interval '1 minute' WHERE run_id = $1 AND target_id = $2", connection);
			update.Parameters.AddWithValue(_runId);
			update.Parameters.AddWithValue(targetA);
			await update.ExecuteNonQueryAsync();
		}

		int deleted = await _store.DeleteExpiredAsync(CancellationToken.None);

		Assert.Equal(1, deleted);
		Guid jobId = await SeedJobAsync(_runId);
		Assert.Null(await _store.DecryptAsync(_runId, keyA, jobId, "tester", CancellationToken.None));
		using DecryptedRunSecret? stillFresh = await _store.DecryptAsync(_runId, keyB, jobId, "tester", CancellationToken.None);
		Assert.NotNull(stillFresh);
		Assert.Equal("invented-fresh", stillFresh!.Secret);
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

	/// <summary>Issue #586: seeds a target for a per-target/per-purpose RunSecretKey.</summary>
	private async Task<Guid> SeedTargetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid siteId;
		await using (NpgsqlCommand site = new("INSERT INTO sites (name) VALUES ($1) RETURNING id", connection))
		{
			site.Parameters.AddWithValue($"run-secret-store-site-{Guid.NewGuid():N}");
			siteId = (Guid)(await site.ExecuteScalarAsync())!;
		}

		await using NpgsqlCommand target = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, discovery_status)
			VALUES ($1, 'vsphere', $2, '{"host":"vcsa-01.example.internal"}'::jsonb, 'never_discovered')
			RETURNING id
			""", connection);
		target.Parameters.AddWithValue(siteId);
		target.Parameters.AddWithValue($"run-secret-store-target-{Guid.NewGuid():N}");
		return (Guid)(await target.ExecuteScalarAsync())!;
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

	private async Task<DateTime> GetExpiresAtAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand select = new("SELECT expires_at FROM run_secrets WHERE run_id = $1", connection);
		select.Parameters.AddWithValue(runId);
		return (DateTime)(await select.ExecuteScalarAsync())!;
	}

	private async Task SetExpiresAtAsync(Guid runId, DateTime expiresAt)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new("UPDATE run_secrets SET expires_at = $2 WHERE run_id = $1", connection);
		update.Parameters.AddWithValue(runId);
		update.Parameters.AddWithValue(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc));
		await update.ExecuteNonQueryAsync();
	}

	private async Task<long> CountRowsAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", connection);
		count.Parameters.AddWithValue(runId);
		return (long)(await count.ExecuteScalarAsync())!;
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

	private async Task<string> GetLatestAuditDetailAsync(string eventType, Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"""
			SELECT detail::text FROM audit_log
			WHERE event_type = $1 AND run_id = $2
			ORDER BY occurred_at DESC LIMIT 1
			""", connection);
		query.Parameters.AddWithValue(eventType);
		query.Parameters.AddWithValue(runId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// Issue #718 AC: run secrets have no reusable credential row, so `username` (never
	/// secret material -- it never goes through the cipher) is the identity an auditor
	/// needs to tell WHICH ad hoc credential a job decrypted. Proves it lands in
	/// `secret.run_decrypted`'s detail alongside the pre-existing target/purpose
	/// attribution, and that the secret value itself never does.
	/// </summary>
	[Fact]
	public async Task Decrypt_AuditDetail_CarriesUsernameIdentity_NeverTheSecret()
	{
		const string secretValue = "invented-adhoc-token-identity-check";
		const string username = "identity-check-user@example.internal";
		await _store.StoreAsync(_runId, new RunSecretCredential(username, secretValue), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		Guid jobId = await SeedJobAsync(_runId);
		using (await _store.DecryptAsync(_runId, jobId, "engine", CancellationToken.None))
		{
		}

		string detailJson = await GetLatestAuditDetailAsync("secret.run_decrypted", _runId);
		using JsonDocument detail = JsonDocument.Parse(detailJson);

		Assert.Equal(username, detail.RootElement.GetProperty("username").GetString());
		Assert.DoesNotContain(secretValue, detailJson, StringComparison.Ordinal);
	}
}
