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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Epic #8 slice 2 (#179) against real Postgres: store/rotate/decrypt round trips,
/// fail-closed audit in the decrypt transaction, wrong-master-key behavior, the
/// automatic redaction window owned by the decrypt handle, and write-only deletion.
/// </summary>
[Collection("Postgres")]
public sealed class CredentialSecretStoreTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-secret-test").FullName;
	private InPlaySecretRedactor _redactor = null!;
	private CredentialSecretStore _store = null!;
	private Guid _credentialId;

	public CredentialSecretStoreTests(PostgresFixture fixture)
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
		_credentialId = await SeedCredentialAsync();
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

	private CredentialSecretStore CreateStore(string keyPath)
	{
		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(keyPath));
		return new CredentialSecretStore(
			_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
	}

	[Fact]
	public async Task StoreDecryptRoundTrip_AuditsBothSides_AndRedactsWhileTheHandleLives()
	{
		const string secretValue = "invented-depot-token-e5f6";
		await _store.StoreAsync(_credentialId, Encoding.UTF8.GetBytes(secretValue), "tester", CancellationToken.None);

		Guid jobId = await SeedJobAsync();
		using (DecryptedSecret handle = await _store.DecryptAsync(_credentialId, "engine", jobId, null, CancellationToken.None))
		{
			Assert.Equal(secretValue, handle.Value);

			// Control 1: holding the handle IS being redacted.
			Assert.Equal("[REDACTED]", _redactor.Redact(secretValue));
		}

		// Disposing the handle ends the in-play window.
		Assert.Equal(secretValue, _redactor.Redact(secretValue));

		Assert.Equal(1, await CountAuditAsync("secret.stored", _credentialId));
		Assert.Equal(1, await CountAuditAsync("secret.decrypted", _credentialId));
		Assert.Equal(jobId, await GetDecryptAuditJobAsync(_credentialId));
	}

	[Fact]
	public async Task Rotation_ReplacesTheRowInPlace_AndTheNewValueWins()
	{
		await _store.StoreAsync(_credentialId, Encoding.UTF8.GetBytes("first-invented"), "tester", CancellationToken.None);
		await _store.StoreAsync(_credentialId, Encoding.UTF8.GetBytes("second-invented"), "tester", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM credential_secrets WHERE credential_id = $1", connection);
		count.Parameters.AddWithValue(_credentialId);
		Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

		using DecryptedSecret handle = await _store.DecryptAsync(_credentialId, "tester", null, null, CancellationToken.None);
		Assert.Equal("second-invented", handle.Value);
	}

	[Fact]
	public async Task AWrongMasterKey_FailsClosed_WithTheOperatorError_AndStillAudits()
	{
		await _store.StoreAsync(_credentialId, Encoding.UTF8.GetBytes("invented-value"), "tester", CancellationToken.None);

		CredentialSecretStore otherStore = CreateStore(WriteKeyFile());
		await Assert.ThrowsAsync<MasterKeyUnavailableException>(
			() => otherStore.DecryptAsync(_credentialId, "tester", null, null, CancellationToken.None));

		// The decrypt attempt was audited even though it failed: the audit commits
		// with the ciphertext read, before the crypto can succeed or fail. "Detect
		// use" covers attempts, not only successes.
		Assert.Equal(1, await CountAuditAsync("secret.decrypted", _credentialId));
	}

	[Fact]
	public async Task NoStoredSecret_ThrowsTheTypedNotFound()
	{
		CredentialSecretNotFoundException error = await Assert.ThrowsAsync<CredentialSecretNotFoundException>(
			() => _store.DecryptAsync(_credentialId, "tester", null, null, CancellationToken.None));
		Assert.Equal(_credentialId, error.CredentialId);
		Assert.Equal(0, await CountAuditAsync("secret.decrypted", _credentialId));
	}

	[Fact]
	public async Task Delete_RemovesTheSecret_AndAuditsOnlyWhenSomethingExisted()
	{
		await _store.StoreAsync(_credentialId, Encoding.UTF8.GetBytes("invented"), "tester", CancellationToken.None);

		Assert.True(await _store.DeleteAsync(_credentialId, "tester", CancellationToken.None));
		Assert.False(await _store.DeleteAsync(_credentialId, "tester", CancellationToken.None));
		Assert.Equal(1, await CountAuditAsync("secret.deleted", _credentialId));
		await Assert.ThrowsAsync<CredentialSecretNotFoundException>(
			() => _store.DecryptAsync(_credentialId, "tester", null, null, CancellationToken.None));
	}

	/// <summary>#183: the two decrypt guards slice 1 left uncovered.</summary>
	[Fact]
	public async Task AnUnsupportedAlgorithmOrTruncatedBlob_FailsClosed()
	{
		await _store.StoreAsync(_credentialId, Encoding.UTF8.GetBytes("invented"), "tester", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand corruptAlgorithm = new(
			"UPDATE credential_secrets SET algorithm = 'ROT13' WHERE credential_id = $1", connection))
		{
			corruptAlgorithm.Parameters.AddWithValue(_credentialId);
			await corruptAlgorithm.ExecuteNonQueryAsync();
		}

		MasterKeyUnavailableException algorithmError = await Assert.ThrowsAsync<MasterKeyUnavailableException>(
			() => _store.DecryptAsync(_credentialId, "tester", null, null, CancellationToken.None));
		Assert.Contains("ROT13", algorithmError.Message, StringComparison.Ordinal);

		await using (NpgsqlCommand truncate = new(
			"UPDATE credential_secrets SET algorithm = 'AES-256-GCM', ciphertext = '\\x0102'::bytea WHERE credential_id = $1", connection))
		{
			truncate.Parameters.AddWithValue(_credentialId);
			await truncate.ExecuteNonQueryAsync();
		}

		await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
			() => _store.DecryptAsync(_credentialId, "tester", null, null, CancellationToken.None));
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'service') RETURNING id", connection);
		insert.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('download', 1, 'queued') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<long> CountAuditAsync(string eventType, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new(
			"SELECT count(*) FROM audit_log WHERE event_type = $1 AND credential_id = $2", connection);
		count.Parameters.AddWithValue(eventType);
		count.Parameters.AddWithValue(credentialId);
		return (long)(await count.ExecuteScalarAsync())!;
	}

	private async Task<Guid?> GetDecryptAuditJobAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT job_id FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1", connection);
		query.Parameters.AddWithValue(credentialId);
		object? result = await query.ExecuteScalarAsync();
		return result is Guid guid ? guid : null;
	}
}
