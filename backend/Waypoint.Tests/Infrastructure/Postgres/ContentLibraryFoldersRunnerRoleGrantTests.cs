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
/// Migration 0113's no-grant posture (issue #1389), same rationale and the same
/// negative-direction shape as <see cref="RunnerRoleGrantDriftTests"/>'s
/// <c>*_CannotReadRepoCredentialBindings</c> tests for 0103's
/// <c>repo_credential_bindings</c>: exactly one consumer today, the API process
/// (<c>ContentLibraryFoldersController</c>), so BOTH runner roles must be denied even
/// a bare SELECT on either new table.
/// </summary>
[Collection("Postgres")]
public sealed class ContentLibraryFoldersRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public ContentLibraryFoldersRunnerRoleGrantTests(PostgresFixture fixture)
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
	public async Task DownloadRunnerRole_CannotReadContentLibraryFolders() =>
		await AssertSelectDeniedAsync(_downloadRunnerConnectionString, "content_library_folders");

	[Fact]
	public async Task ComplianceRunnerRole_CannotReadContentLibraryFolders() =>
		await AssertSelectDeniedAsync(_complianceRunnerConnectionString, "content_library_folders");

	[Fact]
	public async Task DownloadRunnerRole_CannotReadContentLibraryItemFolders() =>
		await AssertSelectDeniedAsync(_downloadRunnerConnectionString, "content_library_item_folders");

	[Fact]
	public async Task ComplianceRunnerRole_CannotReadContentLibraryItemFolders() =>
		await AssertSelectDeniedAsync(_complianceRunnerConnectionString, "content_library_item_folders");

	private static async Task AssertSelectDeniedAsync(string connectionString, string tableName)
	{
		await using NpgsqlConnection connection = new(connectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand select = new($"SELECT id FROM {tableName} LIMIT 1", connection);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}
}
