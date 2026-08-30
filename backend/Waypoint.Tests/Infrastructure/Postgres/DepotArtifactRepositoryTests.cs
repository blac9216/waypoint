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

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Pagination;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #193 (epic #9 slice 1) against real Postgres: idempotent upsert by
/// <c>external_id</c> (the acceptance criterion -- same identity twice yields one row
/// with the newer payload), and filtered/paginated listing including the migration
/// 0007 generated <c>product</c>/<c>version</c> columns derived from the JSONB
/// metadata (ADR-0002 -- vendor shapes stay JSONB, only these two are promoted for
/// query). Fixtures below are entirely invented (CLAUDE.md sanitization rules) --
/// no real depot data, hostnames, or tokens.
/// </summary>
[Collection("Postgres")]
public sealed class DepotArtifactRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private DepotArtifactRepository _repository = null!;

	public DepotArtifactRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetCatalogDataAsync();
		_repository = new DepotArtifactRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task UpsertAsync_SameExternalIdTwice_YieldsOneRowWithTheNewerPayload()
	{
		string externalId = $"vcf-artifact-{Guid.NewGuid():N}";

		Guid firstId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(externalId, "sha-original", "indexed", """{"product":"VCF","version":"9.0","size":1024}"""),
			CancellationToken.None);

		Guid secondId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(externalId, "sha-updated", "present", """{"product":"VCF","version":"9.0","size":2048}"""),
			CancellationToken.None);

		Assert.Equal(firstId, secondId);

		(IReadOnlyList<DepotArtifact> items, long total) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest(), CancellationToken.None);

		DepotArtifact[] matching = items.Where(item => item.ExternalId == externalId).ToArray();
		Assert.Single(matching);
		Assert.Equal("sha-updated", matching[0].Sha256);
		Assert.Equal("present", matching[0].Status);
		Assert.Contains("2048", matching[0].MetadataJson, StringComparison.Ordinal);

		_ = total;
	}

	[Fact]
	public async Task ListAsync_FiltersByProductVersionStatus_AndPaginatesWithXTotalCount()
	{
		string tag = Guid.NewGuid().ToString("N");
		await SeedAsync($"{tag}-a", "indexed", "VCF", "9.0");
		await SeedAsync($"{tag}-b", "indexed", "VCF", "9.1");
		await SeedAsync($"{tag}-c", "present", "VCF", "9.0");
		await SeedAsync($"{tag}-d", "indexed", "NSX", "4.2");

		(IReadOnlyList<DepotArtifact> vcfItems, long vcfTotal) = await _repository.ListAsync(
			new DepotArtifactFilter("VCF", null, null), new PageRequest(), CancellationToken.None);
		Assert.Equal(3, vcfItems.Count(item => item.ExternalId.StartsWith(tag, StringComparison.Ordinal)));
		Assert.True(vcfTotal >= 3);

		(IReadOnlyList<DepotArtifact> vcf90Items, long vcf90Total) = await _repository.ListAsync(
			new DepotArtifactFilter("VCF", "9.0", null), new PageRequest(), CancellationToken.None);
		DepotArtifact[] tagged = vcf90Items.Where(item => item.ExternalId.StartsWith(tag, StringComparison.Ordinal)).ToArray();
		Assert.Equal(2, tagged.Length);
		Assert.All(tagged, item => Assert.Equal("9.0", item.Version));

		(IReadOnlyList<DepotArtifact> vcf90PresentItems, _) = await _repository.ListAsync(
			new DepotArtifactFilter("VCF", "9.0", "present"), new PageRequest(), CancellationToken.None);
		DepotArtifact[] presentTagged = vcf90PresentItems.Where(item => item.ExternalId.StartsWith(tag, StringComparison.Ordinal)).ToArray();
		Assert.Single(presentTagged);
		Assert.Equal($"{tag}-c", presentTagged[0].ExternalId);

		// Pagination: limit=1 offset=1 against the 3 VCF rows returns exactly one,
		// and the header-bound total still reflects the *filtered* count (3), not
		// the page size or the whole table.
		(IReadOnlyList<DepotArtifact> page, long pagedTotal) = await _repository.ListAsync(
			new DepotArtifactFilter("VCF", null, null), new PageRequest { Limit = 1, Offset = 0 }, CancellationToken.None);
		Assert.Single(page);
		Assert.Equal(vcfTotal, pagedTotal);
		_ = vcf90Total;
	}

	/// <summary>
	/// Issue #1593 review, finding 1: a completed/failed download upsert
	/// (<c>DownloadJobHandler</c>) carries no <c>SizeBytes</c> -- it only knows
	/// status/sha256 -- so it must not silently null out the size the catalog write
	/// path (<c>VendorProductVersionCatalogParser</c>/<c>CatalogIndexJobHandler</c>)
	/// already recorded for the same <c>relative_path</c>. Reproduces the exact
	/// shape of that bug: seed with a size (catalog indexing), re-upsert with
	/// <c>SizeBytes: null</c> (download-handler-style "present" transition), and
	/// assert the previously-recorded size survives.
	/// </summary>
	[Fact]
	public async Task UpsertAsync_ReUpsertWithNullSizeBytes_DoesNotClobberPreviouslyRecordedSize()
	{
		string relativePath = $"vcf-artifact-{Guid.NewGuid():N}";

		Guid firstId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "indexed", """{"product":"VCF","version":"9.0"}""", SizeBytes: 123456),
			CancellationToken.None);

		// DownloadJobHandler-style upsert on download completion: no SizeBytes known
		// at this call site (it forwards only Sha256/Status/MetadataJson), matching
		// DownloadJobHandler.cs's `new DepotArtifactUpsert(artifact.ExternalId,
		// artifact.Sha256, "present", artifact.MetadataJson)` call shape exactly.
		Guid secondId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "present", """{"product":"VCF","version":"9.0"}"""),
			CancellationToken.None);

		Assert.Equal(firstId, secondId);

		(IReadOnlyList<DepotArtifact> items, _) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest(), CancellationToken.None);

		DepotArtifact matching = Assert.Single(items.Where(item => item.ExternalId == relativePath));
		Assert.Equal("present", matching.Status);
		Assert.Equal(123456, matching.SizeBytes);
		Assert.Equal("sha-original", matching.Sha256);
	}

	/// <summary>
	/// Same clobber class as the size test above, for <c>sha256</c>: a null incoming
	/// hash must not erase a previously recorded one. No known real caller upserts a
	/// null <c>Sha256</c> over a non-null one today, but the SQL guarantee should not
	/// depend on that staying true.
	/// </summary>
	[Fact]
	public async Task UpsertAsync_ReUpsertWithNullSha256_DoesNotClobberPreviouslyRecordedSha256()
	{
		string relativePath = $"vcf-artifact-{Guid.NewGuid():N}";

		await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "indexed", """{"product":"VCF","version":"9.0"}"""),
			CancellationToken.None);

		await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, null, "present", """{"product":"VCF","version":"9.0"}"""),
			CancellationToken.None);

		(IReadOnlyList<DepotArtifact> items, _) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest(), CancellationToken.None);

		DepotArtifact matching = Assert.Single(items.Where(item => item.ExternalId == relativePath));
		Assert.Equal("sha-original", matching.Sha256);
	}

	/// <summary>
	/// Issue #1488 acceptance criterion: migration 0100's <c>external_id</c> -&gt;
	/// <c>relative_path</c> rename must run cleanly against a fixture carrying
	/// pre-existing <c>depot_artifacts</c> rows from BOTH legacy namespaces --
	/// <c>CatalogIndexJobHandler</c>'s offline disk-walk relative path and
	/// <c>VendorProductVersionCatalogParser</c>'s connected-pull bare filename
	/// (issue #687) -- without dropping or merging either row. Reverts the fixture's
	/// already-migrated schema to the pre-0100 column name to reconstruct that
	/// legacy state (same "revert, seed, reapply" idiom
	/// <c>SchemaMigrationTests.Migration0080_...</c> uses), inserts one row per
	/// namespace, reapplies migration 0100's own idempotent SQL, then asserts both
	/// rows survive under the new column with their original identity strings
	/// untouched.
	/// </summary>
	[Fact]
	public async Task Migration0100_PreExistingRowsFromBothLegacyNamespaces_SurviveTheRekey()
	{
		string nestedLegacyPath = $"COMP/VCENTER/legacy-disk-walk-{Guid.NewGuid():N}.iso";
		string bareLegacyFilename = $"legacy-connected-pull-{Guid.NewGuid():N}.iso";

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Reconstruct the pre-0100 shape: rename the column back so the two rows
		// below are inserted exactly as CatalogIndexJobHandler/VendorProductVersionCatalogParser
		// would have written them under the OLD external_id identity column.
		await using (NpgsqlCommand revert = new("ALTER TABLE depot_artifacts RENAME COLUMN relative_path TO external_id", connection))
		{
			await revert.ExecuteNonQueryAsync();
		}

		await using (NpgsqlCommand seed = new(
			"""
			INSERT INTO depot_artifacts (external_id, status, metadata) VALUES
				($1, 'indexed', '{}'::jsonb),
				($2, 'indexed', '{}'::jsonb)
			""", connection))
		{
			seed.Parameters.AddWithValue(nestedLegacyPath);
			seed.Parameters.AddWithValue(bareLegacyFilename);
			await seed.ExecuteNonQueryAsync();
		}

		string migration0100 = await ReadMigrationSqlAsync("0100_catalog_identity_rekey.sql");
		await using (NpgsqlCommand reapply = new(migration0100, connection))
		{
			await reapply.ExecuteNonQueryAsync();
		}

		await using (NpgsqlCommand verify = new(
			"SELECT relative_path FROM depot_artifacts WHERE relative_path = ANY($1)", connection))
		{
			verify.Parameters.AddWithValue(new[] { nestedLegacyPath, bareLegacyFilename });
			await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
			HashSet<string> found = [];
			while (await reader.ReadAsync())
			{
				found.Add(reader.GetString(0));
			}

			Assert.Equal(2, found.Count);
			Assert.Contains(nestedLegacyPath, found);
			Assert.Contains(bareLegacyFilename, found);
		}

		// Running the migration a SECOND time (already-migrated state, matching
		// every other migration file's re-application guarantee) must still be a
		// no-op, not an error.
		await using (NpgsqlCommand reapplyAgain = new(migration0100, connection))
		{
			await reapplyAgain.ExecuteNonQueryAsync();
		}
	}

	private static async Task<string> ReadMigrationSqlAsync(string fileName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith(fileName, StringComparison.Ordinal)));
		await using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync();
	}

	private async Task SeedAsync(string externalId, string status, string product, string version)
	{
		await _repository.UpsertAsync(
			new DepotArtifactUpsert(externalId, $"sha-{externalId}", status, $$"""{"product":"{{product}}","version":"{{version}}"}"""),
			CancellationToken.None);
	}

	private async Task ResetCatalogDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
