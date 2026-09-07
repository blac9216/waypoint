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
/// Migration 0111's runner grants (issue #1480), following the
/// <see cref="EsxPatchStoreIndexRunnerRoleGrantTests"/>/<see cref="RunnerRoleGrantDriftTests"/>
/// convention (this repo's #556 convention: prove both the grant that exists and the
/// operation that must still be denied). <c>waypoint_download_runner</c> gets exactly
/// <c>SELECT, INSERT, UPDATE</c> on <c>vks_library_items</c> (no DELETE);
/// <c>waypoint_compliance_runner</c> gets nothing on it at all (least-privilege
/// boundary -- this is a download-domain concern, ADR-0013 §2). The API's own SELECT
/// access needs no grant here: it connects as the table owner (migration 0111's own
/// comment), which this test suite does not need to separately prove.
/// </summary>
[Collection("Postgres")]
public sealed class VksLibraryIndexRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public VksLibraryIndexRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = builder.ConnectionString;

		builder.Username = "waypoint_compliance_runner";
		_complianceRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task DownloadRunnerRole_CanSelectInsertUpdate_OnVksLibraryItems()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		string name = $"grant-test-{Guid.NewGuid():N}";

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO vks_library_items (name, source, naming_era, parse_status)
			VALUES ($1, 'public', 'unparsed', 'unparsed')
			""", connection);
		insert.Parameters.AddWithValue(name);
		await insert.ExecuteNonQueryAsync();

		await using NpgsqlCommand update = new(
			"UPDATE vks_library_items SET last_seen_at = now() WHERE name = $1", connection);
		update.Parameters.AddWithValue(name);
		await update.ExecuteNonQueryAsync();

		await using NpgsqlCommand select = new("SELECT count(*) FROM vks_library_items WHERE name = $1", connection);
		select.Parameters.AddWithValue(name);
		object? count = await select.ExecuteScalarAsync();
		Assert.Equal(1L, count);
	}

	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteFromVksLibraryItems()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand delete = new("DELETE FROM vks_library_items", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal("42501", denied.SqlState);
	}

	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirelyOnVksLibraryItems()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand select = new("SELECT count(*) FROM vks_library_items", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
		Assert.Equal("42501", denied.SqlState);
	}
}
