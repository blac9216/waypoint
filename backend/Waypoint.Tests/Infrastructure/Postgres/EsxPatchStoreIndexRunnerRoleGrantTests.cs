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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Migration 0091's runner grants (issue #1447), following the
/// <see cref="RetentionSweepRunnerRoleGrantTests"/>/<see cref="RunnerRoleGrantDriftTests"/>
/// convention (this repo's #556 convention: prove both the grant that exists and the
/// operation that must still be denied). <c>waypoint_download_runner</c> gets exactly
/// <c>SELECT, INSERT, UPDATE</c> on <c>esx_patch_store_index</c> and
/// <c>esx_patch_store_discrepancies</c> -- <c>EsxPatchStoreReconciler</c> upserts rows
/// on both and never deletes (proven here as an explicit DELETE denial), and
/// <c>waypoint_compliance_runner</c> gets nothing on either table (least-privilege
/// boundary -- this is a download-domain concern, ADR-0013 §2).
/// </summary>
[Collection("Postgres")]
public sealed class EsxPatchStoreIndexRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public EsxPatchStoreIndexRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		// Same fixed test-role password convention as the other RunnerRoleGrantTests
		// suites: PostgresFixture.CreateRunnerRolesAsync provisions both roles with
		// "waypoint_test".
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

	/// <summary>
	/// The full surface <c>EsxPatchStoreReconciler</c> exercises as the real
	/// download-runner role: SELECT (list previously indexed content keys), INSERT
	/// (a first-seen bundle/discrepancy), and UPDATE (re-seeing a bundle, resolving a
	/// discrepancy) must all succeed without 42501.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CanReconcileEndToEnd_AgainstBothTables()
	{
		string root = Directory.CreateTempSubdirectory("wp-esx-grant-test-").FullName;
		try
		{
			string hostupdateDir = Path.Combine(root, "hostupdate");
			Directory.CreateDirectory(hostupdateDir);
			File.WriteAllText(
				Path.Combine(hostupdateDir, "__hostupdate20-consolidated-index__.xml"),
				"<hostupdate><vendorList><vendor>vmw</vendor></vendorList></hostupdate>");
			string vendorDir = Path.Combine(hostupdateDir, "vmw");
			Directory.CreateDirectory(vendorDir);
			File.WriteAllText(
				Path.Combine(vendorDir, "__hostupdate20-consolidated-metadata-index__.xml"),
				"""
				<metadataList>
					<metadata>
						<productId>ESXi900</productId>
						<version>9.1.0</version>
						<url>metadata-a.zip</url>
						<channelName>vmw-ESXi-9.1</channelName>
					</metadata>
				</metadataList>
				""");
			string zipPath = Path.Combine(vendorDir, "metadata-a.zip");
			using (System.IO.Compression.ZipArchive archive = new(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
			{
				using StreamWriter writer = new(archive.CreateEntry("vibs/vib-0.xml").Open());
				writer.Write("""<vib><relative-path>vib20/esx-update/pkg-a.vib</relative-path><checksum checksum-type="sha-256">aabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbcc</checksum></vib>""");
			}
			// An unreferenced zip so the run also produces an orphan row.
			File.WriteAllBytes(Path.Combine(vendorDir, "metadata-orphan.zip"), [0x50, 0x4B, 0x05, 0x06]);

			EsxPatchStoreReconciler reconciler = new(_downloadRunnerConnectionString, new EsxPatchStoreMetadataParser());

			EsxPatchStoreReconciliationReport report = await reconciler.ReconcileAsync(root, null, CancellationToken.None);

			Assert.True(report.Succeeded);
			Assert.Equal(1, report.IndexedCount);

			// Re-run to exercise the UPDATE/upsert path (bundle re-seen, index row
			// touched rather than re-inserted) as the same runner role.
			EsxPatchStoreReconciliationReport secondReport = await reconciler.ReconcileAsync(root, null, CancellationToken.None);
			Assert.True(secondReport.Succeeded);
			Assert.Equal(1, secondReport.IndexedCount);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	/// <summary>The negative half of the #556 convention: no DELETE grant on either table.</summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteFromIndexOrDiscrepancies()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand deleteIndex = new("DELETE FROM esx_patch_store_index", connection);
		PostgresException deniedIndex = await Assert.ThrowsAsync<PostgresException>(() => deleteIndex.ExecuteNonQueryAsync());
		Assert.Equal("42501", deniedIndex.SqlState);

		await using NpgsqlCommand deleteDiscrepancies = new("DELETE FROM esx_patch_store_discrepancies", connection);
		PostgresException deniedDiscrepancies = await Assert.ThrowsAsync<PostgresException>(() => deleteDiscrepancies.ExecuteNonQueryAsync());
		Assert.Equal("42501", deniedDiscrepancies.SqlState);
	}

	/// <summary>
	/// Least-privilege boundary: the ESX patch-store domain is a download-domain
	/// concern (ADR-0013 §2) -- the compliance-runner role gets no grant at all on
	/// either table.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirelyOnBothTables()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand selectIndex = new("SELECT count(*) FROM esx_patch_store_index", connection);
		PostgresException deniedIndex = await Assert.ThrowsAsync<PostgresException>(() => selectIndex.ExecuteScalarAsync());
		Assert.Equal("42501", deniedIndex.SqlState);

		await using NpgsqlCommand selectDiscrepancies = new("SELECT count(*) FROM esx_patch_store_discrepancies", connection);
		PostgresException deniedDiscrepancies = await Assert.ThrowsAsync<PostgresException>(() => selectDiscrepancies.ExecuteScalarAsync());
		Assert.Equal("42501", deniedDiscrepancies.SqlState);
	}
}
