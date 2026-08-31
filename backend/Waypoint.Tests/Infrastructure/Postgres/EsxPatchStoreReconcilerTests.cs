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

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// <see cref="EsxPatchStoreReconciler"/> (issue #1447, epic #1183) against real
/// Postgres and invented, shape-faithful <c>hostupdate/</c> fixtures (same fixture
/// style as <see cref="Waypoint.Tests.Infrastructure.Downloads.EsxPatchStoreMetadataParserTests"/>
/// -- no real vendor bytes anywhere). Covers AC1 (a full "should be" model is built
/// from the parser's own output), AC2 (both missing and orphan discrepancies are
/// recorded as first-class rows, never silently dropped), and AC3 (an orphan is never
/// row-deleted or file-deleted by this reconciler -- only its alert can resolve).
/// </summary>
[Collection("Postgres")]
public sealed class EsxPatchStoreReconcilerTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _root = Directory.CreateTempSubdirectory("wp-esx-patch-store-reconciler-").FullName;
	private EsxPatchStoreReconciler _reconciler = null!;
	private string _hostupdateDir = string.Empty;

	public EsxPatchStoreReconcilerTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();

		_reconciler = new EsxPatchStoreReconciler(_fixture.ConnectionString, new EsxPatchStoreMetadataParser());
		_hostupdateDir = Path.Combine(_root, "hostupdate");
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup; a stray temp dir does not fail the test run.
		}
	}

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand delete = new("DELETE FROM esx_patch_store_discrepancies", connection))
		{
			await delete.ExecuteNonQueryAsync();
		}
		await using (NpgsqlCommand delete = new("DELETE FROM esx_patch_store_index", connection))
		{
			await delete.ExecuteNonQueryAsync();
		}
	}

	// ----- fixture construction (mirrors EsxPatchStoreMetadataParserTests) -----

	private static void WriteConsolidatedIndex(string hostupdateDir, params string[] vendorCodes)
	{
		Directory.CreateDirectory(hostupdateDir);
		string vendorListXml = string.Join(string.Empty, vendorCodes.Select(code => $"<vendor>{code}</vendor>"));
		File.WriteAllText(
			Path.Combine(hostupdateDir, "__hostupdate20-consolidated-index__.xml"),
			$"<hostupdate><vendorList>{vendorListXml}</vendorList></hostupdate>");
	}

	private static string WriteVendorMetadataIndex(string hostupdateDir, string vendorCode, params string[] locations)
	{
		string vendorDir = Path.Combine(hostupdateDir, vendorCode);
		Directory.CreateDirectory(vendorDir);
		string entries = string.Join(string.Empty, locations.Select(location =>
			$"""
			<metadata>
				<productId>ESXi900</productId>
				<version>9.1.0</version>
				<url>{location}</url>
				<channelName>vmw-ESXi-9.1</channelName>
			</metadata>
			"""));
		File.WriteAllText(
			Path.Combine(vendorDir, "__hostupdate20-consolidated-metadata-index__.xml"),
			$"<metadataList>{entries}</metadataList>");
		return vendorDir;
	}

	private static void WriteMetadataZip(string path, string relativePath = "vib20/esx-update/pkg-a.vib", string checksum = "")
	{
		using FileStream fileStream = File.Create(path);
		using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);

		using (StreamWriter writer = new(archive.CreateEntry("vendor-index.xml").Open()))
		{
			writer.Write("<vendorIndex/>");
		}

		using StreamWriter vibWriter = new(archive.CreateEntry("vibs/vib-0.xml").Open());
		vibWriter.Write(
			$"""<vib><relative-path>{relativePath}</relative-path><packed-size>1024</packed-size><checksum checksum-type="sha-256">{(checksum.Length > 0 ? checksum : "aa".PadRight(64, '0'))}</checksum></vib>""");
	}

	private async Task<(int Discrepancies, string? DiscrepancyType, bool Resolved)> ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType type)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT discrepancy_type, resolved_at FROM esx_patch_store_discrepancies WHERE store_root = $1 AND discrepancy_type = $2", connection);
		command.Parameters.AddWithValue(_root);
		command.Parameters.AddWithValue(type == EsxPatchStoreDiscrepancyType.Missing ? "missing" : "orphan");

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		int count = 0;
		string? discType = null;
		bool resolved = false;
		while (await reader.ReadAsync())
		{
			count++;
			discType = reader.GetString(0);
			resolved = !reader.IsDBNull(1);
		}

		return (count, discType, resolved);
	}

	private async Task<int> CountIndexRowsAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM esx_patch_store_index WHERE store_root = $1", connection);
		command.Parameters.AddWithValue(_root);
		return (int)(long)(await command.ExecuteScalarAsync())!;
	}

	// ----- AC1: full "should be" model ------------------------------------------

	[Fact]
	public async Task ReconcileAsync_StoreWithOneBundle_IndexesItAndReportsNoDiscrepancies()
	{
		WriteConsolidatedIndex(_hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(_hostupdateDir, "vmw", "metadata-a.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a.zip"));

		EsxPatchStoreReconciliationReport report = await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);

		Assert.True(report.Succeeded);
		Assert.Equal(1, report.IndexedCount);
		Assert.Equal(0, report.NewMissingCount);
		Assert.Equal(0, report.NewOrphanCount);
		Assert.Equal(1, await CountIndexRowsAsync());
	}

	[Fact]
	public async Task ReconcileAsync_StoreRootDoesNotExist_ReturnsFailedReportRatherThanThrowing()
	{
		string missingRoot = Path.Combine(_root, "does-not-exist");

		EsxPatchStoreReconciliationReport report = await _reconciler.ReconcileAsync(missingRoot, null, CancellationToken.None);

		Assert.False(report.Succeeded);
		Assert.NotNull(report.FailureReason);
		Assert.Equal(0, report.IndexedCount);
	}

	// ----- AC2/AC3: missing -------------------------------------------------------

	[Fact]
	public async Task ReconcileAsync_PreviouslyIndexedZipNoLongerOnDisk_RecordsMissingDiscrepancy_WithoutDeletingIndexRow()
	{
		WriteConsolidatedIndex(_hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(_hostupdateDir, "vmw", "metadata-a.zip");
		string zipPath = Path.Combine(vendorDir, "metadata-a.zip");
		WriteMetadataZip(zipPath);

		await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);
		Assert.Equal(1, await CountIndexRowsAsync());

		// The vendor's index still names the zip, but the file itself is gone --
		// the download went away without the metadata catching up (the parser
		// reports this as a warning, not a bundle).
		File.Delete(zipPath);

		EsxPatchStoreReconciliationReport secondReport = await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);

		Assert.True(secondReport.Succeeded);
		Assert.Equal(0, secondReport.IndexedCount);
		Assert.Equal(1, secondReport.NewMissingCount);

		// AC3 / no-silent-drop: the index row this store already had survives --
		// this reconciler never row-deletes it.
		Assert.Equal(1, await CountIndexRowsAsync());

		(int count, string? type, bool resolved) = await ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType.Missing);
		Assert.Equal(1, count);
		Assert.Equal("missing", type);
		Assert.False(resolved);
	}

	[Fact]
	public async Task ReconcileAsync_MissingBundleReappears_ResolvesTheDiscrepancy()
	{
		WriteConsolidatedIndex(_hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(_hostupdateDir, "vmw", "metadata-a.zip");
		string zipPath = Path.Combine(vendorDir, "metadata-a.zip");
		WriteMetadataZip(zipPath);
		await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);

		File.Delete(zipPath);
		await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);
		(int _, string? _, bool resolvedBeforeReappear) = await ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType.Missing);
		Assert.False(resolvedBeforeReappear);

		// Byte-identical content re-arrives (e.g. via transfer, per the issue's Risks
		// note treating "orphan/missing today" as provisional).
		WriteMetadataZip(zipPath);

		EsxPatchStoreReconciliationReport thirdReport = await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);

		Assert.Equal(1, thirdReport.ResolvedCount);
		(int _, string? _, bool resolvedAfterReappear) = await ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType.Missing);
		Assert.True(resolvedAfterReappear);
	}

	// ----- AC2/AC3: orphan ---------------------------------------------------------

	[Fact]
	public async Task ReconcileAsync_ZipPresentButUnreferencedByVendorIndex_RecordsOrphanDiscrepancy()
	{
		WriteConsolidatedIndex(_hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(_hostupdateDir, "vmw", "metadata-a.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a.zip"));

		// Present on disk, but no <metadata> entry in the vendor's consolidated
		// index names it -- the parser never opens it, so it is never a bundle.
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-orphan.zip"));

		EsxPatchStoreReconciliationReport report = await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);

		Assert.Equal(1, report.IndexedCount);
		Assert.Equal(1, report.NewOrphanCount);

		(int count, string? type, bool resolved) = await ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType.Orphan);
		Assert.Equal(1, count);
		Assert.Equal("orphan", type);
		Assert.False(resolved);

		// Disk content is never touched by the reconciler (AC3): the orphan zip
		// this test wrote is still exactly where it was written.
		Assert.True(File.Exists(Path.Combine(vendorDir, "metadata-orphan.zip")));
	}

	[Fact]
	public async Task ReconcileAsync_OrphanLaterReferencedByVendorIndex_ResolvesTheDiscrepancy()
	{
		WriteConsolidatedIndex(_hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(_hostupdateDir, "vmw", "metadata-a.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a.zip"));
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-b.zip"));

		await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);
		(int _, string? _, bool resolvedBefore) = await ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType.Orphan);
		Assert.False(resolvedBefore);

		// A later sync's vendor index now names the previously-orphaned zip too.
		WriteVendorMetadataIndex(_hostupdateDir, "vmw", "metadata-a.zip", "metadata-b.zip");

		EsxPatchStoreReconciliationReport report = await _reconciler.ReconcileAsync(_root, null, CancellationToken.None);

		Assert.Equal(1, report.ResolvedCount);
		Assert.Equal(0, report.NewOrphanCount);
		(int _, string? _, bool resolvedAfter) = await ReadFirstDiscrepancyAsync(EsxPatchStoreDiscrepancyType.Orphan);
		Assert.True(resolvedAfter);
	}
}
