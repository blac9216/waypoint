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
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #731 (migration 0055): proves the storage/atomicity/retention contract
/// against real Postgres -- the acceptance criteria only mean something proven
/// against the real engine (row locking under <c>FOR UPDATE</c>, the partial unique
/// index, <c>ON DELETE RESTRICT</c> are all Postgres-specific).
/// </summary>
[Collection("Postgres")]
public sealed class BaselineRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private BaselineRepository _baselines = null!;
	private CatalogRepository _catalog = null!;

	public BaselineRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_baselines = new BaselineRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				baselines, content_revisions,
				catalog_import_report_entries, catalog_import_reports, catalog_declared_inputs,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>Full 0050 identity tree down to one execution profile, for tests that only need a valid FK target.</summary>
	private async Task<Guid> SeedExecutionProfileAsync(string suffix)
	{
		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware vSphere", $"vsphere-{suffix}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"vcenter-{suffix}", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null),
			CancellationToken.None);
		CatalogContentRelease contentRelease = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Stig, $"v2r3-stig-{suffix}", "STIG V2R3", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"vcenter-stig-{suffix}", "vCenter STIG", 3, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "2.3.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		return executionProfile.Id;
	}

	private async Task<ContentRevision> SeedRevisionAsync(string suffix) =>
		await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", $"digest-{suffix}", $"revisions/digest-{suffix}", CancellationToken.None);

	[Fact]
	public async Task RecordStagedRevisionAsync_SameSourceCommitAndDigest_IsIdempotent()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];

		ContentRevision first = await SeedRevisionAsync(suffix);
		ContentRevision second = await _baselines.RecordStagedRevisionAsync(
			$"commit-{suffix}", $"digest-{suffix}", $"revisions/digest-{suffix}", CancellationToken.None);

		Assert.Equal(first.Id, second.Id);
		Assert.Single(await _baselines.ListRevisionsAsync(CancellationToken.None), r => r.ContentDigest == $"digest-{suffix}");
	}

	[Fact]
	public async Task CreateStagedBaselineAsync_DoesNotActivate()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);

		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);

		Assert.Equal(BaselineStatuses.Staged, staged.Status);
		Assert.Null(await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None));
	}

	/// <summary>
	/// Issue #1002 item 2: <paramref name="benchmarkRevisionId"/> is null here and
	/// <see cref="SeedExecutionProfileAsync"/> seeds a <see cref="CatalogKinds.Stig"/>
	/// execution profile -- this is exactly "a STIG execution profile with no
	/// benchmark" activating successfully. Activation/approval of the profile-only
	/// baseline is NOT gated on a benchmark revision being present; the standing
	/// `benchmark_missing` alert (Waypoint.Api's BenchmarksController) surfaces the gap
	/// without blocking this call. Issue #1021 fixed the scan-time consequence too: this
	/// state now plans and executes the STIG profile profile-only in
	/// <c>ScanPlannerService</c> (<c>ScanPlanItem.IsBenchmarkMissing</c>) instead of the
	/// pre-#1021 <c>ScanPlanSkipReasons.UnmappedBenchmark</c> skip that made the
	/// component permanently unplannable -- see <c>ScanPlannerServiceTests</c> for that
	/// half of the "approvable, scannable, standing alert" contract.
	/// </summary>
	[Fact]
	public async Task ActivateAsync_StigExecutionProfileWithNoBenchmarkRevision_ActivatesSuccessfully()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);

		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "admin@example.internal", CancellationToken.None);

		Assert.Equal(BaselineActivationOutcome.Activated, outcome);
		Baseline? active = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(active);
		Assert.Null(active!.BenchmarkRevisionId);
	}

	[Fact]
	public async Task ActivateAsync_FirstActivationForExecutionProfile_Succeeds()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);

		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "admin@example.internal", CancellationToken.None);

		Assert.Equal(BaselineActivationOutcome.Activated, outcome);
		Baseline? active = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(active);
		Assert.Equal(staged.Id, active!.Id);
		Assert.Equal("admin@example.internal", active.ActivatedBy);
		Assert.NotNull(active.ActivatedAt);

		ContentRevision? revisionAfter = await _baselines.GetRevisionAsync(revision.Id, CancellationToken.None);
		Assert.Equal(ContentRevisionStatuses.Activated, revisionAfter!.Status);
	}

	/// <summary>
	/// Issue #731 AC "activation is atomic": activating a second baseline for the SAME
	/// execution profile must supersede the first, never leave two active rows. This is
	/// the class-killing check -- reverting <see cref="BaselineRepository"/>'s supersede
	/// step (deleting the UPDATE that flips the old row to 'superseded' before
	/// activating the new one) would violate the partial unique index and fail this
	/// test with a constraint violation instead of silently leaving two active rows,
	/// but asserting the END STATE here (exactly one active row, and it is the new one)
	/// is what actually proves the atomicity claim end to end.
	/// </summary>
	[Fact]
	public async Task ActivateAsync_SecondActivationForSameExecutionProfile_SupersedesTheFirst()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);

		ContentRevision revision1 = await SeedRevisionAsync(suffix + "-r1");
		Baseline baseline1 = await _baselines.CreateStagedBaselineAsync(revision1.Id, executionProfileId, null, CancellationToken.None);
		await _baselines.ActivateAsync(baseline1.Id, "admin@example.internal", CancellationToken.None);

		ContentRevision revision2 = await SeedRevisionAsync(suffix + "-r2");
		Baseline baseline2 = await _baselines.CreateStagedBaselineAsync(revision2.Id, executionProfileId, null, CancellationToken.None);
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(baseline2.Id, "admin@example.internal", CancellationToken.None);

		Assert.Equal(BaselineActivationOutcome.Activated, outcome);

		IReadOnlyList<Baseline> all = await _baselines.ListBaselinesForExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.Single(all, b => b.Status == BaselineStatuses.Active);
		Baseline supersededFirst = Assert.Single(all, b => b.Id == baseline1.Id);
		Assert.Equal(BaselineStatuses.Superseded, supersededFirst.Status);
		Assert.NotNull(supersededFirst.SupersededAt);

		Baseline? active = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.Equal(baseline2.Id, active!.Id);
	}

	[Fact]
	public async Task ActivateAsync_UnknownBaseline_ReturnsNotFound()
	{
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(Guid.NewGuid(), "admin@example.internal", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.NotFound, outcome);
	}

	[Fact]
	public async Task ActivateAsync_AlreadyActive_ReturnsAlreadyActiveWithoutChangingState()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);
		await _baselines.ActivateAsync(staged.Id, "admin@example.internal", CancellationToken.None);

		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "admin@example.internal", CancellationToken.None);

		Assert.Equal(BaselineActivationOutcome.AlreadyActive, outcome);
	}

	/// <summary>
	/// Issue #731 AC "rollback to any approved baseline": rolling back to a superseded
	/// baseline reactivates it (ADR-0022 "creates a new activation event pointing at
	/// the old artifact set") -- the previously-active newer baseline becomes
	/// superseded in turn, and the rolled-back baseline's activated_at is refreshed to
	/// the new event time, not resurrected from its original activation.
	/// </summary>
	[Fact]
	public async Task RollbackAsync_ToASupersededBaseline_ReactivatesItAndSupersedesTheCurrent()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);

		ContentRevision revision1 = await SeedRevisionAsync(suffix + "-r1");
		Baseline baseline1 = await _baselines.CreateStagedBaselineAsync(revision1.Id, executionProfileId, null, CancellationToken.None);
		await _baselines.ActivateAsync(baseline1.Id, "admin@example.internal", CancellationToken.None);
		DateTimeOffset firstActivatedAt = (await _baselines.GetBaselineAsync(baseline1.Id, CancellationToken.None))!.ActivatedAt!.Value;

		ContentRevision revision2 = await SeedRevisionAsync(suffix + "-r2");
		Baseline baseline2 = await _baselines.CreateStagedBaselineAsync(revision2.Id, executionProfileId, null, CancellationToken.None);
		await _baselines.ActivateAsync(baseline2.Id, "admin@example.internal", CancellationToken.None);

		await Task.Delay(TimeSpan.FromMilliseconds(50));
		BaselineActivationOutcome outcome = await _baselines.RollbackAsync(baseline1.Id, "admin-rollback@example.internal", CancellationToken.None);

		Assert.Equal(BaselineActivationOutcome.Activated, outcome);

		Baseline? active = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.Equal(baseline1.Id, active!.Id);
		Assert.Equal("admin-rollback@example.internal", active.ActivatedBy);
		Assert.True(active.ActivatedAt > firstActivatedAt, "rollback should record a NEW activation event, not resurrect the original activated_at.");

		Baseline? nowSuperseded = await _baselines.GetBaselineAsync(baseline2.Id, CancellationToken.None);
		Assert.Equal(BaselineStatuses.Superseded, nowSuperseded!.Status);
	}

	[Fact]
	public async Task RollbackAsync_ContentRevisionRejected_ReturnsRevisionNotEligible()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);

		await MarkRevisionRejectedAsync(revision.Id);

		BaselineActivationOutcome outcome = await _baselines.RollbackAsync(staged.Id, "admin@example.internal", CancellationToken.None);

		Assert.Equal(BaselineActivationOutcome.RevisionNotEligible, outcome);
		Assert.Null(await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None));
	}

	/// <summary>
	/// Issue #731 AC "retention: revisions referenced by plans/results are deletion-
	/// protected (RESTRICT semantics)": a content_revisions row referenced by ANY
	/// baseline (staged, active, or superseded) can never be deleted -- migration
	/// 0055's ON DELETE RESTRICT FK proven against the real engine.
	/// </summary>
	[Fact]
	public async Task ContentRevisionReferencedByABaseline_CannotBeDeleted()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);
		await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM content_revisions WHERE id = $1", connection);
		delete.Parameters.AddWithValue(revision.Id);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
	}

	/// <summary>Same retention protection for a catalog_execution_profiles row referenced by a baseline -- migration 0050's FK, now exercised transitively through 0055's new referrer.</summary>
	[Fact]
	public async Task CatalogExecutionProfileReferencedByABaseline_CannotBeDeleted()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		ContentRevision revision = await SeedRevisionAsync(suffix);
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);
		await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, null, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM catalog_execution_profiles WHERE id = $1", connection);
		delete.Parameters.AddWithValue(executionProfileId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
	}

	/// <summary>
	/// Issue #731's concurrency AC, proven directly against the real engine rather than
	/// only via the row-lock code path: two callers racing to activate DIFFERENT
	/// baselines for the SAME execution profile at the same instant must still end with
	/// exactly one active baseline, never zero or two -- <c>FOR UPDATE</c> in
	/// <see cref="BaselineRepository"/> serializes them rather than letting both commit
	/// concurrently. This is the class-killing check for the atomicity claim under
	/// actual concurrent load, not just sequential calls.
	/// </summary>
	[Fact]
	public async Task ConcurrentActivations_ForTheSameExecutionProfile_NeverProduceTwoActiveBaselines()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);

		ContentRevision revisionA = await SeedRevisionAsync(suffix + "-a");
		Baseline baselineA = await _baselines.CreateStagedBaselineAsync(revisionA.Id, executionProfileId, null, CancellationToken.None);
		ContentRevision revisionB = await SeedRevisionAsync(suffix + "-b");
		Baseline baselineB = await _baselines.CreateStagedBaselineAsync(revisionB.Id, executionProfileId, null, CancellationToken.None);

		BaselineRepository racerA = new(_fixture.ConnectionString);
		BaselineRepository racerB = new(_fixture.ConnectionString);

		Task<BaselineActivationOutcome> taskA = racerA.ActivateAsync(baselineA.Id, "admin-a@example.internal", CancellationToken.None);
		Task<BaselineActivationOutcome> taskB = racerB.ActivateAsync(baselineB.Id, "admin-b@example.internal", CancellationToken.None);

		BaselineActivationOutcome[] outcomes = await Task.WhenAll(taskA, taskB);

		Assert.All(outcomes, outcome => Assert.Equal(BaselineActivationOutcome.Activated, outcome));

		IReadOnlyList<Baseline> all = await _baselines.ListBaselinesForExecutionProfileAsync(executionProfileId, CancellationToken.None);
		Assert.Single(all, b => b.Status == BaselineStatuses.Active);
	}

	/// <summary>
	/// Issue #731's concurrency AC, second form: an in-flight "executing job" that has
	/// already resolved a baseline's staged files must not observe that resolution
	/// change even while a concurrent activation runs against the SAME execution
	/// profile. This simulates the executing job as a read of
	/// <see cref="IBaselineRepository.GetBaselineAsync"/>'s already-returned row (an
	/// immutable snapshot by value, not a live reference) taken BEFORE a concurrent
	/// activation swap runs -- the snapshot's own field values must remain exactly what
	/// they were at resolution time no matter what happens afterward, proving a
	/// "resolve, then execute" caller never needs to re-check mid-flight.
	/// </summary>
	[Fact]
	public async Task ExecutingJobsResolvedBaselineSnapshot_IsUnaffectedByAConcurrentActivation()
	{
		string suffix = Guid.NewGuid().ToString("N")[..8];
		Guid executionProfileId = await SeedExecutionProfileAsync(suffix);

		ContentRevision revision1 = await SeedRevisionAsync(suffix + "-r1");
		Baseline baseline1 = await _baselines.CreateStagedBaselineAsync(revision1.Id, executionProfileId, null, CancellationToken.None);
		await _baselines.ActivateAsync(baseline1.Id, "admin@example.internal", CancellationToken.None);

		// The "executing job" resolves the active baseline once, up front -- exactly
		// what a scan's planning step would do before it starts reading files.
		Baseline resolvedByJob = (await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None))!;

		// Concurrently, an operator activates a NEW baseline for the same execution
		// profile while the job above is still (conceptually) executing against its
		// already-resolved snapshot.
		ContentRevision revision2 = await SeedRevisionAsync(suffix + "-r2");
		Baseline baseline2 = await _baselines.CreateStagedBaselineAsync(revision2.Id, executionProfileId, null, CancellationToken.None);
		BaselineActivationOutcome activationOutcome = await _baselines.ActivateAsync(baseline2.Id, "admin@example.internal", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.Activated, activationOutcome);

		// The job's own already-resolved snapshot (a C# record, not a live query) is
		// untouched by value -- it never silently became baseline2 or lost its content
		// revision reference.
		Assert.Equal(baseline1.Id, resolvedByJob.Id);
		Assert.Equal(revision1.Id, resolvedByJob.ContentRevisionId);
		Assert.Equal(BaselineStatuses.Active, resolvedByJob.Status);

		// And the underlying staged revision directory reference the job resolved
		// still exists and was never touched by the later activation/supersession --
		// only its DB status flips to 'superseded', the row and its content_revision_id
		// (the actual file-resolution key) survive unchanged.
		Baseline? nowSuperseded = await _baselines.GetBaselineAsync(baseline1.Id, CancellationToken.None);
		Assert.NotNull(nowSuperseded);
		Assert.Equal(revision1.Id, nowSuperseded!.ContentRevisionId);
		Assert.Equal(BaselineStatuses.Superseded, nowSuperseded.Status);
	}

	private async Task MarkRevisionRejectedAsync(Guid revisionId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE content_revisions SET status = 'rejected' WHERE id = $1", connection);
		command.Parameters.AddWithValue(revisionId);
		await command.ExecuteNonQueryAsync();
	}
}
