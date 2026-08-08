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
