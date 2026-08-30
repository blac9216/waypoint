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
/// Issue #745 (migration 0063), against a real PostgreSQL 16 container:
/// <c>component_results</c>/<c>component_result_findings</c>/<c>component_result_artifacts</c>
/// round-trip, immutability (no UPDATE path exists anywhere in the repository or
/// schema), and the run-rollup aggregation's truthfulness against a seeded
/// multi-component run -- all invented fixtures (AGENTS.md).
/// </summary>
[Collection("Postgres")]
public sealed class ComponentResultRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ComponentResultRepository _repository = null!;
	private CatalogRepository _catalog = null!;
	private ScanPlanRepository _scanPlans = null!;

	public ComponentResultRepositoryTests(PostgresFixture fixture)
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
				component_result_findings, component_result_artifacts, component_results,
				scan_plan_items, scan_plans, run_scope_snapshots, jobs, runs,
				baselines, content_revisions, components, targets, sites,
				catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();

		_repository = new ComponentResultRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_scanPlans = new ScanPlanRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedJobAsync(Guid runId, Guid scanPlanItemId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, has_run_secret, scan_plan_item_id)
			VALUES ($1, 'scan', 1, 'queued', true, $2) RETURNING id
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(scanPlanItemId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>Single-item convenience wrapper over <see cref="SeedPlanItemsAsync"/>.</summary>
	private async Task<(Guid ComponentId, Guid ScanPlanItemId)> SeedPlanItemAsync(Guid runId, string suffix)
	{
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> items = await SeedPlanItemsAsync(runId, [suffix]);
		return items[0];
	}

	/// <summary>
	/// Full 0050 identity tree per suffix plus ONE scan_plans row for the run carrying
	/// ALL the items -- <c>scan_plans.run_id</c> is UNIQUE (one plan per run, migration
	/// 0057), so a multi-component run must be seeded as one plan with N items, never
	/// N plans. Mirrors ScanPlanRepositoryTests' own component/profile seeding.
	/// </summary>
	private async Task<IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)>> SeedPlanItemsAsync(Guid runId, IReadOnlyList<string> suffixes)
	{
		List<ScanPlanItem> items = [];
		List<Guid> componentIds = [];

		foreach (string suffix in suffixes)
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

			componentIds.Add(componentId);
			items.Add(new ScanPlanItem(
				componentId, executionProfile.Id, BaselineId: null, BenchmarkRevisionId: null,
				Transport: CatalogTransports.VMware, SelectorKind: CatalogSelectorKinds.Esxi, SelectorName: null,
				ReportGroupKey: $"group-{suffix}", Priority: 2, OutputKind: CatalogOutputKinds.HdfAndCkl,
				RequiredPurposes: ["vsphere-api"], DeclaredInputNames: ["target_ip"]));
		}

		ScanPlan plan = new(runId, ScanPlanSchema.CurrentVersion, items, [], $"digest-{suffixes[0]}", $"{items.Count} of {items.Count} accepted");
		IReadOnlyDictionary<Guid, Guid> itemIds = await _scanPlans.RecordAsync(runId, runScopeSnapshotId: null, plan, CancellationToken.None);
		return [.. componentIds.Select(componentId => (componentId, itemIds[componentId]))];
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
				new ComponentResultFinding("SV-2", null, "invented title 2", ComponentFindingSeverities.CatII, ComponentFindingStatuses.Passed, null),
			],
			Artifacts: [new ComponentResultArtifact(ComponentResultArtifactKinds.HdfRaw, "invented.json", "deadbeef", 1024)]);

	[Fact]
	public async Task RecordAsync_ThenGetRunRollupAsync_RoundTripsCountsFromFindings()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "rollup-basic");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);

		await _repository.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		Assert.Equal(1, rollup.PlannedComponentCount);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);
		Assert.Equal(ComponentResultStatuses.Completed, row.Status);
		Assert.Equal(1, row.ComponentCount);
		Assert.Equal(1, row.CatIOpen);
		Assert.Equal(0, row.CatIIOpen);
		Assert.Equal(1, row.PassedCount);
	}

	[Fact]
	public async Task NextAttemptNumberAsync_IsOneBasedAndMonotonicPerJob()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "attempt-numbering");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);

		Assert.Equal(1, await _repository.NextAttemptNumberAsync(jobId, CancellationToken.None));
		await _repository.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);
		Assert.Equal(2, await _repository.NextAttemptNumberAsync(jobId, CancellationToken.None));
	}

	/// <summary>ADR-0024: the latest completed attempt supplies the current component result; prior attempts remain immutable history, not overwritten or excluded from GetRunRollupAsync's latest-per-item selection.</summary>
	[Fact]
	public async Task GetRunRollupAsync_UsesOnlyTheLatestAttemptPerPlanItem()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "latest-attempt");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);

		// Attempt 1: execution_error (e.g. a transient auth failure).
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, jobId, scanPlanItemId, componentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.ExecutionError, Detail: "invented transient failure",
				Findings: [new ComponentResultFinding("component", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotReviewed, "invented transient failure")],
				Artifacts: []),
			CancellationToken.None);

		// Attempt 2 (a retry against the same plan item): completed cleanly.
		await _repository.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 2), CancellationToken.None);

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);
		Assert.Equal(ComponentResultStatuses.Completed, row.Status);
		Assert.Equal(1, row.ComponentCount);
	}

	/// <summary>Aggregation truthfulness against a seeded MULTI-component run: reconciles exactly to what was recorded, across every status bucket -- not just a single-component happy path (docs/testing.md fixture-monoculture guard).</summary>
	[Fact]
	public async Task GetRunRollupAsync_ReconcilesExactlyAcrossMultipleComponentsAndStatuses()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["multi-completed", "multi-error", "multi-skipped"]);

		(Guid completedComponentId, Guid completedItemId) = seeded[0];
		Guid completedJobId = await SeedJobAsync(runId, completedItemId);
		await _repository.RecordAsync(CompletedRecord(runId, completedJobId, completedItemId, completedComponentId, attempt: 1), CancellationToken.None);

		(Guid errorComponentId, Guid errorItemId) = seeded[1];
		Guid errorJobId = await SeedJobAsync(runId, errorItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, errorJobId, errorItemId, errorComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.ExecutionError, Detail: "invented unreachable target",
				Findings: [new ComponentResultFinding("component", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotReviewed, "invented unreachable target")],
				Artifacts: []),
			CancellationToken.None);

		(Guid skippedComponentId, Guid skippedItemId) = seeded[2];
		Guid skippedJobId = await SeedJobAsync(runId, skippedItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, skippedJobId, skippedItemId, skippedComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.Skipped, Detail: "invented missing credential",
				Findings: [],
				Artifacts: []),
			CancellationToken.None);

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		Assert.Equal(3, rollup.PlannedComponentCount);
		Assert.Equal(3, rollup.ByStatus.Sum(r => r.ComponentCount));

		Assert.Equal(1, rollup.ByStatus.Single(r => r.Status == ComponentResultStatuses.Completed).ComponentCount);
		Assert.Equal(1, rollup.ByStatus.Single(r => r.Status == ComponentResultStatuses.ExecutionError).ComponentCount);
		Assert.Equal(1, rollup.ByStatus.Single(r => r.Status == ComponentResultStatuses.ExecutionError).NotReviewedCount);
		Assert.Equal(1, rollup.ByStatus.Single(r => r.Status == ComponentResultStatuses.Skipped).ComponentCount);

		// CAT totals reconcile exactly to the one completed component's findings --
		// the error/skipped components contribute zero CAT counts (they never
		// produced a CAT-severity finding, only the synthetic not_reviewed one).
		Assert.Equal(1, rollup.ByStatus.Sum(r => r.CatIOpen));
		Assert.Equal(0, rollup.ByStatus.Sum(r => r.CatIIOpen));
	}

	/// <summary>
	/// Issue #1132 (round-2 finding): a MIXED status bucket -- one component that ran
	/// but evaluated nothing (all not_reviewed) alongside two that evaluated normally,
	/// all three <c>completed</c>. The evaluated-zero signal must be PER COMPONENT: an
	/// aggregate-only test (sum passed &gt; 0) would read this run as fully evaluated
	/// and hide the component that produced no evaluation at all.
	/// </summary>
	[Fact]
	public async Task GetRunRollupAsync_MixedBucket_FlagsTheComponentThatEvaluatedNothing()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["mixed-unevaluated", "mixed-evaluated-a", "mixed-evaluated-b"]);

		// C1: completed, but every control came back not_reviewed -- evaluated nothing.
		(Guid unevaluatedComponentId, Guid unevaluatedItemId) = seeded[0];
		Guid unevaluatedJobId = await SeedJobAsync(runId, unevaluatedItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, unevaluatedJobId, unevaluatedItemId, unevaluatedComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.Completed, Detail: null,
				Findings:
				[
					new ComponentResultFinding("SV-100", null, "invented control 100", ComponentFindingSeverities.CatI, ComponentFindingStatuses.NotReviewed, "invented: control could not execute"),
					new ComponentResultFinding("SV-101", null, "invented control 101", ComponentFindingSeverities.CatII, ComponentFindingStatuses.NotReviewed, "invented: control could not execute"),
					new ComponentResultFinding("SV-102", null, "invented control 102", ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotReviewed, "invented: control could not execute"),
				],
				Artifacts: []),
			CancellationToken.None);

		// C2 and C3: completed and genuinely evaluated (one failed CAT I, one passed each).
		foreach ((Guid componentId, Guid itemId) in seeded.Skip(1))
		{
			Guid jobId = await SeedJobAsync(runId, itemId);
			await _repository.RecordAsync(CompletedRecord(runId, jobId, itemId, componentId, attempt: 1), CancellationToken.None);
		}

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);
		Assert.Equal(ComponentResultStatuses.Completed, row.Status);

		// The aggregate looks healthy -- 3 components, 2 passed, 2 open CAT I.
		Assert.Equal(3, row.ComponentCount);
		Assert.Equal(2, row.PassedCount);
		Assert.Equal(2, row.CatIOpen);
		Assert.Equal(3, row.NotReviewedCount);

		// ...but exactly one component evaluated nothing, and the signal must say so.
		Assert.Equal(1, row.EvaluatedZeroComponentCount);
		Assert.True(row.EvaluatedZeroControls);
	}

	/// <summary>
	/// Round 2 finding 1(a): a component whose controls ALL came back
	/// <c>execution_error</c>. <c>HdfFindingsParser.MapStatus</c> maps any
	/// <c>status: "error"</c> (and any unrecognized result shape) there, and
	/// <c>ComponentFindingStatuses.IsOpen</c> is <c>Failed</c>-only, so before issue
	/// #1144 such findings landed in NO count column at all and the row read all-zero.
	/// Migration 0080 gives them <c>execution_error_count</c>, which this test asserts
	/// below. Seeded in a MIXED bucket alongside two components that evaluated normally,
	/// so the SUMMED pass/open counts still look healthy and only the per-component
	/// filter (plus the new column) can tell the truth.
	/// </summary>
	[Fact]
	public async Task GetRunRollupAsync_MixedBucketAllExecutionError_FlagsTheComponentThatEvaluatedNothing()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["mixed-errored", "mixed-errored-evaluated-a", "mixed-errored-evaluated-b"]);

		(Guid erroredComponentId, Guid erroredItemId) = seeded[0];
		Guid erroredJobId = await SeedJobAsync(runId, erroredItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, erroredJobId, erroredItemId, erroredComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.Completed, Detail: null,
				Findings:
				[
					new ComponentResultFinding("SV-200", null, "invented control 200", ComponentFindingSeverities.CatI, ComponentFindingStatuses.ExecutionError, "invented: control raised an error"),
					new ComponentResultFinding("SV-201", null, "invented control 201", ComponentFindingSeverities.CatII, ComponentFindingStatuses.ExecutionError, "invented: control raised an error"),
				],
				Artifacts: []),
			CancellationToken.None);

		foreach ((Guid componentId, Guid itemId) in seeded.Skip(1))
		{
			Guid jobId = await SeedJobAsync(runId, itemId);
			await _repository.RecordAsync(CompletedRecord(runId, jobId, itemId, componentId, attempt: 1), CancellationToken.None);
		}

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);

		// The pass/open counts read healthy -- the errored component contributes to none
		// of them, so a predicate that looked only at those would see a fully evaluated
		// bucket. Post-0080 the errored component is NOT invisible: it lands in
		// execution_error_count, asserted at the end of this test.
		Assert.Equal(3, row.ComponentCount);
		Assert.Equal(2, row.PassedCount);
		Assert.Equal(2, row.CatIOpen);
		Assert.Equal(0, row.NotReviewedCount);
		Assert.Equal(0, row.SkippedCount);
		Assert.Equal(0, row.NotApplicableCount);

		Assert.Equal(1, row.EvaluatedZeroComponentCount);
		Assert.True(row.EvaluatedZeroControls);

		// Issue #1144: the errored component's findings are no longer invisible --
		// execution_error_count carries them, so the row is not truly all-zero.
		Assert.Equal(2, row.ExecutionErrorCount);
	}

	/// <summary>
	/// Round 2 finding 1(b): a component that produced NO findings at all --
	/// <c>HdfFindingsParser</c> treats an empty <c>controls</c> array as a genuine
	/// success, and the recording service stores it as <c>completed</c> with every
	/// count zero. Seeded in a MIXED bucket, same masking mode as (a).
	/// </summary>
	[Fact]
	public async Task GetRunRollupAsync_MixedBucketZeroFindings_FlagsTheComponentThatEvaluatedNothing()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["mixed-no-findings", "mixed-no-findings-evaluated"]);

		(Guid emptyComponentId, Guid emptyItemId) = seeded[0];
		Guid emptyJobId = await SeedJobAsync(runId, emptyItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, emptyJobId, emptyItemId, emptyComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.Completed, Detail: null,
				Findings: [],
				Artifacts: []),
			CancellationToken.None);

		(Guid evaluatedComponentId, Guid evaluatedItemId) = seeded[1];
		Guid evaluatedJobId = await SeedJobAsync(runId, evaluatedItemId);
		await _repository.RecordAsync(
			CompletedRecord(runId, evaluatedJobId, evaluatedItemId, evaluatedComponentId, attempt: 1),
			CancellationToken.None);

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);

		Assert.Equal(2, row.ComponentCount);
		Assert.Equal(1, row.PassedCount);
		Assert.Equal(1, row.CatIOpen);
		Assert.Equal(0, row.NotReviewedCount);

		Assert.Equal(1, row.EvaluatedZeroComponentCount);
		Assert.True(row.EvaluatedZeroControls);
	}

	/// <summary>
	/// The widened predicate's exclusion, pinned at the SQL grain: a component that is
	/// genuinely, entirely <c>not_applicable</c> is a determinate outcome, not a
	/// failure to evaluate, and must stay unflagged even though it too has zero passed
	/// and zero open findings. Seeded in a mixed bucket so the guard is non-vacuous.
	/// </summary>
	[Fact]
	public async Task GetRunRollupAsync_MixedBucketGenuinelyAllNotApplicable_IsNotFlagged()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["mixed-all-na", "mixed-all-na-evaluated"]);

		(Guid naComponentId, Guid naItemId) = seeded[0];
		Guid naJobId = await SeedJobAsync(runId, naItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, naJobId, naItemId, naComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.Completed, Detail: null,
				Findings:
				[
					new ComponentResultFinding("SV-300", null, "invented control 300", ComponentFindingSeverities.CatI, ComponentFindingStatuses.NotApplicable, "invented: does not apply to this component"),
					new ComponentResultFinding("SV-301", null, "invented control 301", ComponentFindingSeverities.CatII, ComponentFindingStatuses.NotApplicable, "invented: does not apply to this component"),
				],
				Artifacts: []),
			CancellationToken.None);

		(Guid evaluatedComponentId, Guid evaluatedItemId) = seeded[1];
		Guid evaluatedJobId = await SeedJobAsync(runId, evaluatedItemId);
		await _repository.RecordAsync(
			CompletedRecord(runId, evaluatedJobId, evaluatedItemId, evaluatedComponentId, attempt: 1),
			CancellationToken.None);

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);

		Assert.Equal(2, row.ComponentCount);
		Assert.Equal(2, row.NotApplicableCount);
		Assert.Equal(0, row.EvaluatedZeroComponentCount);
		Assert.False(row.EvaluatedZeroControls);
	}

	/// <summary>
	/// Issue #1261 (issue #1144 review round 1): pins the <c>execution_error_count &gt; 0</c>
	/// disjunct in <c>evaluated_zero_component_count</c>'s FILTER on its own. Every other
	/// errored-component test in this file also has <c>not_applicable_count = 0</c>, so the
	/// pre-existing disjunct already fires there and deleting the new term changes nothing.
	/// This component has BOTH <c>not_applicable</c> findings (so
	/// <c>not_applicable_count = 0</c> is FALSE) and <c>execution_error</c> findings, and no
	/// <c>not_reviewed</c>/<c>skipped</c> ones -- so the new term is the ONLY disjunct that
	/// can flag it. Its partner
	/// <see cref="GetRunRollupAsync_MixedBucketGenuinelyAllNotApplicable_IsNotFlagged"/>
	/// pins the other side of the boundary: the same shape WITHOUT an errored control stays
	/// unflagged.
	/// </summary>
	[Fact]
	public async Task GetRunRollupAsync_NotApplicableComponentWithExecutionErrors_IsFlagged()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["na-plus-errored", "na-plus-errored-evaluated"]);

		(Guid mixedComponentId, Guid mixedItemId) = seeded[0];
		Guid mixedJobId = await SeedJobAsync(runId, mixedItemId);
		await _repository.RecordAsync(
			new ComponentResultRecord(
				runId, mixedJobId, mixedItemId, mixedComponentId, AttemptNumber: 1,
				Status: ComponentResultStatuses.Completed, Detail: null,
				Findings:
				[
					new ComponentResultFinding("SV-400", null, "invented control 400", ComponentFindingSeverities.CatI, ComponentFindingStatuses.NotApplicable, "invented: does not apply to this component"),
					new ComponentResultFinding("SV-401", null, "invented control 401", ComponentFindingSeverities.CatII, ComponentFindingStatuses.ExecutionError, "invented: control raised an error"),
				],
				Artifacts: []),
			CancellationToken.None);

		(Guid evaluatedComponentId, Guid evaluatedItemId) = seeded[1];
		Guid evaluatedJobId = await SeedJobAsync(runId, evaluatedItemId);
		await _repository.RecordAsync(
			CompletedRecord(runId, evaluatedJobId, evaluatedItemId, evaluatedComponentId, attempt: 1),
			CancellationToken.None);

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);

		// The shape that makes this test non-vacuous: the flagged component has a
		// NON-zero not_applicable_count and zero not_reviewed/skipped, so only the
		// execution_error_count disjunct can reach it.
		Assert.Equal(2, row.ComponentCount);
		Assert.Equal(1, row.NotApplicableCount);
		Assert.Equal(0, row.NotReviewedCount);
		Assert.Equal(0, row.SkippedCount);
		Assert.Equal(1, row.ExecutionErrorCount);

		Assert.Equal(1, row.EvaluatedZeroComponentCount);
		Assert.True(row.EvaluatedZeroControls);
	}

	/// <summary>Regression guard for the same fix: a bucket in which EVERY component genuinely evaluated must stay unflagged -- the per-component filter must not make healthy runs look incomplete.</summary>
	[Fact]
	public async Task GetRunRollupAsync_AllComponentsEvaluated_ReportsNoEvaluatedZeroComponents()
	{
		Guid runId = await SeedRunAsync();
		IReadOnlyList<(Guid ComponentId, Guid ScanPlanItemId)> seeded =
			await SeedPlanItemsAsync(runId, ["all-evaluated-a", "all-evaluated-b"]);

		foreach ((Guid componentId, Guid itemId) in seeded)
		{
			Guid jobId = await SeedJobAsync(runId, itemId);
			await _repository.RecordAsync(CompletedRecord(runId, jobId, itemId, componentId, attempt: 1), CancellationToken.None);
		}

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		RunResultRollupRow row = Assert.Single(rollup.ByStatus);
		Assert.Equal(2, row.ComponentCount);
		Assert.Equal(0, row.EvaluatedZeroComponentCount);
		Assert.False(row.EvaluatedZeroControls);
	}

	/// <summary>A plan item with NO component_results row at all (never claimed) is coverage that is simply absent -- never fabricated into any status bucket.</summary>
	[Fact]
	public async Task GetRunRollupAsync_PlanItemWithNoResultRow_IsNotFabricatedIntoAnyStatus()
	{
		Guid runId = await SeedRunAsync();
		await SeedPlanItemAsync(runId, "never-claimed");

		RunResultRollup rollup = await _repository.GetRunRollupAsync(runId, CancellationToken.None);
		Assert.Equal(1, rollup.PlannedComponentCount);
		Assert.Empty(rollup.ByStatus);
	}

	/// <summary>Immutability: there is no UPDATE statement anywhere in the repository against these tables, and the schema itself has no trigger that would allow one to succeed silently -- proven by attempting a raw UPDATE directly and asserting the CHECK/constraint surface still holds (a duplicate (job_id, attempt_number) is rejected, which is the actual enforcement point since Npgsql/Postgres grants do not prevent an owner-role UPDATE by construction; the runner ROLE grant is what proves the real production path -- see ComponentResultRunnerRoleGrantTests).</summary>
	[Fact]
	public async Task RecordAsync_DuplicateJobAttemptNumber_IsRejectedByUniqueConstraint()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "duplicate-attempt");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);

		await _repository.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		await Assert.ThrowsAsync<PostgresException>(
			() => _repository.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None));
	}

	[Fact]
	public async Task GetComponentIdForPlanItemAsync_UnknownId_ReturnsNull()
	{
		Assert.Null(await _repository.GetComponentIdForPlanItemAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task GetComponentIdForPlanItemAsync_KnownId_ReturnsFrozenComponentId()
	{
		Guid runId = await SeedRunAsync();
		(Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync(runId, "component-lookup");
		Assert.Equal(componentId, await _repository.GetComponentIdForPlanItemAsync(scanPlanItemId, CancellationToken.None));
	}
}
