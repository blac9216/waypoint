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
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #757 (epic #726 §7, ADR-0024's 10,000+-job scale contract): proves
/// <see cref="JobQueueRepository"/>'s <see cref="IComponentJobRepository"/> half --
/// server-side grouped counts and cursor-paged/filtered/searchable component-job
/// rows -- against a seeded four-figure job set on real Postgres. This is
/// deliberately a SQL-level correctness test, not a DOM/render test: it proves the
/// `GROUP BY` counts reconcile to persisted rows and the keyset pager visits every
/// row across pages exactly once, which is the actual scale-sensitive logic; the
/// frontend virtualization windowing has its own unit tests (liverun.tsx / the
/// virtualized list component) that assert render-window logic without instantiating
/// 10,000 DOM nodes.
/// </summary>
[Collection("Postgres")]
public sealed class ComponentJobQueryRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public ComponentJobQueryRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private static readonly (short Priority, string State)[] StateMatrix =
	[
		(1, JobStates.Queued), (1, JobStates.Running), (1, JobStates.Done),
		(2, JobStates.Queued), (2, JobStates.Failed),
		(3, JobStates.Done), (3, JobStates.Cancelled),
		(4, JobStates.Queued),
		(5, JobStates.Running),
		(6, JobStates.Queued), (6, JobStates.Done),
	];

	/// <summary>
	/// Seeds a run with 4,400 component jobs spread across every (priority, state) pair
	/// in <see cref="StateMatrix"/> (400 each) -- a representative four-figure job set,
	/// per the issue's "seeded 4-figure job sets" AC. Every third job additionally gets
	/// a distinctive <c>target_name</c> so search coverage has real needles.
	/// </summary>
	private async Task<Guid> SeedRunWithJobsAsync(int perCell = 400)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid runId;
		await using (NpgsqlCommand insertRun = new(
			"INSERT INTO runs (run_type, state) VALUES ('scan', 'running') RETURNING id", connection))
		{
			runId = (Guid)(await insertRun.ExecuteScalarAsync())!;
		}

		int counter = 0;
		foreach ((short priority, string state) in StateMatrix)
		{
			for (int i = 0; i < perCell; i++)
			{
				counter++;
				string targetName = counter % 50 == 0 ? $"needle-host-{counter:D5}" : $"host-{counter:D5}";
				// migration 0002/0015: running/attesting/converting rows must carry a
				// lease (jobs_running_requires_lease_check) -- stamp one on those states
				// so the seed matches what the real claim path would have written.
				bool leased = state is JobStates.Running or JobStates.Attesting or JobStates.Converting;
				await using NpgsqlCommand insertJob = new(
					"""
					INSERT INTO jobs (run_id, job_type, target_name, priority, state, claimed_by, claimed_at, lease_expires_at)
					VALUES ($1, 'scan', $2, $3, $4,
						CASE WHEN $5 THEN 'seed-worker' END,
						CASE WHEN $5 THEN now() END,
						CASE WHEN $5 THEN now() + interval '5 minutes' END)
					""", connection);
				insertJob.Parameters.AddWithValue(runId);
				insertJob.Parameters.AddWithValue(targetName);
				insertJob.Parameters.AddWithValue(priority);
				insertJob.Parameters.AddWithValue(state);
				insertJob.Parameters.AddWithValue(leased);
				await insertJob.ExecuteNonQueryAsync();
			}
		}

		return runId;
	}

	private static readonly ComponentJobFilter NoFilter = new(null, null, null, null);

	[Fact]
	public async Task GetGroupedCountsAsync_ReconcilesExactlyToPersistedJobs()
	{
		const int perCell = 400;
		Guid runId = await SeedRunWithJobsAsync(perCell);

		IReadOnlyList<ComponentJobCountRow> rows = await _repository.GetGroupedCountsAsync(runId, NoFilter, CancellationToken.None);

		// Every (priority, state) cell in the matrix reports back exactly perCell, and
		// no extra cells materialize (component_kind is uniformly "unknown" here since
		// none of these jobs carry a scan_plan_item_id).
		Assert.Equal(StateMatrix.Length, rows.Count);
		foreach ((short priority, string state) in StateMatrix)
		{
			ComponentJobCountRow row = Assert.Single(rows, r => r.Priority == priority && r.State == state);
			Assert.Equal(ComponentKindVocabulary.Unknown, row.ComponentKind);
			Assert.Equal(perCell, row.Count);
		}

		long total = rows.Sum(r => r.Count);
		Assert.Equal(StateMatrix.Length * (long)perCell, total);
	}

	[Fact]
	public async Task GetGroupedCountsAsync_FiltersByStateAndPriority()
	{
		const int perCell = 400;
		Guid runId = await SeedRunWithJobsAsync(perCell);

		ComponentJobFilter filter = new(States: [JobStates.Queued], Priorities: null, ComponentKinds: null, Search: null);
		IReadOnlyList<ComponentJobCountRow> queuedOnly = await _repository.GetGroupedCountsAsync(runId, filter, CancellationToken.None);

		int expectedQueuedCells = StateMatrix.Count(x => x.State == JobStates.Queued);
		Assert.Equal(expectedQueuedCells, queuedOnly.Count);
        Assert.All(queuedOnly, r => Assert.Equal(JobStates.Queued, r.State));

		ComponentJobFilter priorityFilter = new(States: null, Priorities: [1, 2], ComponentKinds: null, Search: null);
		IReadOnlyList<ComponentJobCountRow> priorityRows = await _repository.GetGroupedCountsAsync(runId, priorityFilter, CancellationToken.None);
		Assert.All(priorityRows, r => Assert.True(r.Priority is 1 or 2));
	}

	[Fact]
	public async Task GetGroupedCountsAsync_SearchNarrowsToMatchingTargetNames()
	{
		Guid runId = await SeedRunWithJobsAsync(perCell: 100);

		ComponentJobFilter searchFilter = new(null, null, null, "needle-host");
		IReadOnlyList<ComponentJobCountRow> rows = await _repository.GetGroupedCountsAsync(runId, searchFilter, CancellationToken.None);

		// Every 50th job (out of 1,100 total at perCell=100) is a needle -- 22 needles.
		long total = rows.Sum(r => r.Count);
		Assert.Equal(22, total);
	}

	[Fact]
	public async Task ListComponentJobsAsync_PagesEveryRowExactlyOnceInStableOrder()
	{
		const int perCell = 50; // 550 jobs total -- enough pages to exercise the cursor without a slow test
		Guid runId = await SeedRunWithJobsAsync(perCell);
		long expectedTotal = StateMatrix.Length * (long)perCell;

		HashSet<Guid> seen = [];
		short? lastPriority = null;
		ComponentJobCursorPosition? cursor = null;
		int pages = 0;

		while (true)
		{
			ComponentJobPage page = await _repository.ListComponentJobsAsync(
				new ComponentJobListQuery(runId, NoFilter, cursor, Limit: 97), CancellationToken.None);
			pages++;

			foreach (ComponentJobRow row in page.Items)
			{
				Assert.True(seen.Add(row.Id), "a row was returned on more than one page");
				if (lastPriority is { } previous)
				{
					Assert.True(row.Priority >= previous, "priority ordering regressed across a page boundary");
				}
				lastPriority = row.Priority;
			}

			if (page.NextCursor is null)
			{
				break;
			}

			cursor = page.NextCursor;
			Assert.True(pages < 100, "pagination did not converge -- possible cursor bug");
		}

		Assert.Equal(expectedTotal, seen.Count);
		Assert.True(pages > 1, "the seeded set should have required more than one page at this page size");
	}

	/// <summary>
	/// Explicit id tie-break case (PR #941 round-1 note (b)): several rows share the
	/// EXACT same (priority, created_at) pair, so only the cursor's third leg can
	/// order them. Paging one row at a time must visit each exactly once in
	/// ascending-id order with no skip or duplicate across the tied boundary.
	/// </summary>
	[Fact]
	public async Task ListComponentJobsAsync_SamePriorityAndCreatedAt_TieBreaksOnIdExactlyOnce()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid runId;
		await using (NpgsqlCommand insertRun = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection))
		{
			runId = (Guid)(await insertRun.ExecuteScalarAsync())!;
		}

		// Five rows, identical priority AND identical created_at (pinned to one
		// literal timestamp, not now()) -- the keyset's first two legs are useless
		// here by construction.
		List<Guid> insertedIds = [];
		for (int i = 0; i < 5; i++)
		{
			await using NpgsqlCommand insertJob = new(
				"""
				INSERT INTO jobs (run_id, job_type, target_name, priority, state, created_at)
				VALUES ($1, 'scan', $2, 3, 'queued', '2026-08-27T00:00:00Z'::timestamptz)
				RETURNING id
				""", connection);
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue($"tied-host-{i}");
			insertedIds.Add((Guid)(await insertJob.ExecuteScalarAsync())!);
		}

		List<Guid> expectedOrder = [.. insertedIds.OrderBy(id => id)];

		List<Guid> visited = [];
		ComponentJobCursorPosition? cursor = null;
		for (int page = 0; page < 10; page++)
		{
			ComponentJobPage result = await _repository.ListComponentJobsAsync(
				new ComponentJobListQuery(runId, NoFilter, cursor, Limit: 1), CancellationToken.None);
			visited.AddRange(result.Items.Select(r => r.Id));
			if (result.NextCursor is null)
			{
				break;
			}

			cursor = result.NextCursor;
		}

		Assert.Equal(expectedOrder, visited);
	}

	/// <summary>
	/// Issue #946 (folded from PR #941 round-1 deferred finding (c)): the ILIKE
	/// escape character itself must be escaped, not just <c>%</c>/<c>_</c> -- a
	/// search term containing a literal backslash must match only names containing
	/// that literal backslash, and a literal <c>%</c> term must not act as a
	/// wildcard.
	/// </summary>
	[Fact]
	public async Task ListComponentJobsAsync_SearchEscapesBackslashAndWildcardsLiterally()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid runId;
		await using (NpgsqlCommand insertRun = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection))
		{
			runId = (Guid)(await insertRun.ExecuteScalarAsync())!;
		}

		foreach (string name in new[] { @"domain\host-a", "host-100%", "host-plain", "hostX100Y" })
		{
			await using NpgsqlCommand insertJob = new(
				"INSERT INTO jobs (run_id, job_type, target_name, priority, state) VALUES ($1, 'scan', $2, 3, 'queued')", connection);
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue(name);
			await insertJob.ExecuteNonQueryAsync();
		}

		// A literal backslash matches only the backslash row (an unescaped "\h"
		// would instead be the escape sequence for a bare 'h' and over-match).
		ComponentJobPage backslash = await _repository.ListComponentJobsAsync(
			new ComponentJobListQuery(runId, new ComponentJobFilter(null, null, null, @"domain\host"), null, 10), CancellationToken.None);
		ComponentJobRow backslashRow = Assert.Single(backslash.Items);
		Assert.Equal(@"domain\host-a", backslashRow.TargetName);

		// A literal '%' term must not wildcard: "100%" matches only host-100%,
		// never hostX100Y (which unescaped-'%' leakage would also match).
		ComponentJobPage percent = await _repository.ListComponentJobsAsync(
			new ComponentJobListQuery(runId, new ComponentJobFilter(null, null, null, "100%"), null, 10), CancellationToken.None);
		ComponentJobRow percentRow = Assert.Single(percent.Items);
		Assert.Equal("host-100%", percentRow.TargetName);
	}

	[Fact]
	public async Task ListComponentJobsAsync_FilterAndSearchPinAnIndividualItem()
	{
		Guid runId = await SeedRunWithJobsAsync(perCell: 40);

		ComponentJobFilter filter = new(null, null, null, "needle-host-00050");
		ComponentJobPage page = await _repository.ListComponentJobsAsync(
			new ComponentJobListQuery(runId, filter, After: null, Limit: 10), CancellationToken.None);

		ComponentJobRow row = Assert.Single(page.Items);
		Assert.Equal("needle-host-00050", row.TargetName);
		Assert.Null(page.NextCursor);
	}

	[Fact]
	public async Task ListComponentJobsAsync_ReportsComponentKindFromScanPlanItemWhenPresent()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid runId;
		await using (NpgsqlCommand insertRun = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection))
		{
			runId = (Guid)(await insertRun.ExecuteScalarAsync())!;
		}

		// Seed the minimal 0050/0054 identity chain a scan_plan_items row requires --
		// same helper shape ScanPlanRepositoryTests.SeedComponentAndProfileAsync uses --
		// then record a one-item plan via ScanPlanRepository (the real write path,
		// exercising the exact join this test proves) carrying selector_kind='esxi'.
		Guid siteId = (Guid)(await new NpgsqlCommand("INSERT INTO sites (name) VALUES ('site-kind') RETURNING id", connection).ExecuteScalarAsync())!;
		Guid targetId;
		await using (NpgsqlCommand target = new(
			"INSERT INTO targets (site_id, kind, name, connection) VALUES ($1, 'vsphere', 'target-kind', '{}'::jsonb) RETURNING id", connection))
		{
			target.Parameters.AddWithValue(siteId);
			targetId = (Guid)(await target.ExecuteScalarAsync())!;
		}

		Guid componentId;
		await using (NpgsqlCommand component = new(
			"""
			INSERT INTO components (parent_target_id, catalog_component_key, vendor_identity, display_name, lifecycle)
			VALUES ($1, 'esxi', 'host-kind', 'esxi-01', 'active') RETURNING id
			""", connection))
		{
			component.Parameters.AddWithValue(targetId);
			componentId = (Guid)(await component.ExecuteScalarAsync())!;
		}

		CatalogRepository catalog = new(_fixture.ConnectionString);
		CatalogSourceRevision sourceRevision = await catalog.UpsertSourceRevisionAsync("source-kind", null, CancellationToken.None);
		CatalogProduct product = await catalog.UpsertProductAsync(sourceRevision.Id, "VMware", "vsphere-kind", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition("esxi-kind", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Srg, "release-kind", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await catalog.UpsertReportGroupAsync("group-kind", "Test Group", 2, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		ScanPlanItem item = new(
			componentId, executionProfile.Id, BaselineId: null, BenchmarkRevisionId: null,
			Transport: CatalogTransports.VMware, SelectorKind: CatalogSelectorKinds.Esxi, SelectorName: null,
			ReportGroupKey: "group-kind", Priority: 4, OutputKind: CatalogOutputKinds.HdfAndCkl,
			RequiredPurposes: [], DeclaredInputNames: []);
		ScanPlan plan = new(runId, ScanPlanSchema.CurrentVersion, [item], [], "digest-kind", "1 of 1 accepted");
		ScanPlanRepository scanPlans = new(_fixture.ConnectionString);
		IReadOnlyDictionary<Guid, Guid> planItemIdsByComponentId = await scanPlans.RecordAsync(runId, runScopeSnapshotId: null, plan, CancellationToken.None);
		Guid planItemId = planItemIdsByComponentId[componentId];

		await using (NpgsqlCommand insertJob = new(
			"""
			INSERT INTO jobs (run_id, job_type, target_name, priority, state, scan_plan_item_id)
			VALUES ($1, 'scan', 'esxi-01', 4, 'queued', $2)
			""", connection))
		{
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue(planItemId);
			await insertJob.ExecuteNonQueryAsync();
		}

		ComponentJobPage page = await _repository.ListComponentJobsAsync(
			new ComponentJobListQuery(runId, NoFilter, After: null, Limit: 10), CancellationToken.None);

		ComponentJobRow row = Assert.Single(page.Items);
		Assert.Equal("esxi", row.ComponentKind);

		IReadOnlyList<ComponentJobCountRow> counts = await _repository.GetGroupedCountsAsync(runId, NoFilter, CancellationToken.None);
		ComponentJobCountRow countRow = Assert.Single(counts);
		Assert.Equal("esxi", countRow.ComponentKind);
	}

	// -- issue #757: ResolveJobIdsAsync (bulk-action id resolution) ----------

	[Fact]
	public async Task ResolveJobIdsAsync_WithinBound_ReturnsExactlyMatchingIds()
	{
		const int perCell = 10; // 110 jobs total, well under the bound
		Guid runId = await SeedRunWithJobsAsync(perCell);

		ComponentJobFilter queuedFilter = new(States: [JobStates.Queued], Priorities: null, ComponentKinds: null, Search: null);
		IReadOnlyList<Guid> ids = await _repository.ResolveJobIdsAsync(runId, queuedFilter, maxItems: 500, CancellationToken.None);

		int expectedQueuedCells = StateMatrix.Count(x => x.State == JobStates.Queued);
		Assert.Equal(expectedQueuedCells * perCell, ids.Count);
		Assert.Equal(ids.Count, ids.Distinct().Count());
	}

	[Fact]
	public async Task ResolveJobIdsAsync_OverBound_ReturnsMoreThanMaxItems_SoCallerCan400WithoutTruncatingSilently()
	{
		const int perCell = 50; // 550 jobs total
		Guid runId = await SeedRunWithJobsAsync(perCell);

		// No filter -- matches all 550 jobs, well past a deliberately small bound.
		IReadOnlyList<Guid> ids = await _repository.ResolveJobIdsAsync(runId, NoFilter, maxItems: 100, CancellationToken.None);

		// maxItems + 1 (101), never silently clamped to 100 -- the caller (RunsController)
		// is the one that turns "more than the bound" into a 400, not this method.
		Assert.Equal(101, ids.Count);
	}

	[Fact]
	public async Task ResolveJobIdsAsync_ScopedToRun_NeverReturnsAnotherRunsJobs()
	{
		Guid runId = await SeedRunWithJobsAsync(perCell: 5);
		Guid otherRunId = await SeedRunWithJobsAsync(perCell: 5);

		IReadOnlyList<Guid> ids = await _repository.ResolveJobIdsAsync(runId, NoFilter, maxItems: 500, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand otherRunJobIds = new("SELECT id FROM jobs WHERE run_id = $1", connection);
		otherRunJobIds.Parameters.AddWithValue(otherRunId);
		HashSet<Guid> foreignIds = [];
		await using (NpgsqlDataReader reader = await otherRunJobIds.ExecuteReaderAsync())
		{
			while (await reader.ReadAsync())
			{
				foreignIds.Add(reader.GetGuid(0));
			}
		}

		Assert.DoesNotContain(ids, id => foreignIds.Contains(id));
	}
}
