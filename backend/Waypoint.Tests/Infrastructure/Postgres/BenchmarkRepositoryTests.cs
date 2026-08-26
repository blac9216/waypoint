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
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #730 (epic #726, Wave 1) PR 1: immutable, digest-addressed XCCDF/STIG
/// benchmark revisions and rules, plus the component-to-benchmark-revision mapping and
/// its versioned audit history (migration 0052), against a real PostgreSQL 16
/// container. Every fixture is INVENTED, shaped like public DISA STIG XCCDF structure
/// only (AGENTS.md/CLAUDE.md sanitization policy) -- never real STIG content or lab
/// data.
///
/// Covers every issue #730 AC this PR delivers:
/// <list type="bullet">
/// <item><description>Multiple revisions of the same benchmark coexist and are digest-addressed.</description></item>
/// <item><description>Exact component mappings (not target-kind inference).</description></item>
/// <item><description>Coverage/ambiguity are queryable via mapping status.</description></item>
/// <item><description>SRG "no published benchmark" is explicit, never inferred by name.</description></item>
/// <item><description>Mapping changes are versioned/audited (superseded, not overwritten).</description></item>
/// </list>
/// </summary>
[Collection("Postgres")]
public sealed class BenchmarkRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private BenchmarkRepository _repository = null!;
	private CatalogRepository _catalogRepository = null!;

	public BenchmarkRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_repository = new BenchmarkRepository(_fixture.ConnectionString);
		_catalogRepository = new CatalogRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				benchmark_component_mappings, benchmark_rules, benchmark_revisions,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private static BenchmarkImportCandidate InventedCandidate(string benchmarkKey = "xccdf_invented.example_benchmark_EX-1-0_STIG", string ruleTitle = "r1") =>
		new(
			benchmarkKey,
			"Invented Example STIG",
			"2",
			"3",
			ComputeInventedDigest(benchmarkKey, ruleTitle),
			[new XccdfRule("SV-000001r1_rule", "V-000001", BenchmarkRuleSeverities.High, ruleTitle)]);

	private static string ComputeInventedDigest(string benchmarkKey, string ruleTitle) =>
		Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(benchmarkKey + ruleTitle))).ToLowerInvariant();

	private async Task<Guid> SeedCatalogComponentAsync(string componentKey = "vcenter")
	{
		CatalogSourceRevision sourceRevision = await _catalogRepository.UpsertSourceRevisionAsync($"test-revision-{Guid.NewGuid():N}", "invented fixture revision", CancellationToken.None);
		CatalogProduct product = await _catalogRepository.UpsertProductAsync(sourceRevision.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _catalogRepository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogComponent component = await _catalogRepository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition(componentKey, "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		return component.Id;
	}

	[Fact]
	public async Task ImportRevisionAsync_NewContent_PersistsRevisionAndRules()
	{
		BenchmarkImportCandidate candidate = InventedCandidate();

		BenchmarkRevision revision = await _repository.ImportRevisionAsync(candidate, BenchmarkSources.ManualUpload, CancellationToken.None);

		Assert.Equal(candidate.BenchmarkKey, revision.BenchmarkKey);
		Assert.Equal(candidate.ContentDigest, revision.ContentDigest);
		Assert.Equal(1, revision.RuleCount);
		Assert.Equal(BenchmarkLifecycleStates.Staged, revision.LifecycleState);

		IReadOnlyList<BenchmarkRule> rules = await _repository.ListRulesAsync(revision.Id, CancellationToken.None);
		BenchmarkRule rule = Assert.Single(rules);
		Assert.Equal("SV-000001r1_rule", rule.RuleId);
		Assert.Equal("V-000001", rule.VulnId);
		Assert.Equal(BenchmarkRuleSeverities.High, rule.Severity);
	}

	[Fact]
	public async Task ImportRevisionAsync_ByteIdenticalContent_IsIdempotentByDigest()
	{
		BenchmarkImportCandidate candidate = InventedCandidate();

		BenchmarkRevision first = await _repository.ImportRevisionAsync(candidate, BenchmarkSources.ManualUpload, CancellationToken.None);
		BenchmarkRevision second = await _repository.ImportRevisionAsync(candidate, BenchmarkSources.StigManager, CancellationToken.None);

		Assert.Equal(first.Id, second.Id);

		IReadOnlyList<BenchmarkRevision> allRevisions = await _repository.ListRevisionsByBenchmarkKeyAsync(candidate.BenchmarkKey, CancellationToken.None);
		Assert.Single(allRevisions);
	}

	[Fact]
	public async Task ImportRevisionAsync_DifferentContent_SameBenchmarkKey_CreatesCoexistingRevision()
	{
		BenchmarkImportCandidate revisionOne = InventedCandidate(ruleTitle: "original rule text");
		BenchmarkImportCandidate revisionTwo = InventedCandidate(ruleTitle: "genuinely changed rule text");

		BenchmarkRevision first = await _repository.ImportRevisionAsync(revisionOne, BenchmarkSources.ManualUpload, CancellationToken.None);
		BenchmarkRevision second = await _repository.ImportRevisionAsync(revisionTwo, BenchmarkSources.ManualUpload, CancellationToken.None);

		Assert.NotEqual(first.Id, second.Id);
		Assert.NotEqual(first.ContentDigest, second.ContentDigest);

		IReadOnlyList<BenchmarkRevision> allRevisions = await _repository.ListRevisionsByBenchmarkKeyAsync(revisionOne.BenchmarkKey, CancellationToken.None);
		Assert.Equal(2, allRevisions.Count);
		Assert.Contains(allRevisions, r => r.Id == first.Id);
		Assert.Contains(allRevisions, r => r.Id == second.Id);
	}

	[Fact]
	public async Task ImportRevisionAsync_InvalidSource_ThrowsActionableArgumentException()
	{
		BenchmarkImportCandidate candidate = InventedCandidate();

		ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
			() => _repository.ImportRevisionAsync(candidate, "disa-direct-download", CancellationToken.None));
		Assert.Contains("not in the closed benchmark vocabulary", ex.Message);
	}

	[Fact]
	public async Task GetRevisionAsync_UnknownId_ReturnsNull()
	{
		Assert.Null(await _repository.GetRevisionAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task ListBenchmarkKeysAsync_ReturnsDistinctKeysOrdinalOrdered()
	{
		await _repository.ImportRevisionAsync(InventedCandidate("zzz-benchmark", "a"), BenchmarkSources.ManualUpload, CancellationToken.None);
		await _repository.ImportRevisionAsync(InventedCandidate("aaa-benchmark", "b"), BenchmarkSources.ManualUpload, CancellationToken.None);
		await _repository.ImportRevisionAsync(InventedCandidate("aaa-benchmark", "c"), BenchmarkSources.ManualUpload, CancellationToken.None);

		IReadOnlyList<string> keys = await _repository.ListBenchmarkKeysAsync(CancellationToken.None);

		Assert.Equal(["aaa-benchmark", "zzz-benchmark"], keys);
	}

	[Fact]
	public async Task SetMappingAsync_ExactMatch_RecordsMappedStatus()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		BenchmarkRevision revision = await _repository.ImportRevisionAsync(InventedCandidate(), BenchmarkSources.ManualUpload, CancellationToken.None);

		BenchmarkComponentMapping mapping = await _repository.SetMappingAsync(
			componentId, revision.Id, BenchmarkMappingStatuses.Mapped, isSrgNoBenchmark: false, isAdminOverride: false, ambiguousCandidateCount: 0, reason: "exact benchmark_key match", actor: "system", CancellationToken.None);

		Assert.Equal(BenchmarkMappingStatuses.Mapped, mapping.Status);
		Assert.Equal(revision.Id, mapping.BenchmarkRevisionId);
		Assert.True(mapping.IsCurrent);
		Assert.False(mapping.IsAdminOverride);

		BenchmarkComponentMapping? current = await _repository.GetCurrentMappingAsync(componentId, CancellationToken.None);
		Assert.NotNull(current);
		Assert.Equal(mapping.Id, current!.Id);
	}

	[Fact]
	public async Task SetMappingAsync_Ambiguous_RecordsCandidateCountWithoutARevision()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		BenchmarkComponentMapping mapping = await _repository.SetMappingAsync(
			componentId, benchmarkRevisionId: null, BenchmarkMappingStatuses.Ambiguous, isSrgNoBenchmark: false, isAdminOverride: false, ambiguousCandidateCount: 3, reason: "3 benchmark revisions share this component's product version", actor: null, CancellationToken.None);

		Assert.Equal(BenchmarkMappingStatuses.Ambiguous, mapping.Status);
		Assert.Null(mapping.BenchmarkRevisionId);
		Assert.Equal(3, mapping.AmbiguousCandidateCount);
	}

	[Fact]
	public async Task SetMappingAsync_Unmapped_IsQueryableRatherThanAbsent()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		await _repository.SetMappingAsync(
			componentId, benchmarkRevisionId: null, BenchmarkMappingStatuses.Unmapped, isSrgNoBenchmark: false, isAdminOverride: false, ambiguousCandidateCount: 0, reason: "no candidate found", actor: null, CancellationToken.None);

		IReadOnlyList<BenchmarkComponentMapping> allCurrent = await _repository.ListCurrentMappingsAsync(CancellationToken.None);
		BenchmarkComponentMapping mapping = Assert.Single(allCurrent);
		Assert.Equal(BenchmarkMappingStatuses.Unmapped, mapping.Status);
	}

	[Fact]
	public async Task SetMappingAsync_SrgComponent_RecordsExplicitNoPublishedBenchmark()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		BenchmarkComponentMapping mapping = await _repository.SetMappingAsync(
			componentId, benchmarkRevisionId: null, BenchmarkMappingStatuses.Unmapped, isSrgNoBenchmark: true, isAdminOverride: false, ambiguousCandidateCount: 0, reason: "SRG content has no published DISA benchmark", actor: null, CancellationToken.None);

		Assert.True(mapping.IsSrgNoBenchmark);
		Assert.Null(mapping.BenchmarkRevisionId);
	}

	[Fact]
	public async Task SetMappingAsync_SrgFlagWithRevision_IsRejectedAsMutuallyExclusive()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		BenchmarkRevision revision = await _repository.ImportRevisionAsync(InventedCandidate(), BenchmarkSources.ManualUpload, CancellationToken.None);

		await Assert.ThrowsAsync<ArgumentException>(() => _repository.SetMappingAsync(
			componentId, revision.Id, BenchmarkMappingStatuses.Mapped, isSrgNoBenchmark: true, isAdminOverride: false, ambiguousCandidateCount: 0, reason: null, actor: null, CancellationToken.None));
	}

	[Fact]
	public async Task SetMappingAsync_MappedStatusWithoutRevision_IsRejected()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		await Assert.ThrowsAsync<ArgumentException>(() => _repository.SetMappingAsync(
			componentId, benchmarkRevisionId: null, BenchmarkMappingStatuses.Mapped, isSrgNoBenchmark: false, isAdminOverride: false, ambiguousCandidateCount: 0, reason: null, actor: null, CancellationToken.None));
	}

	[Fact]
	public async Task SetMappingAsync_AdminOverride_SupersedesPriorMappingAndKeepsAuditHistory()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		BenchmarkRevision revisionOne = await _repository.ImportRevisionAsync(InventedCandidate(ruleTitle: "first"), BenchmarkSources.ManualUpload, CancellationToken.None);
		BenchmarkRevision revisionTwo = await _repository.ImportRevisionAsync(InventedCandidate(ruleTitle: "second"), BenchmarkSources.ManualUpload, CancellationToken.None);

		BenchmarkComponentMapping systemSuggested = await _repository.SetMappingAsync(
			componentId, revisionOne.Id, BenchmarkMappingStatuses.Suggested, isSrgNoBenchmark: false, isAdminOverride: false, ambiguousCandidateCount: 0, reason: "system suggestion", actor: null, CancellationToken.None);

		BenchmarkComponentMapping adminOverride = await _repository.SetMappingAsync(
			componentId, revisionTwo.Id, BenchmarkMappingStatuses.Mapped, isSrgNoBenchmark: false, isAdminOverride: true, ambiguousCandidateCount: 0, reason: "admin confirmed the correct revision", actor: "admin@example.internal", CancellationToken.None);

		BenchmarkComponentMapping? current = await _repository.GetCurrentMappingAsync(componentId, CancellationToken.None);
		Assert.NotNull(current);
		Assert.Equal(adminOverride.Id, current!.Id);
		Assert.True(current.IsAdminOverride);
		Assert.Equal("admin@example.internal", current.Actor);

		IReadOnlyList<BenchmarkComponentMapping> history = await _repository.GetMappingHistoryAsync(componentId, CancellationToken.None);
		Assert.Equal(2, history.Count);
		Assert.Contains(history, m => m.Id == systemSuggested.Id && !m.IsCurrent);
		Assert.Contains(history, m => m.Id == adminOverride.Id && m.IsCurrent);
	}

	[Fact]
	public async Task ListCurrentMappingsAsync_OnlyReturnsOneCurrentRowPerComponent()
	{
		Guid componentA = await SeedCatalogComponentAsync("vcenter");
		Guid componentB = await SeedCatalogComponentAsync("esxi");

		await _repository.SetMappingAsync(componentA, null, BenchmarkMappingStatuses.Unmapped, false, false, 0, null, null, CancellationToken.None);
		await _repository.SetMappingAsync(componentA, null, BenchmarkMappingStatuses.Ambiguous, false, false, 2, "re-evaluated", null, CancellationToken.None);
		await _repository.SetMappingAsync(componentB, null, BenchmarkMappingStatuses.Unmapped, false, false, 0, null, null, CancellationToken.None);

		IReadOnlyList<BenchmarkComponentMapping> current = await _repository.ListCurrentMappingsAsync(CancellationToken.None);

		Assert.Equal(2, current.Count);
		Assert.Single(current, m => m.CatalogComponentId == componentA && m.Status == BenchmarkMappingStatuses.Ambiguous);
		Assert.Single(current, m => m.CatalogComponentId == componentB && m.Status == BenchmarkMappingStatuses.Unmapped);
	}

	[Fact]
	public async Task GetCurrentMappingAsync_ComponentNeverMapped_ReturnsNull()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		Assert.Null(await _repository.GetCurrentMappingAsync(componentId, CancellationToken.None));
	}
}
