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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1517 (migration 0103) against real Postgres:
/// <see cref="RepoCredentialBindingRepository"/>'s CRUD, store/type validation, and the
/// override-not-duplicate semantics of a table UNIQUE on <c>store</c> alone -- the
/// same real-Postgres coverage
/// <see cref="TargetCredentialBindingRepositoryTests"/> provides for its own binding
/// table, adapted for this bounded context's single-purpose (no purpose dimension)
/// shape.
/// </summary>
[Collection("Postgres")]
public sealed class RepoCredentialBindingRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private RepoCredentialBindingRepository _bindings = null!;

	public RepoCredentialBindingRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_bindings = new RepoCredentialBindingRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task SetAsync_ValidStoreAndCompatibleType_Succeeds()
	{
		Guid credentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);

		RepoCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(RepoStores.Depot, credentialId, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingWriteOutcome.Ok, outcome);
		RepoCredentialBinding? binding = await _bindings.GetAsync(RepoStores.Depot, CancellationToken.None);
		Assert.NotNull(binding);
		Assert.Equal(RepoStores.Depot, binding!.Store);
		Assert.Equal(credentialId, binding.CredentialId);
	}

	[Theory]
	[InlineData(RepoStores.Depot)]
	[InlineData(RepoStores.Umds)]
	[InlineData(RepoStores.Photon)]
	[InlineData(RepoStores.VmTools)]
	[InlineData(RepoStores.Vks)]
	[InlineData(RepoStores.ContentLibraries)]
	public async Task SetAsync_EveryClosedSetStore_Succeeds(string store)
	{
		Guid credentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);

		RepoCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(store, credentialId, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingWriteOutcome.Ok, outcome);
	}

	[Fact]
	public async Task SetAsync_UnknownStore_IsRejected()
	{
		Guid credentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);

		RepoCredentialBindingWriteOutcome outcome = await _bindings.SetAsync("made-up-store", credentialId, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingWriteOutcome.InvalidStore, outcome);
	}

	[Fact]
	public async Task SetAsync_UnknownCredential_ReturnsCredentialNotFound()
	{
		RepoCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(RepoStores.Depot, Guid.NewGuid(), CancellationToken.None);

		Assert.Equal(RepoCredentialBindingWriteOutcome.CredentialNotFound, outcome);
	}

	[Fact]
	public async Task SetAsync_IncompatibleCredentialType_IsRejected()
	{
		// A repo store binding must name a repo-basic-auth credential -- a `token`
		// credential (or any other type) must never be accepted.
		Guid tokenCredentialId = await SeedCredentialAsync(CredentialTypes.Token);

		RepoCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(RepoStores.Umds, tokenCredentialId, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingWriteOutcome.IncompatibleCredentialType, outcome);
		Assert.Null(await _bindings.GetAsync(RepoStores.Umds, CancellationToken.None));
	}

	[Fact]
	public async Task SetAsync_SameStoreTwice_ReplacesRatherThanDuplicating()
	{
		Guid firstCredentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);
		Guid secondCredentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);

		await _bindings.SetAsync(RepoStores.Photon, firstCredentialId, CancellationToken.None);
		RepoCredentialBindingWriteOutcome overrideOutcome = await _bindings.SetAsync(RepoStores.Photon, secondCredentialId, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingWriteOutcome.Ok, overrideOutcome);
		RepoCredentialBinding? binding = await _bindings.GetAsync(RepoStores.Photon, CancellationToken.None);
		Assert.Equal(secondCredentialId, binding!.CredentialId);

		// Exactly one row for the store -- replaced, not duplicated.
		IReadOnlyList<RepoCredentialBinding> all = await _bindings.ListAsync(CancellationToken.None);
		Assert.Single(all, b => b.Store == RepoStores.Photon);
	}

	[Fact]
	public async Task ListAsync_MultipleStores_ReturnsEachOnce()
	{
		Guid depotCredentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);
		Guid umdsCredentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);
		await _bindings.SetAsync(RepoStores.Depot, depotCredentialId, CancellationToken.None);
		await _bindings.SetAsync(RepoStores.Umds, umdsCredentialId, CancellationToken.None);

		IReadOnlyList<RepoCredentialBinding> all = await _bindings.ListAsync(CancellationToken.None);

		Assert.Equal(2, all.Count);
		Assert.Contains(all, b => b.Store == RepoStores.Depot && b.CredentialId == depotCredentialId);
		Assert.Contains(all, b => b.Store == RepoStores.Umds && b.CredentialId == umdsCredentialId);
	}

	[Fact]
	public async Task GetAsync_NoBindingForStore_ReturnsNull()
	{
		Assert.Null(await _bindings.GetAsync(RepoStores.Vks, CancellationToken.None));
	}

	[Fact]
	public async Task ClearAsync_RemovesTheBinding()
	{
		Guid credentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);
		await _bindings.SetAsync(RepoStores.ContentLibraries, credentialId, CancellationToken.None);

		RepoCredentialBindingDeleteOutcome outcome = await _bindings.ClearAsync(RepoStores.ContentLibraries, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingDeleteOutcome.Deleted, outcome);
		Assert.Null(await _bindings.GetAsync(RepoStores.ContentLibraries, CancellationToken.None));
	}

	[Fact]
	public async Task ClearAsync_NoExistingBinding_ReturnsNotFound()
	{
		RepoCredentialBindingDeleteOutcome outcome = await _bindings.ClearAsync(RepoStores.Vks, CancellationToken.None);

		Assert.Equal(RepoCredentialBindingDeleteOutcome.NotFound, outcome);
	}

	/// <summary>Migration 0103's <c>repo_credential_bindings_store_check</c> is defense-in-depth behind the repository's own <see cref="RepoStores.IsValid"/> gate -- proven directly against the raw table, bypassing the repository.</summary>
	[Fact]
	public async Task Migration0103_StoreCheckConstraint_RejectsAnUnknownStore()
	{
		Guid credentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO repo_credential_bindings (store, credential_id) VALUES ('bogus-store', $1)", connection);
		insert.Parameters.AddWithValue(credentialId);

		NpgsqlException ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
		Assert.Contains("repo_credential_bindings_store_check", ex.Message, StringComparison.Ordinal);
	}

	/// <summary>Migration 0103's <c>repo_credential_bindings_store_key</c> UNIQUE constraint is defense-in-depth behind the repository's own ON CONFLICT upsert -- proven directly with two raw inserts.</summary>
	[Fact]
	public async Task Migration0103_StoreUniqueConstraint_RejectsADuplicateInsert()
	{
		Guid firstCredentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);
		Guid secondCredentialId = await SeedCredentialAsync(CredentialTypes.RepoBasicAuth);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand insert = new(
			"INSERT INTO repo_credential_bindings (store, credential_id) VALUES ('vks', $1)", connection))
		{
			insert.Parameters.AddWithValue(firstCredentialId);
			await insert.ExecuteNonQueryAsync();
		}

		await using NpgsqlCommand duplicate = new(
			"INSERT INTO repo_credential_bindings (store, credential_id) VALUES ('vks', $1)", connection);
		duplicate.Parameters.AddWithValue(secondCredentialId);

		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
	}

	private async Task<Guid> SeedCredentialAsync(string credentialType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type, owner) VALUES ($1, $2, 'shared') RETURNING id", connection);
		insert.Parameters.AddWithValue($"repo-binding-test-cred-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialType);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE repo_credential_bindings, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
