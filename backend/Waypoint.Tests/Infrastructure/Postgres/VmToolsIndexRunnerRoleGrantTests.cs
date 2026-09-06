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
/// Migration 0109's runner grants (issue #1392), following the
/// <see cref="EsxPatchStoreIndexRunnerRoleGrantTests"/>/<see cref="RunnerRoleGrantDriftTests"/>
/// convention (this repo's #556 convention: prove both the grant that exists and the
/// operation that must still be denied). <c>waypoint_download_runner</c> gets exactly
/// <c>SELECT, INSERT, UPDATE</c> on both VMTools tables (the crawler and the
/// <c>versions</c>-file parser both upsert, never delete); <c>waypoint_compliance_runner</c>
/// gets nothing on either table (least-privilege boundary -- this is a
/// download-domain concern, ADR-0013 §2).
/// </summary>
[Collection("Postgres")]
public sealed class VmToolsIndexRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public VmToolsIndexRunnerRoleGrantTests(PostgresFixture fixture)
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
	public async Task DownloadRunnerRole_CanSelectInsertUpdate_OnBothTables()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insertArtifact = new(
			"""
			INSERT INTO vmtools_artifact_index (relative_path, platform, file_type)
			VALUES ($1, 'windows', 'iso')
			""", connection);
		insertArtifact.Parameters.AddWithValue($"grant-test-{Guid.NewGuid():N}.iso");
		await insertArtifact.ExecuteNonQueryAsync();

		await using NpgsqlCommand updateArtifact = new(
			"UPDATE vmtools_artifact_index SET last_seen_at = now() WHERE platform = 'windows'", connection);
		await updateArtifact.ExecuteNonQueryAsync();

		await using NpgsqlCommand selectArtifact = new("SELECT count(*) FROM vmtools_artifact_index", connection);
		await selectArtifact.ExecuteScalarAsync();

		await using NpgsqlCommand insertMapping = new(
			"""
			INSERT INTO vmtools_esx_version_mapping (sequence_in_file, esxi_version_dir, tools_version_code, raw_row)
			VALUES (0, $1, 'grant-test', 'raw')
			""", connection);
		insertMapping.Parameters.AddWithValue($"esx/grant-test-{Guid.NewGuid():N}");
		await insertMapping.ExecuteNonQueryAsync();

		await using NpgsqlCommand updateMapping = new(
			"UPDATE vmtools_esx_version_mapping SET raw_row = 'raw-updated' WHERE tools_version_code = 'grant-test'", connection);
		await updateMapping.ExecuteNonQueryAsync();

		await using NpgsqlCommand selectMapping = new("SELECT count(*) FROM vmtools_esx_version_mapping", connection);
		await selectMapping.ExecuteScalarAsync();
	}

	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteFromEitherTable()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand deleteArtifacts = new("DELETE FROM vmtools_artifact_index", connection);
		PostgresException deniedArtifacts = await Assert.ThrowsAsync<PostgresException>(() => deleteArtifacts.ExecuteNonQueryAsync());
		Assert.Equal("42501", deniedArtifacts.SqlState);

		await using NpgsqlCommand deleteMappings = new("DELETE FROM vmtools_esx_version_mapping", connection);
		PostgresException deniedMappings = await Assert.ThrowsAsync<PostgresException>(() => deleteMappings.ExecuteNonQueryAsync());
		Assert.Equal("42501", deniedMappings.SqlState);
	}

	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirelyOnBothTables()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand selectArtifacts = new("SELECT count(*) FROM vmtools_artifact_index", connection);
		PostgresException deniedArtifacts = await Assert.ThrowsAsync<PostgresException>(() => selectArtifacts.ExecuteScalarAsync());
		Assert.Equal("42501", deniedArtifacts.SqlState);

		await using NpgsqlCommand selectMappings = new("SELECT count(*) FROM vmtools_esx_version_mapping", connection);
		PostgresException deniedMappings = await Assert.ThrowsAsync<PostgresException>(() => selectMappings.ExecuteScalarAsync());
		Assert.Equal("42501", deniedMappings.SqlState);
	}
}
