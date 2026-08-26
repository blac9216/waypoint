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
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #734 (migration 0057), against a real PostgreSQL 16 container:
/// <c>scan_plans</c>/<c>scan_plan_items</c> round-trip the frozen accepted-item plan
/// and its skip list. One row per run (UNIQUE run_id), ON DELETE CASCADE off
/// <c>runs</c>; every plan-item FK is RESTRICT, proven here by attempting to delete a
/// referenced catalog execution profile while a plan item still references it.
/// </summary>
[Collection("Postgres")]
public sealed class ScanPlanRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ScanPlanRepository _repository = null!;
	private CatalogRepository _catalog = null!;

	public ScanPlanRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				scan_plan_items, scan_plans, run_scope_snapshots, jobs, runs,
				baselines, content_revisions, components, targets, sites,
				catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();

		_repository = new ScanPlanRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>Full 0050 identity tree down to one execution profile, plus one seeded component under a fresh target -- everything a plan item's FKs need.</summary>
	private async Task<(Guid ComponentId, Guid ExecutionProfileId)> SeedComponentAndProfileAsync(string suffix)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid siteId;
		await using (NpgsqlCommand site = new("INSERT INTO sites (name) VALUES ($1) RETURNING id", connection))
		{
			site.Parameters.AddWithValue($"site-{suffix}");
			siteId = (Guid)(await site.ExecuteScalarAsync())!;
		}

		Guid targetId;
		await using (NpgsqlCommand target = new(
			"INSERT INTO targets (site_id, kind, name, connection) VALUES ($1, 'vsphere', $2, '{}'::jsonb) RETURNING id", connection))
		{
			target.Parameters.AddWithValue(siteId);
			target.Parameters.AddWithValue($"target-{suffix}");
			targetId = (Guid)(await target.ExecuteScalarAsync())!;
		}

		Guid componentId;
		await using (NpgsqlCommand component = new(
			"""
			INSERT INTO components (parent_target_id, catalog_component_key, vendor_identity, display_name, lifecycle)
			VALUES ($1, 'esxi', $2, $2, 'active') RETURNING id
			""", connection))
		{
			component.Parameters.AddWithValue(targetId);
			component.Parameters.AddWithValue($"host-{suffix}");
			componentId = (Guid)(await component.ExecuteScalarAsync())!;
		}

		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", $"vsphere-{suffix}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition($"esxi-{suffix}", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Srg, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 2, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		return (componentId, executionProfile.Id);
	}

	[Fact]
	public async Task RecordAsync_ThenGetForRunAsync_RoundTripsAcceptedItemsAndSkips()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid executionProfileId) = await SeedComponentAndProfileAsync("round-trip");
		Guid skippedComponentId = Guid.NewGuid();

		ScanPlanItem item = new(
			componentId, executionProfileId, BaselineId: null, BenchmarkRevisionId: null,
			Transport: CatalogTransports.VMware, SelectorKind: CatalogSelectorKinds.Esxi, SelectorName: null,
			ReportGroupKey: "group-round-trip", Priority: 2, OutputKind: CatalogOutputKinds.HdfAndCkl,
			RequiredPurposes: ["vsphere-api"], DeclaredInputNames: ["target_ip"]);
		ScanPlanSkip skip = new(skippedComponentId, ScanPlanSkipReasons.NoActiveBaseline, "no active baseline");
		ScanPlan plan = new(runId, ScanPlanSchema.CurrentVersion, [item], [skip], "digest-abc", "1 of 2 accepted; 1 skipped");

		await _repository.RecordAsync(runId, runScopeSnapshotId: null, plan, CancellationToken.None);

		ScanPlan? roundTripped = await _repository.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(roundTripped);
		Assert.Equal(ScanPlanSchema.CurrentVersion, roundTripped!.PlanSchemaVersion);
		Assert.Equal("digest-abc", roundTripped.PlanDigest);
		Assert.Equal("1 of 2 accepted; 1 skipped", roundTripped.Explanation);

		ScanPlanItem roundTrippedItem = Assert.Single(roundTripped.Items);
		Assert.Equal(componentId, roundTrippedItem.ComponentId);
		Assert.Equal(executionProfileId, roundTrippedItem.CatalogExecutionProfileId);
		Assert.Null(roundTrippedItem.BaselineId);
		Assert.Equal(["vsphere-api"], roundTrippedItem.RequiredPurposes);
		Assert.Equal(["target_ip"], roundTrippedItem.DeclaredInputNames);

		ScanPlanSkip roundTrippedSkip = Assert.Single(roundTripped.Skips);
		Assert.Equal(skippedComponentId, roundTrippedSkip.ComponentId);
		Assert.Equal(ScanPlanSkipReasons.NoActiveBaseline, roundTrippedSkip.Reason);
	}

	[Fact]
	public async Task GetForRunAsync_NoPlanRecorded_ReturnsNull()
	{
		Guid runId = await SeedRunAsync();

		Assert.Null(await _repository.GetForRunAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task RunDeleted_CascadesThePlanAndItsItems()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid executionProfileId) = await SeedComponentAndProfileAsync("cascade");
		ScanPlanItem item = new(
			componentId, executionProfileId, null, null, CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null,
			"group-cascade", 2, CatalogOutputKinds.HdfAndCkl, [], []);
		await _repository.RecordAsync(runId, null, new ScanPlan(runId, ScanPlanSchema.CurrentVersion, [item], [], "digest-cascade", "1 of 1 accepted"), CancellationToken.None);

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand delete = new("DELETE FROM runs WHERE id = $1", connection);
			delete.Parameters.AddWithValue(runId);
			await delete.ExecuteNonQueryAsync();
		}

		Assert.Null(await _repository.GetForRunAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task ReferencedCatalogExecutionProfile_CannotBeDeletedWhileAPlanItemReferencesIt()
	{
		// ADR-0023 "Later ... cannot rewrite them" made concrete: scan_plan_items'
		// catalog_execution_profile_id FK is RESTRICT, not CASCADE.
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid executionProfileId) = await SeedComponentAndProfileAsync("restrict");
		ScanPlanItem item = new(
			componentId, executionProfileId, null, null, CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null,
			"group-restrict", 2, CatalogOutputKinds.HdfAndCkl, [], []);
		await _repository.RecordAsync(runId, null, new ScanPlan(runId, ScanPlanSchema.CurrentVersion, [item], [], "digest-restrict", "1 of 1 accepted"), CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM catalog_execution_profiles WHERE id = $1", connection);
		delete.Parameters.AddWithValue(executionProfileId);

		await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
	}
}
