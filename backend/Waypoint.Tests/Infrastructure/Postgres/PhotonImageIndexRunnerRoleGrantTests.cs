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
using Waypoint.Core.Downloads.Photon;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads.Photon;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Migration 0135's runner grant (issue #1790), following
/// <see cref="PhotonRepoIndexRunnerRoleGrantTests"/>'s own #556 convention: prove both
/// the grant that exists (SELECT/INSERT/UPDATE on <c>photon_image_index</c> for
/// <c>waypoint_download_runner</c>) and the operations that must still be denied (no
/// DELETE on that table for that role; no access at all for
/// <c>waypoint_compliance_runner</c>).
/// </summary>
[Collection("Postgres")]
public sealed class PhotonImageIndexRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public PhotonImageIndexRunnerRoleGrantTests(PostgresFixture fixture)
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

	/// <summary>The full surface PhotonIndexRepository exercises as the real download-runner role: SELECT + INSERT + UPDATE (upsert), no DELETE.</summary>
	[Fact]
	public async Task DownloadRunnerRole_CanUpsertAndListImageIndexEntries()
	{
		PhotonIndexRepository repository = new(_downloadRunnerConnectionString);
		string version = $"5.0-grant-test-{Guid.NewGuid():N}";

		await repository.UpsertImageIndexEntryAsync(
			new PhotonImageIndexEntry(version, PhotonImageChannels.Ga, PhotonImageKinds.Iso, "photon-5.0-x86_64.iso", 123456, "\"etag-1\""),
			CancellationToken.None);
		// Re-discovery of the same triple -- the UPDATE half of the upsert, as the real runner role.
		await repository.UpsertImageIndexEntryAsync(
			new PhotonImageIndexEntry(version, PhotonImageChannels.Ga, PhotonImageKinds.Iso, "photon-5.0-x86_64.iso", 123999, "\"etag-2\""),
			CancellationToken.None);

		PhotonImageIndexEntry? entry = await repository.GetImageIndexEntryAsync(
			version, PhotonImageChannels.Ga, "photon-5.0-x86_64.iso", CancellationToken.None);
		Assert.NotNull(entry);
		Assert.Equal(123999, entry!.SizeBytes);
		Assert.Equal("\"etag-2\"", entry.ETag);
	}

	/// <summary>The negative half of the #556 convention: no DELETE grant.</summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteFromImageIndex()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand delete = new("DELETE FROM photon_image_index", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal("42501", denied.SqlState);
	}

	/// <summary>Least-privilege boundary: Photon is a download-domain concern (ADR-0013 SS2) -- compliance-runner gets nothing.</summary>
	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirelyOnImageIndex()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand select = new("SELECT count(*) FROM photon_image_index", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
		Assert.Equal("42501", denied.SqlState);
	}
}
