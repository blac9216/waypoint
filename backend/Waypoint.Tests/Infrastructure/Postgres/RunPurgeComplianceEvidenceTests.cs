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
using Waypoint.Core.Runs;
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #745 remainder (ADR-0019 decision 5, epic #726): real-Postgres coverage that
/// <see cref="RunPurgeService"/>'s database phase (migration 0066) actually deletes
/// migration 0062/0063's evidence rows for a purged run -- closing PR #961's stated
/// "purge currently RESTRICTs" gap -- while leaving an unrelated, unpurged run's own
/// evidence completely untouched, and that the append-only triggers still block a
/// bare DELETE/UPDATE with no purge GUC set. All seeded identities are invented
/// (AGENTS.md) via the same 0050 catalog + scan-plan fixture chain
/// <c>ComponentResultRepositoryTests</c> already established.
/// </summary>
[Collection("Postgres")]
public sealed class RunPurgeComplianceEvidenceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private RunPurgeRepository _purges = null!;
	private AttestationSnapshotRepository _attestationSnapshots = null!;
	private RunRetentionHoldRepository _retentionHolds = null!;
	private RunPurgeService _service = null!;
	private ComponentResultRepository _componentResults = null!;
	private CatalogRepository _catalog = null!;
	private ScanPlanRepository _scanPlans = null!;

	public RunPurgeComplianceEvidenceTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new(
			"""
			TRUNCATE TABLE
				component_result_findings, component_result_artifacts, component_results,
				upload_attempts, run_retention_holds, scan_plan_items, scan_plans, run_scope_snapshots, jobs, runs,
				baselines, content_revisions, components, targets, sites,
				catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await truncate.ExecuteNonQueryAsync();

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_purges = new RunPurgeRepository(_fixture.ConnectionString);
		_attestationSnapshots = new AttestationSnapshotRepository(_fixture.ConnectionString);
		_retentionHolds = new RunRetentionHoldRepository(_fixture.ConnectionString);
		_service = new RunPurgeService(_jobs, _purges, _attestationSnapshots, _retentionHolds, _fixture.ConnectionString, NullLogger<RunPurgeService>.Instance);
		_componentResults = new ComponentResultRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_scanPlans = new ScanPlanRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task PurgeRunAsync_DeletesComponentResultsFindingsArtifactsAndUploadAttempts()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "purge-target");
		Guid jobId = await SeedScanJobAsync(runId, scanPlanItemId);

		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);
		await InsertUploadAttemptAsync(jobId, "uploaded");

		Assert.True(await ComponentResultsExistAsync(runId));
		Assert.True(await ComponentResultFindingsExistAsync(runId));
		Assert.True(await ComponentResultArtifactsExistAsync(runId));
		Assert.True(await UploadAttemptsExistAsync(jobId));

		RunPurgeResult inProgress = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		// Database-phase deletion is synchronous -- visible before the artifact job
		// (which this test never drives to completion) reports anything back.
		Assert.False(await ComponentResultsExistAsync(runId));
		Assert.False(await ComponentResultFindingsExistAsync(runId));
		Assert.False(await ComponentResultArtifactsExistAsync(runId));
		Assert.False(await UploadAttemptsExistAsync(jobId));
	}

	[Fact]
	public async Task PurgeRunAsync_LeavesAnUnrelatedUnpurgedRunsEvidenceUntouched()
	{
		Guid purgedRunId = await SeedRunAsync();
		(Guid purgedComponentId, Guid purgedItemId) = await SeedPlanItemAsync(purgedRunId, "purge-victim");
		Guid purgedJobId = await SeedScanJobAsync(purgedRunId, purgedItemId);
		await _componentResults.RecordAsync(CompletedRecord(purgedRunId, purgedJobId, purgedItemId, purgedComponentId, attempt: 1), CancellationToken.None);
		await InsertUploadAttemptAsync(purgedJobId, "uploaded");

		Guid liveRunId = await SeedRunAsync();
		(Guid liveComponentId, Guid liveItemId) = await SeedPlanItemAsync(liveRunId, "purge-bystander");
		Guid liveJobId = await SeedScanJobAsync(liveRunId, liveItemId);
		await _componentResults.RecordAsync(CompletedRecord(liveRunId, liveJobId, liveItemId, liveComponentId, attempt: 1), CancellationToken.None);
		await InsertUploadAttemptAsync(liveJobId, "uploaded");

		RunPurgeResult inProgress = await _service.PurgeRunAsync(purgedRunId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, inProgress.Outcome);

		Assert.False(await ComponentResultsExistAsync(purgedRunId));
		Assert.False(await UploadAttemptsExistAsync(purgedJobId));

		// The other run's evidence -- never named in this purge call -- must survive
		// intact. This is the "read surface could half-render" risk called out in the
		// task brief: a sibling run's rows must never be collaterally removed.
		Assert.True(await ComponentResultsExistAsync(liveRunId));
		Assert.True(await UploadAttemptsExistAsync(liveJobId));
	}

	/// <summary>
	/// Issue #784 AC3 ("automated retention does not purge any held run or its
	/// dependent records/artifacts"), proven against the REAL schema exactly like
	/// this file's other two purge tests -- component_results, its findings/
	/// artifacts children, AND upload_attempts (the full evidence graph "as one
	/// unit" the issue's Risk section calls out) all survive a purge attempt while a
	/// hold is active, and all four are removed once the hold is lifted -- so the
	/// SAME purge call becomes eligible again with no other state change, matching
	/// <see cref="RunRetentionHoldService.RemoveHoldAsync"/>'s doc comment.
	/// </summary>
	[Fact]
	public async Task PurgeRunAsync_HeldRun_LeavesCompleteEvidenceGraphUntouchedUntilHoldRemoved()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "hold-target");
		Guid jobId = await SeedScanJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);
		await InsertUploadAttemptAsync(jobId, "uploaded");

		bool placed = await _retentionHolds.TryInsertAsync(runId, "invented-audit-hold-reason", "admin-tester", CancellationToken.None);
		Assert.True(placed);

		RunPurgeResult held = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.Held, held.Outcome);

		Assert.True(await ComponentResultsExistAsync(runId));
		Assert.True(await ComponentResultFindingsExistAsync(runId));
		Assert.True(await ComponentResultArtifactsExistAsync(runId));
		Assert.True(await UploadAttemptsExistAsync(jobId));

		bool removed = await _retentionHolds.TryRemoveAsync(runId, "invented-audit-unhold-reason", "admin-tester", CancellationToken.None);
		Assert.True(removed);

		RunPurgeResult resumed = await _service.PurgeRunAsync(runId, "admin-tester", CancellationToken.None);
		Assert.Equal(RunPurgeOutcome.InProgress, resumed.Outcome);

		Assert.False(await ComponentResultsExistAsync(runId));
		Assert.False(await ComponentResultFindingsExistAsync(runId));
		Assert.False(await ComponentResultArtifactsExistAsync(runId));
		Assert.False(await UploadAttemptsExistAsync(jobId));
	}

	[Fact]
	public async Task ComponentResults_BareDeleteWithNoPurgeGuc_StillRaises()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "guard-check");
		Guid jobId = await SeedScanJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM component_results WHERE run_id = $1", connection);
		delete.Parameters.AddWithValue(runId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Contains("append-only", exception.MessageText, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UploadAttempts_BareDeleteWithNoPurgeGuc_StillRaises()
	{
		Guid runId = await SeedRunAsync();
		(_, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "guard-check-upload");
		Guid jobId = await SeedScanJobAsync(runId, scanPlanItemId);
		await InsertUploadAttemptAsync(jobId, "uploaded");

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM upload_attempts WHERE job_id = $1", connection);
		delete.Parameters.AddWithValue(jobId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Contains("append-only", exception.MessageText, StringComparison.Ordinal);
	}

	// -- seeding/reading helpers ---------------------------------------------------

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}', 'completed') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedScanJobAsync(Guid runId, Guid scanPlanItemId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, scan_plan_item_id)
			VALUES ($1, 'scan', 1, 'done', $2) RETURNING id
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(scanPlanItemId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>Full 0050 identity tree + one scan_plans/scan_plan_items row -- mirrors <c>ComponentResultRepositoryTests.SeedPlanItemAsync</c>.</summary>
	private async Task<(Guid ComponentId, Guid ScanPlanItemId)> SeedPlanItemAsync(Guid runId, string suffix)
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

		ScanPlanItem item = new(
			componentId, executionProfile.Id, BaselineId: null, BenchmarkRevisionId: null,
			Transport: CatalogTransports.VMware, SelectorKind: CatalogSelectorKinds.Esxi, SelectorName: null,
			ReportGroupKey: $"group-{suffix}", Priority: 2, OutputKind: CatalogOutputKinds.HdfAndCkl,
			RequiredPurposes: ["vsphere-api"], DeclaredInputNames: ["target_ip"]);

		ScanPlan plan = new(runId, ScanPlanSchema.CurrentVersion, [item], [], $"digest-{suffix}", "1 of 1 accepted");
		IReadOnlyDictionary<Guid, Guid> itemIds = await _scanPlans.RecordAsync(runId, runScopeSnapshotId: null, plan, CancellationToken.None);
		return (componentId, itemIds[componentId]);
	}

	private static ComponentResultRecord CompletedRecord(Guid runId, Guid jobId, Guid scanPlanItemId, Guid componentId, int attempt) =>
		new(
			RunId: runId,
			JobId: jobId,
			ScanPlanItemId: scanPlanItemId,
			ComponentId: componentId,
			AttemptNumber: attempt,
			Status: ComponentResultStatuses.Completed,
			Detail: null,
			Findings:
			[
				new ComponentResultFinding("SV-1", "SV-1r1_rule", "invented title", ComponentFindingSeverities.CatI, ComponentFindingStatuses.Failed, "invented failure evidence"),
			],
			Artifacts: [new ComponentResultArtifact(ComponentResultArtifactKinds.HdfRaw, "invented.json", "deadbeef", 1024)]);

	private async Task InsertUploadAttemptAsync(Guid jobId, string status)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO upload_attempts (job_id, attempt_number, endpoint, collection, status)
			VALUES ($1, 1, 'invented-endpoint.example.internal', 'invented-collection', $2)
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(status);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<bool> ComponentResultsExistAsync(Guid runId) =>
		await ExistsAsync("SELECT EXISTS(SELECT 1 FROM component_results WHERE run_id = $1)", runId);

	private async Task<bool> ComponentResultFindingsExistAsync(Guid runId) =>
		await ExistsAsync(
			"SELECT EXISTS(SELECT 1 FROM component_result_findings f JOIN component_results r ON r.id = f.component_result_id WHERE r.run_id = $1)",
			runId);

	private async Task<bool> ComponentResultArtifactsExistAsync(Guid runId) =>
		await ExistsAsync(
			"SELECT EXISTS(SELECT 1 FROM component_result_artifacts a JOIN component_results r ON r.id = a.component_result_id WHERE r.run_id = $1)",
			runId);

	private async Task<bool> UploadAttemptsExistAsync(Guid jobId) =>
		await ExistsAsync("SELECT EXISTS(SELECT 1 FROM upload_attempts WHERE job_id = $1)", jobId);

	private async Task<bool> ExistsAsync(string sql, Guid id)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(sql, connection);
		command.Parameters.AddWithValue(id);
		return (bool)(await command.ExecuteScalarAsync())!;
	}
}
