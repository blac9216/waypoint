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
/// Migration 0130's runner grants (issue #1509), following the
/// <see cref="EsxPatchStoreIndexRunnerRoleGrantTests"/>/#556 convention: prove both the
/// grant that exists (SELECT/INSERT/UPDATE on <c>photon_repo_index</c> for
/// <c>waypoint_download_runner</c>) and the operations that must still be denied (no
/// DELETE on that table for that role; no access at all for
/// <c>waypoint_compliance_runner</c>; and no grant at all yet on
/// <c>photon_subscription_config</c> for either role -- that table's own reader/writer
/// is still a separate, unfiled issue, mirroring 0118's <c>oci_bundles</c> precedent).
/// Migration 0135 (issue #1790) granted <c>photon_image_index</c> to
/// <c>waypoint_download_runner</c> -- see <see cref="PhotonImageIndexRunnerRoleGrantTests"/>
/// for that grant's own proof.
/// </summary>
[Collection("Postgres")]
public sealed class PhotonRepoIndexRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public PhotonRepoIndexRunnerRoleGrantTests(PostgresFixture fixture)
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
	public async Task DownloadRunnerRole_CanUpsertAndListRepoIndexEntries()
	{
		PhotonIndexRepository repository = new(_downloadRunnerConnectionString);
		// This class shares its Postgres database with the whole "Postgres" collection
		// -- a unique version value plus a point lookup keeps this assertion honest
		// regardless of what other tests have already written to photon_repo_index.
		string version = $"5.0-grant-test-{Guid.NewGuid():N}";

		await repository.UpsertRepoIndexEntryAsync(
			new PhotonRepoIndexEntry(version, PhotonRepoVariants.Release, PhotonArches.X8664, "https://photon.example.internal/photon/5.0/photon_release_5.0_x86_64", true, "1699999999", 1795),
			CancellationToken.None);
		// Re-discovery of the same triple -- the UPDATE half of the upsert, as the real runner role.
		await repository.UpsertRepoIndexEntryAsync(
			new PhotonRepoIndexEntry(version, PhotonRepoVariants.Release, PhotonArches.X8664, "https://photon.example.internal/photon/5.0/photon_release_5.0_x86_64", true, "1700000001", 1796),
			CancellationToken.None);

		PhotonRepoIndexEntry? entry = await repository.GetRepoIndexEntryAsync(
			version, PhotonRepoVariants.Release, PhotonArches.X8664, CancellationToken.None);
		Assert.NotNull(entry);
		Assert.Equal("1700000001", entry!.RepomdRevision);
		Assert.Equal(1796, entry.PackageCount);
	}

	/// <summary>The negative half of the #556 convention: no DELETE grant.</summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteFromRepoIndex()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand delete = new("DELETE FROM photon_repo_index", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal("42501", denied.SqlState);
	}

	/// <summary>Least-privilege boundary: Photon is a download-domain concern (ADR-0013 SS2) -- compliance-runner gets nothing.</summary>
	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirelyOnRepoIndex()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand select = new("SELECT count(*) FROM photon_repo_index", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
		Assert.Equal("42501", denied.SqlState);
	}

	/// <summary>
	/// <c>photon_subscription_config</c> is still schema-only for both roles -- issue
	/// #1790 (migration 0135) granted <c>photon_image_index</c> to
	/// <c>waypoint_download_runner</c> (see <see cref="PhotonImageIndexRunnerRoleGrantTests"/>
	/// for that grant's own proof); <c>photon_subscription_config</c> has no consumer
	/// yet (0118's <c>oci_bundles</c> precedent for the same shape of gap).
	/// </summary>
	[Fact]
	public async Task NeitherRunnerRole_HasAnyGrantOnSubscriptionConfig()
	{
		foreach (string connectionString in new[] { _downloadRunnerConnectionString, _complianceRunnerConnectionString })
		{
			await using NpgsqlConnection connection = new(connectionString);
			await connection.OpenAsync();

			await using NpgsqlCommand selectSubscriptions = new("SELECT count(*) FROM photon_subscription_config", connection);
			PostgresException deniedSubscriptions = await Assert.ThrowsAsync<PostgresException>(() => selectSubscriptions.ExecuteScalarAsync());
			Assert.Equal("42501", deniedSubscriptions.SqlState);
		}
	}
}
