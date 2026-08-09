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

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #188: <c>CredentialCreationCoordinator</c> commits the metadata row and the
/// secret atomically. A cipher failure mid-create (the same class of fault as a bad
/// master key) must roll back the metadata INSERT too -- these tests force that
/// failure with a throwing <see cref="IEnvelopeCipher"/> double (the real
/// <c>AesGcmEnvelopeCipher</c> would throw <see cref="MasterKeyUnavailableException"/>
/// for the same reason in production; the double is deterministic and does not
/// require corrupting an actual key file).
/// </summary>
[Collection("Postgres")]
public sealed class CredentialCreationCoordinatorTests : IAsyncLifetime
{
	private sealed class ThrowingCipher : IEnvelopeCipher
	{
		public SecretEnvelope Encrypt(byte[] plaintext, string associatedContext) =>
			throw new MasterKeyUnavailableException("invented-test-failure: master key unavailable");

		public byte[] Decrypt(SecretEnvelope envelope, string associatedContext) =>
			throw new NotSupportedException();
	}

	private readonly PostgresFixture _fixture;

	public CredentialCreationCoordinatorTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// The core AC: a secret-store failure between the metadata INSERT and commit
	/// rolls back BOTH, so the credential name is free again -- a retry with the same
	/// name succeeds instead of 409ing name_taken against an orphan (issue #188's
	/// exact repro).
	/// </summary>
	[Fact]
	public async Task CreateAsync_SecretStoreFailureMidCreate_RollsBackMetadata_AndARetrySucceeds()
	{
		CredentialCreationCoordinator coordinator = new(_fixture.ConnectionString, new ThrowingCipher(), NullLogger<CredentialCreationCoordinator>.Instance);
		string name = $"atomic-create-{Guid.NewGuid():N}";
		byte[] secret = Encoding.UTF8.GetBytes("invented-canary-secret-4471");

		await Assert.ThrowsAsync<MasterKeyUnavailableException>(() =>
			coordinator.CreateAsync(name, CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false, username: null, secret, "tester", CancellationToken.None));

		// No orphan: the metadata row must not exist at all.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand count = new("SELECT count(*) FROM credentials WHERE name = $1", connection);
			count.Parameters.AddWithValue(name);
			Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
		}

		// A retry with the same name and a working cipher succeeds -- no spurious 409,
		// because nothing survived the rollback to collide with.
		CredentialCreationCoordinator workingCoordinator = new(
			_fixture.ConnectionString, new AesGcmEnvelopeCipher(new FileMasterKeyProvider(WriteKeyFile())), NullLogger<CredentialCreationCoordinator>.Instance);
		Guid? retryId = await workingCoordinator.CreateAsync(
			name, CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false, username: null, secret, "tester", CancellationToken.None);

		Assert.NotNull(retryId);

		CredentialRepository repository = new(_fixture.ConnectionString);
		Waypoint.Core.Secrets.CredentialResponse? created = await repository.GetAsync(retryId!.Value, CancellationToken.None);
		Assert.NotNull(created);
		Assert.True(created!.HasSecret);
	}

	/// <summary>Happy path is unaffected: metadata + secret commit together, has_secret=true, rotated_at stamped.</summary>
	[Fact]
	public async Task CreateAsync_WithSecret_CommitsMetadataAndSecretTogether()
	{
		string keyPath = WriteKeyFile();
		CredentialCreationCoordinator coordinator = new(
			_fixture.ConnectionString, new AesGcmEnvelopeCipher(new FileMasterKeyProvider(keyPath)), NullLogger<CredentialCreationCoordinator>.Instance);
		string name = $"atomic-happy-{Guid.NewGuid():N}";
		byte[] secret = Encoding.UTF8.GetBytes("invented-happy-path-secret-91a2");

		Guid? id = await coordinator.CreateAsync(
			name, CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false, username: null, secret, "tester", CancellationToken.None);

		Assert.NotNull(id);

		CredentialRepository repository = new(_fixture.ConnectionString);
		Waypoint.Core.Secrets.CredentialResponse? created = await repository.GetAsync(id!.Value, CancellationToken.None);
		Assert.NotNull(created);
		Assert.True(created!.HasSecret);
		Assert.NotNull(created.RotatedAt);
	}

	/// <summary>The create-without-secret path is unaffected by the coordinator: metadata commits alone, has_secret stays false, no rotated_at stamp.</summary>
	[Fact]
	public async Task CreateAsync_WithoutSecret_CreatesMetadataOnly()
	{
		CredentialCreationCoordinator coordinator = new(_fixture.ConnectionString, new ThrowingCipher(), NullLogger<CredentialCreationCoordinator>.Instance);
		string name = $"atomic-no-secret-{Guid.NewGuid():N}";

		Guid? id = await coordinator.CreateAsync(
			name, CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false, username: null, secretValue: null, "tester", CancellationToken.None);

		Assert.NotNull(id);

		CredentialRepository repository = new(_fixture.ConnectionString);
		Waypoint.Core.Secrets.CredentialResponse? created = await repository.GetAsync(id!.Value, CancellationToken.None);
		Assert.NotNull(created);
		Assert.False(created!.HasSecret);
		Assert.Null(created.RotatedAt);
	}

	/// <summary>Name-taken still returns null (the controller's existing 409 mapping), not an exception.</summary>
	[Fact]
	public async Task CreateAsync_NameTaken_ReturnsNull()
	{
		CredentialCreationCoordinator coordinator = new(_fixture.ConnectionString, new ThrowingCipher(), NullLogger<CredentialCreationCoordinator>.Instance);
		string name = $"atomic-taken-{Guid.NewGuid():N}";

		Guid? first = await coordinator.CreateAsync(
			name, CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false, username: null, secretValue: null, "tester", CancellationToken.None);
		Assert.NotNull(first);

		Guid? second = await coordinator.CreateAsync(
			name, CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false, username: null, secretValue: null, "tester", CancellationToken.None);
		Assert.Null(second);
	}

	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-coordinator-test").FullName;

	private string WriteKeyFile()
	{
		string path = Path.Combine(_keyDirectory, $"key-{Guid.NewGuid():N}");
		File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
		return path;
	}
}
