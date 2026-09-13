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
/// Migration 0133's original no-grant posture (issue #1396) denied both runner roles;
/// the 20260913040921 migration (issue #1513) narrowly widened
/// <c>waypoint_download_runner</c> to SELECT/INSERT/UPDATE for
/// <c>SupervisorLibrarySyncJobHandler</c> -- see
/// <c>SupervisorLibrarySyncRunnerRoleGrantTests</c> for that positive/negative
/// coverage. <c>waypoint_compliance_runner</c> is untouched by that migration (this is
/// a download-domain job, ADR-0013 SS2) and stays denied even a bare SELECT, mirroring
/// <see cref="ContentLibraryFoldersRunnerRoleGrantTests"/>'s pattern for 0113.
/// </summary>
[Collection("Postgres")]
public sealed class ContentLibraryItemsRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _complianceRunnerConnectionString = string.Empty;

	public ContentLibraryItemsRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_compliance_runner",
			Password = "waypoint_test",
		};
		_complianceRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task ComplianceRunnerRole_CannotReadContentLibraryItems() =>
		await AssertSelectDeniedAsync(_complianceRunnerConnectionString, "content_library_items");

	private static async Task AssertSelectDeniedAsync(string connectionString, string tableName)
	{
		await using NpgsqlConnection connection = new(connectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand select = new($"SELECT id FROM {tableName} LIMIT 1", connection);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}
}
