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
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Migration 20260913040921's grants (issue #1513, epic #1185), following the
/// <see cref="RetentionSweepRunnerRoleGrantTests"/>/<see cref="ManagedToolInstallRunnerRoleGrantTests"/>
/// convention (this repo's #556 grant-hygiene rule: prove the grant that exists AND
/// the operation that must still be denied). <c>waypoint_download_runner</c> gets
/// exactly SELECT/INSERT on <c>content_libraries</c> (resolve-or-create the Supervisor
/// library, never rename/delete it) and SELECT/INSERT/UPDATE on
/// <c>content_library_items</c> (list/add/update the library's items, never delete
/// one -- this repo's never-auto-remove convention). <c>waypoint_compliance_runner</c>
/// gets nothing on either table (download-domain job, ADR-0013 SS2) -- see
/// <see cref="ContentLibraryFoldersRunnerRoleGrantTests"/>/<see cref="ContentLibraryItemsRunnerRoleGrantTests"/>
/// for that side.
/// </summary>
[Collection("Postgres")]
public sealed class SupervisorLibrarySyncRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _ownerConnectionString = string.Empty;

	public SupervisorLibrarySyncRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		_ownerConnectionString = _fixture.ConnectionString;

		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task DownloadRunnerRole_CanSelectAndInsertContentLibraries()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		string name = $"grant-test-{Guid.NewGuid():N}";
		await using NpgsqlCommand insert = new(
			"INSERT INTO content_libraries (name, disk_path) VALUES ($1, $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(name);
		insert.Parameters.AddWithValue($"/tmp/{name}");
		object? id = await insert.ExecuteScalarAsync();
		Assert.NotNull(id);

		await using NpgsqlCommand select = new("SELECT id FROM content_libraries WHERE id = $1", connection);
		select.Parameters.AddWithValue((Guid)id!);
		object? selected = await select.ExecuteScalarAsync();
		Assert.NotNull(selected);
	}

	[Fact]
	public async Task DownloadRunnerRole_CannotUpdateOrDeleteContentLibraries()
	{
		Guid libraryId = await InsertLibraryAsync();

		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand update = new("UPDATE content_libraries SET name = 'renamed' WHERE id = $1", connection);
		update.Parameters.AddWithValue(libraryId);
		PostgresException updateDenied = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateDenied.SqlState);

		await using NpgsqlCommand delete = new("DELETE FROM content_libraries WHERE id = $1", connection);
		delete.Parameters.AddWithValue(libraryId);
		PostgresException deleteDenied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteDenied.SqlState);
	}

	[Fact]
	public async Task DownloadRunnerRole_CanSelectInsertAndUpdateContentLibraryItems()
	{
		Guid libraryId = await InsertLibraryAsync();

		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO content_library_items (id, library_id, directory_name, name, type, description, files)
			VALUES ($1, $2, $3, $4, 'vcsp.other', $5, '[{"name": "a.zip", "size": 1, "content_hash": "abc"}]'::jsonb)
			""", connection);
		Guid itemId = Guid.NewGuid();
		insert.Parameters.AddWithValue(itemId);
		insert.Parameters.AddWithValue(libraryId);
		insert.Parameters.AddWithValue(itemId.ToString("N"));
		insert.Parameters.AddWithValue("a.zip");
		insert.Parameters.AddWithValue("PROD/COMP/SUPERVISOR/a.zip");
		await insert.ExecuteNonQueryAsync();

		await using NpgsqlCommand select = new("SELECT id FROM content_library_items WHERE id = $1", connection);
		select.Parameters.AddWithValue(itemId);
		Assert.NotNull(await select.ExecuteScalarAsync());

		await using NpgsqlCommand update = new("UPDATE content_library_items SET version = version + 1 WHERE id = $1", connection);
		update.Parameters.AddWithValue(itemId);
		await update.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteContentLibraryItems()
	{
		Guid libraryId = await InsertLibraryAsync();
		Guid itemId = await InsertItemAsync(libraryId);

		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand delete = new("DELETE FROM content_library_items WHERE id = $1", connection);
		delete.Parameters.AddWithValue(itemId);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
	}

	private async Task<Guid> InsertLibraryAsync()
	{
		await using NpgsqlConnection connection = new(_ownerConnectionString);
		await connection.OpenAsync();

		string name = $"grant-test-{Guid.NewGuid():N}";
		await using NpgsqlCommand insert = new(
			"INSERT INTO content_libraries (name, disk_path) VALUES ($1, $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(name);
		insert.Parameters.AddWithValue($"/tmp/{name}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertItemAsync(Guid libraryId)
	{
		await using NpgsqlConnection connection = new(_ownerConnectionString);
		await connection.OpenAsync();

		Guid itemId = Guid.NewGuid();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO content_library_items (id, library_id, directory_name, name, type, description, files)
			VALUES ($1, $2, $3, $4, 'vcsp.other', $5, '[{"name": "a.zip", "size": 1, "content_hash": "abc"}]'::jsonb)
			""", connection);
		insert.Parameters.AddWithValue(itemId);
		insert.Parameters.AddWithValue(libraryId);
		insert.Parameters.AddWithValue(itemId.ToString("N"));
		insert.Parameters.AddWithValue("a.zip");
		insert.Parameters.AddWithValue("PROD/COMP/SUPERVISOR/a.zip");
		await insert.ExecuteNonQueryAsync();
		return itemId;
	}
}
