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

	/// <summary>
	/// Issue #1612: <c>UpsertAsync</c>'s SQL uses
	/// <c>COALESCE(EXCLUDED.size_bytes, depot_artifacts.size_bytes)</c> so a NULL
	/// incoming size preserves the stored one -- <c>sha256</c> already has an
	/// overwrite-wins test above, but nothing proved the equivalent direction for
	/// <c>size_bytes</c>: a NON-NULL incoming size must still overwrite a previously
	/// stored one. Reversing the COALESCE arguments (matching
	/// <c>depot_artifacts.size_bytes, EXCLUDED.size_bytes</c>) makes this fail.
	/// </summary>
	[Fact]
	public async Task UpsertAsync_ReUpsertWithNewSizeBytes_OverwritesPreviouslyRecordedSize()
	{
		string relativePath = $"vcf-artifact-{Guid.NewGuid():N}";

		await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "indexed", """{"product":"VCF","version":"9.0"}""", SizeBytes: 123456),
			CancellationToken.None);

		await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "indexed", """{"product":"VCF","version":"9.0"}""", SizeBytes: 999),
			CancellationToken.None);

		(IReadOnlyList<DepotArtifact> items, _) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest(), CancellationToken.None);

		DepotArtifact[] matching = items.Where(item => item.ExternalId == relativePath).ToArray();
		Assert.Single(matching);
		Assert.Equal(999, matching[0].SizeBytes);
	}

	/// <summary>
	/// Issue #1705 regression: the #1503 presence sweep's absent-from-disk result
	/// (<see cref="DepotArtifactStatuses.Missing"/>) used to violate
	/// <c>depot_artifacts_status_check</c> (only 'indexed'/'downloading'/'present'/
	/// 'failed' were allowed pre-migration-0129), aborting the whole
	/// <c>catalog-index</c> job on the first entry a real, partial depot produces.
	/// Proves the real repository's upsert path persists a 'missing' row and that
	/// it reads back through the same <c>GET /catalog/artifacts</c> read API
	/// (<see cref="IDepotArtifactRepository.ListAsync"/>) every other status uses --
	/// not a bespoke seam.
	/// </summary>
	[Fact]
	public async Task UpsertAsync_MissingStatus_PersistsAndReadsBackViaListAsync()
	{
		string externalId = $"vcf-artifact-{Guid.NewGuid():N}";

		Guid id = await _repository.UpsertAsync(
			new DepotArtifactUpsert(externalId, Sha256: null, DepotArtifactStatuses.Missing, """{"product":"VCF","version":"9.1"}"""),
			CancellationToken.None);

		DepotArtifact? byId = await _repository.GetByIdAsync(id, CancellationToken.None);
		Assert.NotNull(byId);
		Assert.Equal(DepotArtifactStatuses.Missing, byId!.Status);

		(IReadOnlyList<DepotArtifact> items, long total) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, DepotArtifactStatuses.Missing), new PageRequest(), CancellationToken.None);

		Assert.Contains(items, item => item.ExternalId == externalId);
		Assert.True(total >= 1);
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

	[Fact]
	public async Task RekeyManyAsync_LegacyRowWithNoCollision_RenamesInPlace()
	{
		string legacyId = $"legacy-{Guid.NewGuid():N}.iso";
		string newId = $"PROD/COMP/VCENTER/{legacyId}";

		await _repository.UpsertAsync(
			new DepotArtifactUpsert(legacyId, "sha-legacy", "indexed", "{}"), CancellationToken.None);

		int reconciled = await _repository.RekeyManyAsync(
			new Dictionary<string, string> { [legacyId] = newId }, CancellationToken.None);

		Assert.Equal(1, reconciled);

		(IReadOnlyList<DepotArtifact> items, long total) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);
		Assert.DoesNotContain(items, item => item.ExternalId == legacyId);
		Assert.Contains(items, item => item.ExternalId == newId && item.Sha256 == "sha-legacy");
		_ = total;
	}

	[Fact]
	public async Task RekeyManyAsync_ZeroCandidatesHaveALegacyRow_ReturnsZeroWithoutTouchingAnything()
	{
		string legacyId = $"legacy-{Guid.NewGuid():N}.iso";
		string newId = $"PROD/COMP/VCENTER/{legacyId}";

		int reconciled = await _repository.RekeyManyAsync(
			new Dictionary<string, string> { [legacyId] = newId }, CancellationToken.None);

		Assert.Equal(0, reconciled);
	}

	/// <summary>
	/// Issue #1804's own acceptance criterion: seeds BOTH a legacy-identity row and a
	/// new-identity row for the same logical artifact (simulating the presence sweep
	/// having already created the new-identity row before this artifact's first
	/// post-#1784 pull) and asserts exactly one row remains afterward -- the legacy
	/// row's still-valid <c>sha256</c> (which the sweep-created row never carries --
	/// the sweep only ever reports presence/status) is folded into the surviving
	/// row, and the legacy row is marked <c>superseded_at</c> rather than deleted
	/// (design #16 section 2's never-auto-remove policy) -- confirmed directly
	/// against the raw column, since <see cref="DepotArtifact"/> does not expose it.
	/// </summary>
	[Fact]
	public async Task RekeyManyAsync_CollisionWhereBothRowsAlreadyExist_MergesAndSupersedesTheLegacyRow()
	{
		string legacyId = $"legacy-{Guid.NewGuid():N}.iso";
		string newId = $"PROD/COMP/VCENTER/{legacyId}";

		Guid legacyRowId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(legacyId, "sha-from-legacy-pull", "indexed", "{}", SizeBytes: 999),
			CancellationToken.None);
		await _repository.UpsertAsync(
			new DepotArtifactUpsert(newId, null, "present", "{}"), CancellationToken.None);

		int reconciled = await _repository.RekeyManyAsync(
			new Dictionary<string, string> { [legacyId] = newId }, CancellationToken.None);

		Assert.Equal(1, reconciled);

		(IReadOnlyList<DepotArtifact> items, long total) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);

		// Exactly one VISIBLE row for this logical artifact -- the legacy row is
		// superseded, not deleted, so ListAsync's superseded_at IS NULL filter is
		// what makes this "exactly one", not the legacy row's absence from the table.
		DepotArtifact survivor = Assert.Single(items.Where(item => item.ExternalId == newId || item.ExternalId == legacyId));
		Assert.Equal(newId, survivor.ExternalId);
		Assert.Equal("present", survivor.Status); // the sweep-created row's own status wins -- never overwritten by the merge.
		Assert.Equal("sha-from-legacy-pull", survivor.Sha256); // folded from the legacy row (COALESCE -- the survivor had none).
		Assert.Equal(999, survivor.SizeBytes); // folded from the legacy row.
		_ = total;

		// The legacy row itself: still present in the table (never deleted), but now
		// marked superseded_at, and GetByIdAsync (an existing-FK-reference lookup,
		// not a listing surface) still resolves it by id.
		DepotArtifact? legacyById = await _repository.GetByIdAsync(legacyRowId, CancellationToken.None);
		Assert.NotNull(legacyById);
		Assert.Equal(legacyId, legacyById!.ExternalId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT superseded_at FROM depot_artifacts WHERE id = $1", connection);
		command.Parameters.AddWithValue(legacyRowId);
		object? supersededAt = await command.ExecuteScalarAsync();
		Assert.NotNull(supersededAt);
	}

	/// <summary>
	/// Issue #1851: pins the edge case its own Motivation section verifies as
	/// unreachable through the application today (<c>superseded_at</c> is only ever
	/// set on a bare-fileName FROM identity, never on a
	/// <c>PROD/COMP/&lt;product&gt;/&lt;fileName&gt;</c> TO identity) but not
	/// guarded against in SQL -- fabricated directly against the table (not via
	/// <see cref="DepotArtifactRepository.RekeyManyAsync"/> or
	/// <see cref="DepotArtifactRepository.UpsertAsync"/>, neither of which can
	/// produce it) since that is the only way to construct it. Before the fix, step
	/// 2's rename guard (no <c>superseded_at</c> filter) saw the fabricated
	/// superseded row and blocked the rename, and step 3's merge (no filter on its
	/// own <c>target</c>) then folded the legacy row's facts into that
	/// already-superseded row and superseded the legacy row too -- two superseded
	/// rows, zero visible, permanently (issue #1851's "Current Behavior"). After the
	/// fix (step 3's <c>target.superseded_at IS NULL</c>), this pair matches
	/// neither step 2 (blocked by the real <c>depot_artifacts_relative_path_key</c>
	/// UNIQUE constraint, which does not exempt superseded rows, regardless of any
	/// SQL-level filter) nor step 3 -- the legacy row is left untouched rather than
	/// corrupted.
	/// </summary>
	[Fact]
	public async Task RekeyManyAsync_ToIdentityRowIsAlreadySuperseded_LeavesTheLegacyRowUntouchedRatherThanCorruptingIt()
	{
		string legacyId = $"legacy-{Guid.NewGuid():N}.iso";
		string newId = $"PROD/COMP/VCENTER/{legacyId}";

		await _repository.UpsertAsync(
			new DepotArtifactUpsert(legacyId, "sha-legacy", "indexed", "{}"), CancellationToken.None);

		await using (NpgsqlConnection fabricate = new(_fixture.ConnectionString))
		{
			await fabricate.OpenAsync();
			await using NpgsqlCommand insertSupersededToRow = new(
				"INSERT INTO depot_artifacts (relative_path, status, superseded_at) VALUES ($1, 'indexed', now())",
				fabricate);
			insertSupersededToRow.Parameters.AddWithValue(newId);
			await insertSupersededToRow.ExecuteNonQueryAsync();
		}

		int reconciled = await _repository.RekeyManyAsync(
			new Dictionary<string, string> { [legacyId] = newId }, CancellationToken.None);

		Assert.Equal(0, reconciled);

		(IReadOnlyList<DepotArtifact> items, long total) = await _repository.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);
		DepotArtifact survivor = Assert.Single(items.Where(item => item.ExternalId == legacyId || item.ExternalId == newId));
		Assert.Equal(legacyId, survivor.ExternalId);
		Assert.Equal("sha-legacy", survivor.Sha256);
		_ = total;
	}

	/// <summary>
	/// Issue #1852's first gap: <c>RekeyManyAsync</c> replaced the per-artifact
	/// <c>RekeyAsync(string, string)</c>, which began with
	/// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> on both its FROM and TO
	/// arguments. The batched shape validated only the dictionary itself, so a
	/// whitespace-only value (a plausible real-world case: a bare-fileName legacy
	/// identity that is itself just whitespace never occurs, but the derived TO
	/// side is string-built by <c>CatalogPullJobHandler</c> and a defect there
	/// could produce one) flowed unvalidated into the batched SQL. Asserts the
	/// restored per-identity check throws. (The check does run before the connection
	/// is opened -- see the method -- but this test asserts only the throw, so it
	/// does not claim to prove the ordering.)
	/// </summary>
	[Fact]
	public async Task RekeyManyAsync_WhitespaceOnlyToIdentity_ThrowsRatherThanFlowingIntoTheBatchedSql()
	{
		string legacyId = $"legacy-{Guid.NewGuid():N}.iso";

		await Assert.ThrowsAsync<ArgumentException>(() => _repository.RekeyManyAsync(
			new Dictionary<string, string> { [legacyId] = "   " }, CancellationToken.None));
	}

	/// <summary>
	/// Round-1 note 1: the sibling test above pinned only the VALUE half of the
	/// per-identity validation issue #1852 asked to restore -- deleting
	/// <c>ThrowIfNullOrWhiteSpace(pair.Key)</c> left the suite green, so half the
	/// restoration was regression-proof and half was not (the recurring
	/// unpinned-guard defect class this session, cf. #1849). This is the missing
	/// mirror: a whitespace-only FROM identity must be refused by the same check
	/// rather than flowing into the batched SQL's <c>text[]</c> parameter, where it
	/// would silently match nothing and make the rekey a confusing no-op.
	/// </summary>
	[Fact]
	public async Task RekeyManyAsync_WhitespaceOnlyFromIdentity_ThrowsRatherThanFlowingIntoTheBatchedSql()
	{
		string newId = $"PROD/COMP/VCENTER/{Guid.NewGuid():N}.iso";

		await Assert.ThrowsAsync<ArgumentException>(() => _repository.RekeyManyAsync(
			new Dictionary<string, string> { ["   "] = newId }, CancellationToken.None));
	}

	/// <summary>
	/// Pins <c>RekeyManyAsync</c>'s repository-boundary backstop: two different FROM
	/// legacy identities renaming onto the SAME TO identity must refuse rather than
	/// reach the batched SQL, where <c>depot_artifacts_relative_path_key</c>'s real
	/// UNIQUE constraint would otherwise let whichever UNNEST row is processed first
	/// win silently.
	///
	/// This is explicitly NOT the shape issue #1852's bare-fileName violation takes
	/// at the real call site, and the earlier version of this comment had it exactly
	/// backwards. <c>CatalogPullJobHandler</c> derives each map KEY from its VALUE
	/// (the value's trailing path segment), so from that caller two distinct keys can
	/// never share a value -- what a genuine violation produces there is two entries
	/// collapsing onto ONE key (<c>PROD/COMP/VCENTER/x.iso</c> and
	/// <c>PROD/COMP/NSX/x.iso</c> both deriving <c>x.iso</c>). That case is enforced
	/// in <c>CatalogPullJobHandler.BuildLegacyRenames</c> and pinned by
	/// <c>CatalogPullJobHandlerTests</c>. The guard exercised here is a backstop for
	/// future callers that build their maps some other way.
	/// </summary>
	[Fact]
	public async Task RekeyManyAsync_TwoDifferentFromIdentitiesTargetTheSameToIdentity_ThrowsRatherThanSilentlyPickingAWinner()
	{
		string firstLegacyId = $"legacy-{Guid.NewGuid():N}.iso";
		string secondLegacyId = $"legacy-{Guid.NewGuid():N}.iso";
		string sharedNewId = $"PROD/COMP/VCENTER/{Guid.NewGuid():N}.iso";

		await Assert.ThrowsAsync<ArgumentException>(() => _repository.RekeyManyAsync(
			new Dictionary<string, string>
			{
				[firstLegacyId] = sharedNewId,
				[secondLegacyId] = sharedNewId,
			}, CancellationToken.None));
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

		string migration0100 = await ReadMigrationSqlAsync("0100_catalog_identity_rekey.sql");

		// Issue #1614: this test mutates the SHARED fixture's schema in place. If
		// anything between the revert and the reapply below throws, a naive version of
		// this test would leave depot_artifacts permanently on the pre-0100 column
		// name, cascading failures into every other test in the Postgres collection.
		// Migration 0100's own guards (IF EXISTS/NOT EXISTS on both the column rename
		// and the constraint rename) make it safe to unconditionally re-run in a
		// finally block regardless of where a failure occurred -- that always restores
		// the forward (post-0100) schema shape, the same guarantee every other
		// migration file's re-application idempotency already provides.
		try
		{
			// Reconstruct the pre-0100 shape: rename BOTH the column and the
			// constraint back, so the two rows below are inserted exactly as
			// CatalogIndexJobHandler/VendorProductVersionCatalogParser would have
			// written them under the OLD external_id identity column, AND migration
			// 0100's second DO $$ block (which renames
			// depot_artifacts_external_id_key -> depot_artifacts_relative_path_key)
			// exercises its real branch instead of always finding the constraint
			// already renamed and taking the no-op path (Postgres does not
			// auto-rename a constraint when its column is renamed).
			await using (NpgsqlCommand revert = new("ALTER TABLE depot_artifacts RENAME COLUMN relative_path TO external_id", connection))
			{
				await revert.ExecuteNonQueryAsync();
			}

			await using (NpgsqlCommand revertConstraint = new(
				"ALTER TABLE depot_artifacts RENAME CONSTRAINT depot_artifacts_relative_path_key TO depot_artifacts_external_id_key", connection))
			{
				await revertConstraint.ExecuteNonQueryAsync();
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

			await using (NpgsqlCommand verifyConstraint = new(
				"SELECT 1 FROM pg_constraint WHERE conname = 'depot_artifacts_relative_path_key'", connection))
			{
				Assert.NotNull(await verifyConstraint.ExecuteScalarAsync());
			}

			// Running the migration a SECOND time (already-migrated state, matching
			// every other migration file's re-application guarantee) must still be a
			// no-op, not an error.
			await using (NpgsqlCommand reapplyAgain = new(migration0100, connection))
			{
				await reapplyAgain.ExecuteNonQueryAsync();
			}
		}
		finally
		{
			await using NpgsqlCommand restore = new(migration0100, connection);
			await restore.ExecuteNonQueryAsync();
		}
	}

	/// <summary>
	/// Issue #1783: <c>bundle_id</c> (migration 0132) round-trips through
	/// upsert/read, and a subsequent upsert that carries no bundle id (e.g. a
	/// <c>DownloadJobHandler</c>/<c>BinariesDownloadJobHandler</c> present/failed
	/// transition, which knows nothing about bundle ids) must not null out a
	/// previously recorded one -- the same <c>COALESCE</c> convention
	/// <c>Sha256</c>/<c>SizeBytes</c> already use.
	/// </summary>
	[Fact]
	public async Task UpsertAsync_BundleId_RoundTripsAndSurvivesAReUpsertWithNullBundleId()
	{
		string relativePath = $"vcf-artifact-{Guid.NewGuid():N}";

		Guid firstId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "indexed", "{}", BundleId: "bundle-abc"),
			CancellationToken.None);

		DepotArtifact? afterFirstUpsert = await _repository.GetByIdAsync(firstId, CancellationToken.None);
		Assert.Equal("bundle-abc", afterFirstUpsert!.BundleId);

		Guid secondId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "present", "{}"),
			CancellationToken.None);

		Assert.Equal(firstId, secondId);
		DepotArtifact? afterSecondUpsert = await _repository.GetByIdAsync(secondId, CancellationToken.None);
		Assert.Equal("bundle-abc", afterSecondUpsert!.BundleId);
		Assert.Equal("present", afterSecondUpsert.Status);
	}

	/// <summary>Issue #1783: a row with no bundle id (the offline disk walk, or a pre-migration-0132 connected pull) reads back null, not an empty string or a throw.</summary>
	[Fact]
	public async Task UpsertAsync_NoBundleId_ReadsBackAsNull()
	{
		string relativePath = $"vcf-artifact-{Guid.NewGuid():N}";

		Guid id = await _repository.UpsertAsync(
			new DepotArtifactUpsert(relativePath, "sha-original", "indexed", "{}"),
			CancellationToken.None);

		DepotArtifact? artifact = await _repository.GetByIdAsync(id, CancellationToken.None);
		Assert.Null(artifact!.BundleId);
	}

	/// <summary>
	/// Issue #797, Finding 1: <see cref="DepotArtifactRepository.SupersedeCatalogDocumentRowAsync"/>
	/// must mark <c>superseded_at</c> on ONLY the stray catalog-document row (NULL product,
	/// NULL version at the catalog-document <c>relative_path</c>) -- never a hard delete, and
	/// never a row at any other path, even another NULL/NULL one. Seeds the stray target, an
	/// unrelated NULL/NULL row at a DIFFERENT path, and a pre-superseded NULL/NULL row at a
	/// THIRD path, then asserts only the target gains <c>superseded_at</c>, the other two are
	/// untouched (the different-path row stays visible; the pre-superseded row keeps its
	/// ORIGINAL timestamp), and the total row count is unchanged. Reverting the method body to
	/// a no-op, or dropping its <c>relative_path = $1</c> targeting, fails this test.
	/// </summary>
	[Fact]
	public async Task SupersedeCatalogDocumentRowAsync_StrayCatalogDocumentRow_SupersedesThatRowOnlyNeverDeletesOrTouchesOthers()
	{
		string catalogDocPath = $"PROD/metadata/productVersionCatalog/v1/productVersionCatalog-{Guid.NewGuid():N}.json";
		string otherPath = $"PROD/COMP/VCENTER/other-{Guid.NewGuid():N}.iso";
		string preSupersededPath = $"PROD/metadata/stale-{Guid.NewGuid():N}.json";

		// The stray catalog-document row: NULL product, NULL version (metadata carries neither).
		await _repository.UpsertAsync(
			new DepotArtifactUpsert(catalogDocPath, "sha-stray", "indexed", "{}"), CancellationToken.None);
		// A DIFFERENT path, also NULL/NULL -- left alone (targeting is by path, not a blanket null filter).
		await _repository.UpsertAsync(
			new DepotArtifactUpsert(otherPath, "sha-other", "indexed", "{}"), CancellationToken.None);
		// An already-superseded NULL/NULL row at a third path -- its original timestamp must not be rewritten.
		Guid preId = await _repository.UpsertAsync(
			new DepotArtifactUpsert(preSupersededPath, "sha-pre", "indexed", "{}"), CancellationToken.None);
		DateTime preSupersededAt;
		await using (NpgsqlConnection seed = new(_fixture.ConnectionString))
		{
			await seed.OpenAsync();
			await using NpgsqlCommand mark = new(
				"UPDATE depot_artifacts SET superseded_at = now() - interval '1 day' WHERE id = $1 RETURNING superseded_at", seed);
			mark.Parameters.AddWithValue(preId);
			preSupersededAt = (DateTime)(await mark.ExecuteScalarAsync())!;
		}

		long before = await CountAllRowsAsync();

		bool superseded = await _repository.SupersedeCatalogDocumentRowAsync(catalogDocPath, CancellationToken.None);

		Assert.True(superseded);
		Assert.NotNull(await SupersededAtForPathAsync(catalogDocPath)); // target marked superseded
		Assert.Null(await SupersededAtForPathAsync(otherPath));         // different path untouched, still visible
		Assert.Equal(preSupersededAt, await SupersededAtForPathAsync(preSupersededPath)); // original timestamp preserved
		Assert.Equal(before, await CountAllRowsAsync());               // supersede-only: no delete
	}

	/// <summary>
	/// Issue #797, Finding 1: the method's <c>AND product IS NULL AND version IS NULL</c> guard
	/// means a LEGITIMATE row at the catalog-document path (real product/version -- the shape a
	/// correct pull could one day write there) is never superseded. The
	/// <c>depot_artifacts_relative_path_key</c> UNIQUE constraint forbids a legit and a stray
	/// row sharing one path at once, so this invariant is pinned as its own fixture rather than
	/// folded into the stray test above. Dropping the product/version predicate from the WHERE
	/// clause supersedes this row and fails this test.
	/// </summary>
	[Fact]
	public async Task SupersedeCatalogDocumentRowAsync_LegitimateRowAtCatalogPath_LeavesItUntouched()
	{
		string catalogDocPath = $"PROD/metadata/productVersionCatalog/v1/productVersionCatalog-{Guid.NewGuid():N}.json";
		await _repository.UpsertAsync(
			new DepotArtifactUpsert(catalogDocPath, "sha-legit", "present", """{"product":"VCENTER","version":"8.0.3"}"""),
			CancellationToken.None);

		bool superseded = await _repository.SupersedeCatalogDocumentRowAsync(catalogDocPath, CancellationToken.None);

		Assert.False(superseded);
		Assert.Null(await SupersededAtForPathAsync(catalogDocPath));
	}

	/// <summary>
	/// Issue #797, Finding 1: the <c>superseded_at IS NULL</c> guard makes a second call a
	/// no-op -- the already-superseded row keeps its ORIGINAL timestamp rather than being
	/// re-stamped, and the method returns false (nothing left to reconcile). Proves the
	/// per-pull retry the handler relies on is idempotent. Dropping the
	/// <c>superseded_at IS NULL</c> predicate makes the second call rewrite the timestamp and
	/// return true, failing this test.
	/// </summary>
	[Fact]
	public async Task SupersedeCatalogDocumentRowAsync_AlreadySuperseded_IsANoOpAndPreservesTheOriginalTimestamp()
	{
		string catalogDocPath = $"PROD/metadata/productVersionCatalog/v1/productVersionCatalog-{Guid.NewGuid():N}.json";
		await _repository.UpsertAsync(
			new DepotArtifactUpsert(catalogDocPath, "sha-stray", "indexed", "{}"), CancellationToken.None);

		Assert.True(await _repository.SupersedeCatalogDocumentRowAsync(catalogDocPath, CancellationToken.None));
		DateTime? first = await SupersededAtForPathAsync(catalogDocPath);
		Assert.NotNull(first);

		Assert.False(await _repository.SupersedeCatalogDocumentRowAsync(catalogDocPath, CancellationToken.None));
		Assert.Equal(first, await SupersededAtForPathAsync(catalogDocPath));
	}

	private async Task<DateTime?> SupersededAtForPathAsync(string relativePath)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT superseded_at FROM depot_artifacts WHERE relative_path = $1", connection);
		command.Parameters.AddWithValue(relativePath);
		object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
		return value is null or DBNull ? null : (DateTime)value;
	}

	private async Task<long> CountAllRowsAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT count(*) FROM depot_artifacts", connection);
		return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
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
