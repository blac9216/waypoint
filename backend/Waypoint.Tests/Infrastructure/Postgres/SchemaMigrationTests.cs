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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Runs the real migrations pipeline against a real, disposable PostgreSQL 16
/// container (see <see cref="PostgresFixture"/>) — the acceptance criteria this
/// covers only mean something proven against the real engine (partial indexes,
/// advisory locks, <c>CREATE OR REPLACE TRIGGER</c> are all Postgres-specific and
/// have no meaningful fake).
/// </summary>
[Collection("Postgres")]
public sealed class SchemaMigrationTests
{
	private static readonly string[] ExpectedTables =
	[
		"credentials",
		"credential_secrets",
		"runs",
		"jobs",
		"job_events",
		"depot_artifacts",
		"downloads",
		"audit_log",
		"appliance_state",
		"sites",
		"targets",
		"inventory_items",
		"config_docs",
		"config_versions",
		"stigman_connections",
		"attestation_snapshots",
		"run_secrets",
		"worker_registry",
		"schedules",
		"users",
		"compliance_content",
		"compliance_content_pulls",
		"profiles",
		"capacity_pool",
		"capacity_leases",
		"managed_tool_installs",
		"profile_controls",
		"run_purges",
		"run_purge_tombstones",
		"target_credential_bindings",
		"job_credential_bindings",
		"run_history_deletion_tombstones",
		"depot_enrollment",
		"catalog_pull_state",
		"catalog_source_revisions",
		"catalog_products",
		"catalog_product_versions",
		"catalog_content_releases",
		"catalog_components",
		"catalog_report_groups",
		"catalog_execution_profiles",
		"catalog_credential_requirements",
		"catalog_benchmark_references",
		"catalog_remediation_definitions",
		"benchmark_revisions",
		"benchmark_rules",
		"benchmark_component_mappings",
		"catalog_import_reports",
		"catalog_import_report_entries",
		"catalog_declared_inputs",
		"components",
		"component_observations",
		"content_revisions",
		"baselines",
		"run_scope_snapshots",
		"scan_plans",
		"scan_plan_items",
		"trust_bundles",
		"trust_policies",
		"component_results",
		"component_result_findings",
		"component_result_artifacts",
		"schema_migrations"
	];

	/// <summary>Embedded migration count as of issue #584 (... 0042 adds run_purges + run_purge_tombstones -- the durable retryable purge lifecycle and append-only audit tombstone for the admin-only terminal-compliance-run purge, plus runs.purged_at, 'purge' in jobs_job_type_check/runs_run_type_check, relaxes schedules.last_run_id from RESTRICT to ON DELETE SET NULL, and the compliance-runner's run_purges progress-reporting grant, issue #594, 0043 adds target_credential_bindings -- the normalized purpose-specific credential binding table (ADR-0021), backfills existing targets.credential_id references into the kind-appropriate default-purpose binding, and documents the dual-write contract keeping targets.credential_id and the default-purpose binding consistent until #585 removes the legacy column -- no new runner grants, issue #584, 0044 adds job_credential_bindings -- the immutable per-job per-purpose credential snapshot ledger (ADR-0021 SS5) RunCreationService's scan fan-out populates, with the compliance-runner SELECT-only grant and the jobs.credential_id dual-write/fallback contract documented in the migration header, issue #585, 0045 re-keys run_secrets from one row per run to one row per (run, target, purpose) -- additive columns/indexes only, the unconditional per-terminal-completion DELETE stays run_id-scoped so it covers both shapes with no code change, plus job_credential_bindings.is_run_secret so a job's per-purpose snapshot can name an ad hoc (run_secrets-backed) source instead of a stored credential_id, issue #586, 0046 adds runs.history_deleted_at + run_history_deletion_tombstones -- generic operational-history deletion for TERMINAL runs, structurally separate from run_purges/run_purge_tombstones (a deliberate sibling table, not a shared one) and deferring to that domain purge for scan/remediate runs, entirely API-side with no new runner grants, issue #592, 0047 extends credentials_credential_type_check with 'depot-activation-code' and 'legacy-download-token' (issue #690's non-destructive split of the ambiguous 'depot-token' well-known type -- 'depot-token' itself is RETAINED, not dropped, so pre-existing rows stay valid and visibly legacy; no data rewritten, no new runner grants), issue #691, 0048 adds depot_enrollment -- the singleton (mirrors appliance_state) non-secret Software Depot ID + assisted-enrollment state-machine table, adds 'depot-enrollment' to jobs_job_type_check/runs_run_type_check for the noninteractive tool-invocation job, and grants waypoint_download_runner SELECT/UPDATE on the new table, issue #687, 0049 adds catalog_pull_state -- the singleton (mirrors depot_enrollment) connected-catalog-pull attempt/success tracking table, adds 'catalog-pull' to jobs_job_type_check/runs_run_type_check for the distinct connected vendor-catalog-pull job (separate from the local credential-free catalog-index re-index), and grants waypoint_download_runner SELECT/INSERT/UPDATE on the new table, issue #687, 0050 adds the normalized compliance catalog (issue #728, ADR-0022): catalog_source_revisions, catalog_products, catalog_product_versions, catalog_content_releases, catalog_components, catalog_report_groups, catalog_execution_profiles, catalog_credential_requirements, catalog_benchmark_references, and catalog_remediation_definitions -- the versioned identity tree and closed capability vocabulary for STIG/SRG execution profiles, all FKs ON DELETE RESTRICT so a plan-referenced historical revision cannot be deleted, no new runner grants (every row is catalog-authored, appliance-shipped data), issue #728, 0051 adds catalog_import_reports + catalog_import_report_entries (persisted SemanticImportReport headers/entries -- accepted/warning/rejected disposition per profile key) and catalog_declared_inputs (per-execution-profile declared InSpec inputs, closing issue #728's last queryable-fields AC) -- the issue #729 persistence slice; grants waypoint_compliance_runner SELECT/INSERT on the two report tables and SELECT/INSERT/UPDATE on every 0050 catalog identity-tree table plus catalog_declared_inputs, since ContentPullJobHandler's semantic-import pass now writes candidate promotions from the compliance-runner process, issue #729, 0052 adds 0052_xccdf_benchmark_revisions.sql: benchmark_revisions, benchmark_rules, and benchmark_component_mappings -- immutable digest-addressed DISA XCCDF/STIG benchmark revisions and rules plus the exact component-to-benchmark-revision mapping and its versioned audit history (one current row per component via a partial unique index, prior decisions superseded rather than overwritten), no new runner grants (Admin-only mapping writes are API-layer, deferred to issue #730's remainder PR), issue #730, 0053 fixes issue #832 (same defect class as PR #831's NULL-parent catalog_components race): catalog_execution_profiles' promotion write becomes an atomic INSERT ... ON CONFLICT (component_id, content_release_id) DO UPDATE against 0050's own catalog_execution_profiles_unique constraint (no new index needed -- both natural-key columns are NOT NULL, so the existing plain UNIQUE constraint already has no NULL-distinctness gap), replacing the prior check-then-insert; adds the missing UPDATE grant on catalog_execution_profiles to waypoint_compliance_runner (0051 only granted SELECT/INSERT, matching the check-then-insert code that existed then) so the new DO UPDATE branch does not ship green under the owner role and then 42501 live under the runner role, issue #832, 0054 adds components + component_observations (issue #732, epic #726 Wave
	/// 2, ADR-0023): stable compliance endpoint/component identity beneath a top-level
	/// target, independent configured/discovered exact-version facts with an explicit
	/// fact_conflict readiness signal, active/absent/retired lifecycle (rediscovery
	/// reconnects, continuous absence is application-policy-timed retirement), and
	/// append-only observation provenance -- no new runner grants (this slice's write
	/// path is API-side only; discovery-job wiring is deferred), issue #732, 0055 adds content_revisions + baselines (issue #731, ADR-0022 capstone
	/// "Stage, diff, activate, and retain compliance content revisions atomically"):
	/// content_revisions is one immutable digest-addressed staged filesystem snapshot
	/// per (source_commit, content_digest), and baselines is one atomically-activatable
	/// coherent set (content revision + catalog execution profile + optional benchmark
	/// revision) with a partial unique index enforcing at most one active baseline per
	/// execution profile -- the activation boundary ADR-0022 requires to be exclusive.
	/// Grants waypoint_compliance_runner SELECT/INSERT plus column-scoped
	/// UPDATE (source_commit) on content_revisions (ContentRevisionStager's idempotent
	/// ON CONFLICT DO UPDATE write path) and SELECT on baselines; activation/rollback
	/// never run as a runner role, issue #731, 0056 adds run_scope_snapshots (issue
	/// #733, epic #726 Wave 2, ADR-0023): one immutable row per scan run recording the
	/// requested `target_scope` (tri-state all/explicit) alongside the exact resolved
	/// stable-component id set and every scope omission with its reason -- the
	/// requested-versus-resolved audit freeze `RunCreationService.CreateScanRunAsync`
	/// writes via the new `ScopeResolutionService`/`RunScopeSnapshotRepository`, ON
	/// DELETE CASCADE off `runs.id` (matching job_credential_bindings' convention),
	/// no new runner grants (API-side only, mirroring migration 0054), issue #733,
	/// 0057 adds scan_plans + scan_plan_items (issue #734, epic #726 Wave 2, ADR-0023/
	/// 0024): the immutable, digest-addressed execution plan compiled from a run's
	/// resolved component scope -- one scan_plans header row per run (plan schema
	/// version, a link to migration 0056's run_scope_snapshots row, the deterministic
	/// plan_digest, a human-readable explanation, and skips_json for every candidate
	/// component that did not become an accepted item) plus one scan_plan_items row
	/// per ACCEPTED execution item (exact catalog execution profile/baseline/benchmark-
	/// revision identity, transport/selector, report group/priority, required
	/// credential purposes, and declared input names) -- written once by
	/// `RunCreationService.CreateScanRunAsync` via the new `ScanPlannerService`/
	/// `ScanPlanRepository`, every plan-item FK ON DELETE RESTRICT (a frozen plan must
	/// never be invalidated by later catalog/baseline changes) except `scan_plan_id`
	/// itself which cascades off its owning plan/run, no new runner grants (API-side
	/// only, mirroring migrations 0054/0056; #735-#737 own the runner-consumed
	/// component-job layer built on top of this), issue #734, 0058 grants
	/// waypoint_compliance_runner SELECT/INSERT/UPDATE on components and INSERT on
	/// component_observations (issue #732 discovery-wiring remainder):
	/// DiscoverJobHandler now calls the rewritten atomic
	/// ComponentRepository.UpsertDiscoveredAsync (issue #840's ON CONFLICT rewrite
	/// against migration 0054's two existing unique indexes -- no new schema needed)
	/// after its existing InventoryRepository write, so a real discovery pass
	/// materializes `components` rows instead of only repository-seeded test rows,
	/// issue #732 / #840,
	/// 0059 adds trust_bundles + trust_policies (issue #753, epic #726, ADR-0025
	/// "Connection-scoped trust"): trust_bundles is one immutable Admin-uploaded CA
	/// certificate/chain (public material, never a secret) with subject/issuer/
	/// fingerprint/validity metadata parsed at upload time and immutable supersede-not-
	/// mutate replacement, and trust_policies is one scoped (scope_type, scope_id)
	/// binding to either a trust_bundle or an explicit, reasoned, audited
	/// skip-verification bypass, never a process-global default -- partial unique
	/// indexes enforce at most one ACTIVE bundle per fingerprint and at most one
	/// CURRENT policy per scope, mirroring migrations 0052/0055's "one current row"
	/// idiom. No new runner grants (this slice is Admin-only API CRUD; runtime
	/// consumption is this issue's stated remainder), issue #753, 0060 keys
	/// config_docs to the stable catalog_execution_profile_id identity (nullable,
	/// additive alongside the existing free-text profile column) and adds
	/// scan_plan_items.input_resolutions_json/attestation_resolution_json/
	/// config_resolution_digest -- issue #735 (epic #726 Wave 2, ADR-0024
	/// "Control-granular settings and snapshots"): resolves each plan item's Input/
	/// Attestation config-doc snapshot Global -> Site -> Target at plan-compile time,
	/// keyed to the plan item's own catalog execution profile rather than a single
	/// fixed ScanOptions.AttestationProfile name, and freezes the resolution into the
	/// plan item alongside its other frozen fields. No new runner grant (this slice's
	/// write path is API-side only, mirroring migration 0057; ScanJobHandler's
	/// runtime attestation resolution is unchanged this slice), issue #735, 0061 adds
	/// jobs.scan_plan_item_id (issue #737 first slice, epic #726 Wave 2 capstone,
	/// ADR-0024 "one Postgres component job" per accepted plan item): a nullable
	/// ON DELETE RESTRICT link from a fanned-out 'scan' job to the exact immutable
	/// scan_plan_items row it executes. NULL for the legacy target-granular fan-out
	/// path, which RunCreationService keeps unchanged; non-NULL only for a
	/// target_scope-driven run's plan-item-granular fan-out. No new runner grant
	/// (jobs already grants waypoint_compliance_runner SELECT/INSERT/UPDATE via
	/// migration 0025; ScanJobHandler's component-granular execution consuming
	/// scan_plan_items is this issue's stated remainder), issue #737 -- 0062 adds
	/// the append-only <c>upload_attempts</c> table (issue #744, epic #726 Wave 4
	/// first slice): a per-attempt audit row (endpoint/collection/attempt_number/
	/// status/error_detail) recorded by ScanUploadCoordinator alongside its existing
	/// jobs.upload_status/upload_detail summary write, so a job's full upload attempt
	/// history (first pass and every stigman-upload-retry call) is queryable rather
	/// than overwritten. New runner grant: SELECT, INSERT on upload_attempts to
	/// waypoint_compliance_runner (append-only; the runner never updates/deletes a
	/// recorded attempt), issue #744 -- 0063 adds the immutable domain-owned result
	/// model (issue #745, epic #726 Wave 4, ADR-0024/0025): component_results (one row
	/// per job attempt against a scan_plan_item, closed status vocabulary
	/// completed/execution_error/skipped, CAT I/II/III open + passed/not_applicable/
	/// not_reviewed/skipped counts), component_result_findings (one row per XCCDF-
	/// mapped control finding, closed status vocabulary distinguishing pass/fail/
	/// not_applicable/not_reviewed/execution_error/skipped -- epic #726 §6's exactly-
	/// once Not_Reviewed rule), and component_result_artifacts (kind/path/digest/size
	/// for raw/attested HDF, CKL, summary, log). All three append-only, no UPDATE path.
	/// New runner grant: SELECT, INSERT on all three tables to
	/// waypoint_compliance_runner, issue #745 -- 0066 (slot claimed for this PR;
	/// 0064/0065 reserved by parallel agents at branch time, verified free at commit
	/// time against both the migrations directory and open PRs) wires ADR-0019
	/// retention/purge into the migration 0062/0063 evidence tables: append-only
	/// block-mutation triggers (mirroring 0021/0042's attestation_snapshots pattern)
	/// on component_results, component_result_findings, component_result_artifacts,
	/// and upload_attempts, each with a narrow session-local-GUC carve-out
	/// (waypoint.purge_run_id / waypoint.purge_job_ids) that only RunPurgeService's
	/// own purge transaction ever sets -- no new tables, no new runner grants (purge
	/// deletion is exclusively API-side, same owner-privileged connection every other
	/// purge step already uses), closing PR #961's stated "purge currently RESTRICTs"
	/// gap, issue #963 -- 0064 (the slot 0066 above's own comment already noted as
	/// "reserved by parallel agents at branch time"; re-verified free against both the
	/// migrations directory and open PRs at this PR's own commit time) adds no new
	/// tables: it seeds the hand-curated execution catalog (issue #959 Option C, epic
	/// #726) from docs/compliance-parity.md's documented provenance-matrix rows into
	/// 0050's existing catalog_source_revisions/catalog_products/
	/// catalog_product_versions/catalog_content_releases/catalog_components/
	/// catalog_report_groups/catalog_execution_profiles/
	/// catalog_credential_requirements/catalog_benchmark_references tables -- a
	/// representative slice covering every documented shape (vSphere object-kind
	/// split, VCSA named-service split, NSX named-function split, Photon
	/// whole-appliance), invented-from-documentation data only, every INSERT
	/// idempotent via ON CONFLICT DO NOTHING against 0050's existing natural-key
	/// constraints, no new runner grants (seed-only, same "no runner mutates this
	/// schema" convention as 0050), issue #959 -- 0067 (slot verified free against
	/// both the migrations directory and open PRs at this PR's own commit time;
	/// 0065 remains reserved by a different issue) expands 0064's seed to 9 of the
	/// remaining provenance-matrix rows (vSphere 9-0 SRG vmware + VCSA-service rows,
	/// NSX 9-x SRG named-function row, Aria Operations/Automation/Suite Lifecycle SRG
	/// whole-appliance rows, Workspace ONE Access SRG whole-appliance row, VCF 9-x SRG
	/// ssh named-service row), same invented-from-documentation/idempotent-ON-CONFLICT/
	/// no-new-grants pattern as 0064. The VCF 9-x `vcf-api` named-service row was
	/// deliberately NOT seeded by 0067: migration 0050's catalog_credential_requirements
	/// purpose CHECK constraint excluded 'vcf-api' pending issue #807, issue #967 --
	///
	/// 0068 (issue #974): adds `inventory_items.version`, additive alongside the
	/// existing `build` column -- no new tables, no runner grant changes --
	///
	/// 0069 (issue #977, epic #726; slot verified free against both the migrations
	/// directory and open PRs at this PR's own commit time, rebased onto main after
	/// PR #978 claimed and merged slot 0068) closes the vcf-api gap: #807 closed and
	/// its ADR-0024 resolved the vcf-api credential purpose, so 0069 widens 0050's
	/// CHECK (DROP CONSTRAINT IF EXISTS + ADD CONSTRAINT idiom, matching migration
	/// 0022's precedent for widening a closed-vocabulary CHECK) to admit 'vcf-api' and
	/// seeds the 13th and final provenance-matrix row (VCF 9-x `vcf-api` named-service:
	/// SDDC Manager application, Automation application), same
	/// invented-from-documentation/idempotent-ON-CONFLICT/no-new-grants pattern as
	/// 0064/0067 --
	///
	/// 0070 (issue #998, epic #726, PR #1004): reconciles every seeded
	/// catalog_product_versions.version_key from the pre-decision patch-level/exact
	/// forms ("8.0.3", "9.0.0", ...) to the vendor's DECLARED VERSION SCOPE verbatim
	/// ("8.0", "9.x", ...) -- catalog keys only; the scope-matching logic itself lives
	/// in Waypoint.Core.Components.VersionScopeMatcher, no schema shape changes, no
	/// new runner grants --
	///
	/// 0071 (issue #1002, epic #726; slot 0070 was reserved for issue #998's catalog
	/// seed/matcher work per epic #726 coordination and is claimed by PR #1004's
	/// merged migration above; 0071 verified free against both the migrations
	/// directory and open PRs at this migration's own commit time, re-verified after
	/// rebasing onto the merged #1003/#1004) removes
	/// migration 0052's admin-stated benchmark_component_mappings.is_srg_no_benchmark
	/// column and its mutual-exclusivity CHECK: SRG participation in benchmark mapping
	/// is now a DERIVED read-state (Waypoint.Api joins the component's bound catalog
	/// content kind), never stored, never admin-settable. Least-lossy history shape:
	/// any row that had is_srg_no_benchmark = true gets its historical fact folded into
	/// its own free-text `reason` column BEFORE the column drops, so an old mapping
	/// decision's audit trail still explains itself; no new runner grants, no other
	/// schema changes, issue #1002 --
	///
	/// 0072 (issue #1007, epic #726; slot verified free against both the migrations
	/// directory and open PRs at this migration's own commit time): merges a
	/// duplicate catalog PRODUCT tree the content-pull importer's pre-fix bug created
	/// (ContentPullJobHandler passed a display string instead of the seed migrations'
	/// literal 'vmware' as catalog_products.vendor, defeating the
	/// catalog_products_vendor_key_unique upsert) back onto the canonical
	/// 'vmware'-vendor row -- re-points every dependent table down through
	/// catalog_product_versions/catalog_components/catalog_execution_profiles AND the
	/// external tables that reference them (components, benchmark_component_mappings,
	/// baselines, scan_plan_items, config_docs), deterministic adopt-or-drop per table,
	/// no other schema changes, no new runner grants, issue #1007 --
	/// bump this alongside adding a new <c>Data/Migrations/*.sql</c> file.</summary>
	private const int ExpectedMigrationCount = 71;

	private readonly PostgresFixture _fixture;

	public SchemaMigrationTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	/// <summary>
	/// The core acceptance criterion, proven in one deterministic sequence rather than
	/// as separately-ordered [Fact]s (xUnit does not guarantee inter-test ordering):
	/// (1) applying to a genuinely fresh database creates the full M1 schema; (2)
	/// re-running the migrator against that now-migrated database is a no-op via the
	/// schema_migrations tracking table; (3) re-running the embedded migration SQL
	/// directly — bypassing the tracking table entirely — is *also* a no-op, proving
	/// the SQL itself is idempotent (IF NOT EXISTS / OR REPLACE / ON CONFLICT), not
	/// just the runner around it.
	/// </summary>
	[Fact]
	public async Task Migrations_ApplyFreshThenReapplyAllViaRunnerAndRawSql_AreAllIdempotent()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);

		// (1) Fresh apply.
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		foreach (string table in ExpectedTables)
		{
			Assert.True(await TableExistsAsync(connection, table), $"Expected table '{table}' to exist after a fresh migration.");
		}

		// Six embedded migrations through issue #180: initial schema, running-lease CHECK,
		// the aborted-run queued-job invariant, the resolved-auth-outcome index, and the
		// credential queue-halt state/trigger, and the audit-survives-delete FK (0006) -- ExpectedMigrationCount below is
		// the single place this test's own count assertions read from.
		Assert.Equal(ExpectedMigrationCount, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM appliance_state"));

		// (2) Re-apply via the runner: schema_migrations already has every version, so
		// this must be a pure no-op — not an error, not a second tracking row per version.
		await migrator.ApplyAsync();
		Assert.Equal(ExpectedMigrationCount, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM appliance_state"));

		foreach (string table in ExpectedTables)
		{
			Assert.True(await TableExistsAsync(connection, table));
		}

		// (3) Re-run every embedded migration's raw SQL directly, in order, bypassing
		// the tracking table entirely. If any statement in any migration lacked
		// IF NOT EXISTS/OR REPLACE/ON CONFLICT (or, for 0002's constraint, the
		// DROP CONSTRAINT IF EXISTS + ADD CONSTRAINT idiom), this throws.
		foreach (string sql in await ReadEmbeddedMigrationSqlInOrderAsync())
		{
			await using NpgsqlCommand rawReapply = new(sql, connection);
			await rawReapply.ExecuteNonQueryAsync();
		}

		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM appliance_state"));
	}

	[Fact]
	public async Task Migrations_ResolvedCredentialOutcomeIndex_MatchesWindowOrder()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT indexdef FROM pg_indexes WHERE indexname = 'idx_jobs_credential_resolved_outcomes'", connection);
		string definition = Assert.IsType<string>(await command.ExecuteScalarAsync());
		Assert.Contains("credential_id, finished_at DESC, id DESC", definition, StringComparison.Ordinal);
		Assert.Contains("finished_at IS NOT NULL", definition, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Migrations_QueueClaimIndex_ExistsAndIsPartial()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// The claim query is `WHERE state = 'queued' AND job_type = ANY($3) ORDER BY
		// priority, created_at` (issue #435/ADR-0014); the index must both exist and
		// lead with job_type so the allowlist predicate stays index-supported instead
		// of scanning every claimable row (0024_jobs_queue_claim_job_type_index.sql).
		await using NpgsqlCommand command = new(
			"SELECT indexdef FROM pg_indexes WHERE indexname = 'idx_jobs_queue_claim'", connection);
		object? indexDefinition = await command.ExecuteScalarAsync();

		Assert.NotNull(indexDefinition);
		string definition = (string)indexDefinition!;
		Assert.Contains("WHERE (state = 'queued'::text)", definition, StringComparison.Ordinal);
		Assert.Contains("(job_type, priority, created_at)", definition, StringComparison.Ordinal);
	}

	/// <summary>
	/// <c>seq</c> is assigned by <c>trg_job_events_assign_seq</c>, not by an identity
	/// column, because the trigger has to take the ordering advisory lock *before* the
	/// value is drawn (identity defaults are evaluated before BEFORE-row triggers run).
	/// Two things must therefore hold: the trigger is installed as a row-level
	/// <c>BEFORE INSERT</c> trigger, and a client-supplied <c>seq</c> is discarded — the
	/// server is the only assigner, exactly as <c>GENERATED ALWAYS</c> guaranteed before.
	/// <see cref="JobEventsSeqTests"/> proves the ordering property the trigger exists for.
	/// </summary>
	[Fact]
	public async Task Migrations_JobEventsSeq_IsServerAssignedByTheOrderingTrigger()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand triggerQuery = new(
			"""
			SELECT action_timing, event_manipulation, action_orientation
			FROM information_schema.triggers
			WHERE event_object_table = 'job_events' AND trigger_name = 'trg_job_events_assign_seq'
			""", connection))
		{
			await using NpgsqlDataReader reader = await triggerQuery.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync(), "trg_job_events_assign_seq is not installed on job_events.");
			Assert.Equal("BEFORE", reader.GetString(0));
			Assert.Equal("INSERT", reader.GetString(1));
			Assert.Equal("ROW", reader.GetString(2));
		}

		// A client-supplied seq must be overwritten by the server-assigned one; if it
		// were not, a writer could hand itself a value outside the commit ordering.
		// 'queued' rather than 'running': this seed only needs a job_id for the FK, and
		// a bare 'running' row (no lease) is no longer representable since
		// jobs_running_requires_lease_check landed (issue #107).
		await using NpgsqlCommand seedJob = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('catalog-index', 1, 'queued') RETURNING id",
			connection);
		Guid jobId = (Guid)(await seedJob.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertEvent = new(
			"""
			INSERT INTO job_events (seq, job_id, event_type)
			VALUES (9223372036854775807, $1, 'job.log')
			RETURNING seq
			""", connection);
		insertEvent.Parameters.AddWithValue(jobId);

		Assert.NotEqual(long.MaxValue, (long)(await insertEvent.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #106: <c>job_events</c> is documented append-only (0001's header comment,
	/// docs/api-contract.md's schema sketch) but nothing enforced it. 0020's trigger must
	/// reject both mutation forms outright -- no writer legitimately UPDATEs or DELETEs a
	/// committed row (<see cref="Waypoint.Runner.Jobs.JobEventPublisher"/> only
	/// INSERTs).
	/// </summary>
	[Fact]
	public async Task Migrations_JobEvents_RejectsUpdateAndDelete()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand seedJob = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('catalog-index', 1, 'queued') RETURNING id",
			connection);
		Guid jobId = (Guid)(await seedJob.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertEvent = new(
			"INSERT INTO job_events (job_id, event_type) VALUES ($1, 'job.log') RETURNING seq",
			connection);
		insertEvent.Parameters.AddWithValue(jobId);
		long seq = (long)(await insertEvent.ExecuteScalarAsync())!;

		await using NpgsqlCommand update = new(
			"UPDATE job_events SET event_type = 'job.state' WHERE seq = $1", connection);
		update.Parameters.AddWithValue(seq);
		PostgresException updateEx = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Contains("append-only", updateEx.MessageText, StringComparison.Ordinal);

		await using NpgsqlCommand delete = new("DELETE FROM job_events WHERE seq = $1", connection);
		delete.Parameters.AddWithValue(seq);
		PostgresException deleteEx = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Contains("append-only", deleteEx.MessageText, StringComparison.Ordinal);

		// The row must still be exactly as inserted -- neither rejected statement should
		// have partially applied.
		await using NpgsqlCommand verify = new("SELECT event_type FROM job_events WHERE seq = $1", connection);
		verify.Parameters.AddWithValue(seq);
		Assert.Equal("job.log", (string)(await verify.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #106: <c>audit_log</c> carries the same append-only claim as
	/// <c>job_events</c>, backed by docs/security.md control 4 (the decrypt audit trail
	/// that compensates for the service/shared credential exposure tier) -- a trail the
	/// compromised component can edit compensates for nothing. Direct UPDATE and DELETE
	/// must both fail.
	/// </summary>
	[Fact]
	public async Task Migrations_AuditLog_RejectsUpdateAndDelete()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO audit_log (event_type, actor) VALUES ('credential.tested', 'test-actor') RETURNING id",
			connection);
		Guid id = (Guid)(await insert.ExecuteScalarAsync())!;

		await using NpgsqlCommand update = new("UPDATE audit_log SET actor = 'someone-else' WHERE id = $1", connection);
		update.Parameters.AddWithValue(id);
		PostgresException updateEx = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Contains("append-only", updateEx.MessageText, StringComparison.Ordinal);

		await using NpgsqlCommand delete = new("DELETE FROM audit_log WHERE id = $1", connection);
		delete.Parameters.AddWithValue(id);
		PostgresException deleteEx = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Contains("append-only", deleteEx.MessageText, StringComparison.Ordinal);

		await using NpgsqlCommand verify = new("SELECT actor FROM audit_log WHERE id = $1", connection);
		verify.Parameters.AddWithValue(id);
		Assert.Equal("test-actor", (string)(await verify.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #106's carve-out: 0006 added
	/// <c>audit_log.credential_id ... ON DELETE SET NULL</c> so a credential delete
	/// doesn't 500 against the audit trail that should outlive it
	/// (<c>CredentialRepository.DeleteAsync</c>). That FK action performs its nulling as
	/// a real UPDATE against audit_log -- the 0020 trigger must let exactly that shape
	/// through (credential_id non-null -> NULL, every other column unchanged) while
	/// still blocking every other mutation. This proves the carve-out works end to end
	/// via an actual credential DELETE, not a hand-crafted UPDATE.
	/// </summary>
	[Fact]
	public async Task Migrations_AuditLog_CredentialDeleteStillNullsCredentialId()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand seedCredential = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
		seedCredential.Parameters.AddWithValue($"test-cred-{Guid.NewGuid():N}");
		Guid credentialId = (Guid)(await seedCredential.ExecuteScalarAsync())!;

		await using NpgsqlCommand insert = new(
			"INSERT INTO audit_log (event_type, actor, credential_id) VALUES ('credential.tested', 'test-actor', $1) RETURNING id",
			connection);
		insert.Parameters.AddWithValue(credentialId);
		Guid auditId = (Guid)(await insert.ExecuteScalarAsync())!;

		await using NpgsqlCommand deleteCredential = new("DELETE FROM credentials WHERE id = $1", connection);
		deleteCredential.Parameters.AddWithValue(credentialId);
		await deleteCredential.ExecuteNonQueryAsync();

		await using (NpgsqlCommand verify = new(
			"SELECT credential_id, actor, event_type FROM audit_log WHERE id = $1", connection))
		{
			verify.Parameters.AddWithValue(auditId);
			await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync(), "audit_log row must survive the credential delete (0006).");
			Assert.True(reader.IsDBNull(0), "credential_id must be nulled by the FK action.");
			Assert.Equal("test-actor", reader.GetString(1));
			Assert.Equal("credential.tested", reader.GetString(2));
		}

		// Confirm the carve-out is narrow: a direct attempt to null credential_id via an
		// UPDATE that *also* touches another column must still be rejected -- only the
		// exact FK-driven shape (credential_id alone, non-null -> NULL) is permitted.
		await using NpgsqlCommand seedCredential2 = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
		seedCredential2.Parameters.AddWithValue($"test-cred-{Guid.NewGuid():N}");
		Guid credentialId2 = (Guid)(await seedCredential2.ExecuteScalarAsync())!;

		await using NpgsqlCommand insert2 = new(
			"INSERT INTO audit_log (event_type, actor, credential_id) VALUES ('credential.tested', 'test-actor', $1) RETURNING id",
			connection);
		insert2.Parameters.AddWithValue(credentialId2);
		Guid auditId2 = (Guid)(await insert2.ExecuteScalarAsync())!;

		await using NpgsqlCommand disallowedUpdate = new(
			"UPDATE audit_log SET credential_id = NULL, actor = 'attacker' WHERE id = $1", connection);
		disallowedUpdate.Parameters.AddWithValue(auditId2);
		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => disallowedUpdate.ExecuteNonQueryAsync());
		Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
	}

	/// <summary>
	/// The migrator's observability is a claim <c>backend/README.md</c> makes and startup
	/// diagnosis depends on: an operator staring at a slow boot needs to see which
	/// migration is running. Asserted against a genuinely fresh database created for this
	/// test, so the fresh-apply path is deterministic rather than dependent on which test
	/// class in the shared "Postgres" collection happened to run first.
	/// </summary>
	[Fact]
	public async Task Migrations_LogWhichVersionTheyApply_ThenLogSkippingItOnReapply()
	{
		string connectionString = await CreateFreshDatabaseAsync();
		CollectingLogger logger = new();
		NpgsqlSchemaMigrator migrator = new(connectionString, logger);

		await migrator.ApplyAsync();

		Assert.Contains(logger.Messages, message => message == "Applying migration 0001_initial_schema");
		Assert.Contains(logger.Messages, message => message == "Applied migration 0001_initial_schema");

		logger.Messages.Clear();
		await migrator.ApplyAsync();

		Assert.Contains(logger.Messages, message => message == "Migration 0001_initial_schema already applied, skipping");
		Assert.DoesNotContain(logger.Messages, message => message.StartsWith("Applying migration", StringComparison.Ordinal));
	}

	/// <summary>
	/// Reproduces issue #108's exact failure and proves the fix: a session holding the
	/// migrator's advisory lock (key <c>875190001</c>, see
	/// <see cref="NpgsqlSchemaMigrator"/>) for longer than Npgsql's default 30s
	/// <see cref="NpgsqlCommand.CommandTimeout"/> must not make a second instance's
	/// <see cref="NpgsqlSchemaMigrator.ApplyAsync"/> throw. Before the fix, the
	/// lock-acquire command inherited that 30s default and the second instance's
	/// <c>SELECT pg_advisory_lock($1)</c> failed with a client-side
	/// <see cref="TimeoutException"/> at the 30s mark (verified manually while filing
	/// #108: exit=1 after 31s, zero tables created). This holds the lock for 32s — just
	/// past the old default — and asserts <see cref="NpgsqlSchemaMigrator.ApplyAsync"/>
	/// instead blocks for the hold and then completes successfully once released.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WaitsPastTheOldThirtySecondDefault_InsteadOfTimingOut()
	{
		string connectionString = await CreateFreshDatabaseAsync();

		await using NpgsqlConnection holder = new(connectionString);
		await holder.OpenAsync();
		await using (NpgsqlCommand acquire = new("SELECT pg_advisory_lock(875190001)", holder))
		{
			await acquire.ExecuteNonQueryAsync();
		}

		TimeSpan holdDuration = TimeSpan.FromSeconds(32);
		Task releaseAfterHold = Task.Run(async () =>
		{
			await Task.Delay(holdDuration);
			await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(875190001)", holder);
			await release.ExecuteNonQueryAsync();
		});

		NpgsqlSchemaMigrator migrator = new(connectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);

		DateTimeOffset started = DateTimeOffset.UtcNow;
		await migrator.ApplyAsync();
		TimeSpan elapsed = DateTimeOffset.UtcNow - started;

		await releaseAfterHold;

		Assert.True(
			elapsed >= holdDuration - TimeSpan.FromSeconds(1),
			$"ApplyAsync returned after only {elapsed.TotalSeconds:F1}s, before the {holdDuration.TotalSeconds:F0}s lock hold released -- it should have blocked, not raced past a still-held lock.");

		await using NpgsqlConnection verify = new(connectionString);
		await verify.OpenAsync();
		Assert.True(await TableExistsAsync(verify, "schema_migrations"), "ApplyAsync should have completed the migration once the lock was released.");
	}

	/// <summary>
	/// Issue #231: a token cancelled before <see cref="NpgsqlSchemaMigrator.ApplyAsync"/>
	/// is even called must abort promptly with <see cref="OperationCanceledException"/>
	/// rather than run to completion regardless -- proving the token actually reaches the
	/// commands the runner issues (the connection open, the advisory-lock acquire, the
	/// migrations-table bootstrap) instead of being accepted and ignored. No migration may
	/// be recorded as applied, since nothing should have progressed past the very first
	/// cancellation check.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WithAlreadyCancelledToken_ThrowsAndAppliesNothing()
	{
		string connectionString = await CreateFreshDatabaseAsync();
		NpgsqlSchemaMigrator migrator = new(connectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);

		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migrator.ApplyAsync(cts.Token));

		// schema_migrations may or may not exist depending on exactly where cancellation
		// was observed, but it must never report a migration as applied.
		await using NpgsqlConnection verify = new(connectionString);
		await verify.OpenAsync();
		if (await TableExistsAsync(verify, "schema_migrations"))
		{
			Assert.Equal(0, await CountAsync(verify, "SELECT count(*) FROM schema_migrations"));
		}
	}

	/// <summary>
	/// Issue #231 (deferred third bullet of #108, more urgent after #229 made the
	/// advisory-lock acquire wait unbounded): a caller blocked waiting on a lock another
	/// session holds must be released by cancelling the token, not left to block
	/// indefinitely with nothing able to interrupt it. Mirrors
	/// <see cref="ApplyAsync_WaitsPastTheOldThirtySecondDefault_InsteadOfTimingOut"/>'s
	/// held-lock setup, but cancels shortly after starting instead of waiting out a timed
	/// hold, and asserts the wait is aborted promptly (well under the hold duration)
	/// rather than completed once released.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_CancelledWhileBlockedOnAdvisoryLock_AbortsPromptly()
	{
		string connectionString = await CreateFreshDatabaseAsync();

		await using NpgsqlConnection holder = new(connectionString);
		await holder.OpenAsync();
		await using (NpgsqlCommand acquire = new("SELECT pg_advisory_lock(875190001)", holder))
		{
			await acquire.ExecuteNonQueryAsync();
		}

		try
		{
			NpgsqlSchemaMigrator migrator = new(connectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);

			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

			DateTimeOffset started = DateTimeOffset.UtcNow;
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migrator.ApplyAsync(cts.Token));
			TimeSpan elapsed = DateTimeOffset.UtcNow - started;

			Assert.True(
				elapsed < TimeSpan.FromSeconds(15),
				$"ApplyAsync took {elapsed.TotalSeconds:F1}s to observe cancellation while blocked on the advisory lock -- it should have aborted shortly after the ~3s cancellation, not run indefinitely.");
		}
		finally
		{
			await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(875190001)", holder);
			await release.ExecuteNonQueryAsync();
		}
	}

	/// <summary>
	/// Issue #252: 0022 adds <c>credentials_credential_type_check</c>, the DB-level
	/// mirror of <c>Waypoint.Core.Secrets.CredentialTypes.All</c> that migration 0010
	/// deliberately deferred (see that migration's comment). A bogus type must now be
	/// rejected at the database, not just by <c>CredentialsController</c>'s API-layer
	/// validation.
	/// </summary>
	[Fact]
	public async Task Migrations_Credentials_RejectsInvalidCredentialType()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'bogus-type') RETURNING id", connection);
		insert.Parameters.AddWithValue($"test-cred-{Guid.NewGuid():N}");

		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteScalarAsync());
		Assert.Equal("23514", ex.SqlState); // check_violation
		Assert.Contains("credentials_credential_type_check", ex.MessageText, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #252, extended by issue #690 (migration 0047): every value in the closed
	/// <c>CredentialTypes.All</c> set -- including the deprecated legacy
	/// <c>depot-token</c> alias (retained, non-destructively, for pre-#690 rows) and its
	/// two replacements <c>depot-activation-code</c>/<c>legacy-download-token</c> --
	/// must still insert cleanly under the CHECK.
	/// </summary>
	[Theory]
	[InlineData("vcenter")]
	[InlineData("nsx")]
	[InlineData("ssh")]
	[InlineData("token")]
	[InlineData("depot-token")]
	[InlineData("depot-activation-code")]
	[InlineData("legacy-download-token")]
	public async Task Migrations_Credentials_AcceptsEveryClosedSetCredentialType(string credentialType)
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, $2) RETURNING id", connection);
		insert.Parameters.AddWithValue($"test-cred-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialType);

		object? id = await insert.ExecuteScalarAsync();
		Assert.NotNull(id);

		// Clean up: a row of a type introduced by a LATER migration than 0022 (e.g.
		// 'depot-activation-code'/'legacy-download-token', issue #690's 0047) must not
		// linger in the shared PostgresFixture database for
		// Migrations_ApplyFreshThenReapplyAllViaRunnerAndRawSql_AreAllIdempotent's raw,
		// in-order SQL replay to trip over -- that test re-executes 0022's own (older,
		// narrower) DROP+ADD CONSTRAINT verbatim, which would otherwise reject on a row
		// only the LATER 0047 CHECK permits, purely due to shared-fixture test-method
		// ordering rather than any real migration defect.
		await using NpgsqlCommand delete = new("DELETE FROM credentials WHERE id = $1", connection);
		delete.Parameters.AddWithValue((Guid)id!);
		await delete.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Issue #512: 0031's <c>users_role_check</c> mirrors <c>WaypointRole</c>'s closed
	/// set -- a bogus role must be rejected at the database, not just by
	/// <c>UsersController</c>'s API-layer validation.
	/// </summary>
	[Fact]
	public async Task Migrations_Users_RejectsInvalidRole()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO users (oidc_sub, username, role, auth_method) VALUES ($1, 'test', 'SuperAdmin', 'oidc') RETURNING id", connection);
		insert.Parameters.AddWithValue($"sub-{Guid.NewGuid():N}");

		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteScalarAsync());
		Assert.Equal("23514", ex.SqlState); // check_violation
		Assert.Contains("users_role_check", ex.MessageText, StringComparison.Ordinal);
	}

	/// <summary>Issue #512: 0031's <c>users_auth_method_check</c> closes the set to exactly the two schemes this backend registers (<c>OidcOrLocalPolicySchemeDefaults</c>).</summary>
	[Fact]
	public async Task Migrations_Users_RejectsInvalidAuthMethod()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO users (oidc_sub, username, role, auth_method) VALUES ($1, 'test', 'Viewer', 'saml') RETURNING id", connection);
		insert.Parameters.AddWithValue($"sub-{Guid.NewGuid():N}");

		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteScalarAsync());
		Assert.Equal("23514", ex.SqlState); // check_violation
		Assert.Contains("users_auth_method_check", ex.MessageText, StringComparison.Ordinal);
	}

	/// <summary>Issue #512: 0031's <c>users_oidc_sub_key</c> is the upsert's ON CONFLICT target -- a duplicate must be rejected at the database.</summary>
	[Fact]
	public async Task Migrations_Users_RejectsDuplicateOidcSub()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		string sub = $"sub-{Guid.NewGuid():N}";
		await using (NpgsqlCommand first = new(
			"INSERT INTO users (oidc_sub, username, role, auth_method) VALUES ($1, 'test-1', 'Viewer', 'oidc')", connection))
		{
			first.Parameters.AddWithValue(sub);
			await first.ExecuteNonQueryAsync();
		}

		await using NpgsqlCommand second = new(
			"INSERT INTO users (oidc_sub, username, role, auth_method) VALUES ($1, 'test-2', 'Viewer', 'oidc')", connection);
		second.Parameters.AddWithValue(sub);

		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
		Assert.Equal("23505", ex.SqlState); // unique_violation
	}

	/// <summary>
	/// Issue #515: 0032's <c>runs_schedule_id_fkey</c> must actually reject an orphan
	/// <c>schedule_id</c> -- 0001 declared the column but with no constraint at all
	/// (a deliberate forward reference before <c>schedules</c> existed).
	/// </summary>
	[Fact]
	public async Task Migrations_Runs_RejectsUnknownScheduleId()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, schedule_id) VALUES ('discover', $1) RETURNING id", connection);
		insert.Parameters.AddWithValue(Guid.NewGuid());

		PostgresException ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteScalarAsync());
		Assert.Equal("23503", ex.SqlState); // foreign_key_violation
		Assert.Contains("runs_schedule_id_fkey", ex.MessageText, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #515: a manually-created run (the vast majority -- every controller-driven
	/// <c>POST /runs</c>, <c>/discover</c>, <c>/catalog-index</c>, <c>/credential-test</c>)
	/// must still leave <c>schedule_id</c> NULL; only the dispatcher stamps it
	/// (<see cref="Waypoint.Tests.Infrastructure.Postgres.ScheduleDispatchServiceTests"/>
	/// covers the dispatcher's own stamping end to end).
	/// </summary>
	[Fact]
	public async Task Migrations_Runs_ManuallyCreatedRun_HasNullScheduleId()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type) VALUES ('discover') RETURNING id", connection);
		Guid runId = (Guid)(await insert.ExecuteScalarAsync())!;

		await using NpgsqlCommand verify = new("SELECT schedule_id FROM runs WHERE id = $1", connection);
		verify.Parameters.AddWithValue(runId);
		Assert.Equal(DBNull.Value, await verify.ExecuteScalarAsync());
	}

	/// <summary>
	/// Issue #728, finding 3: the closed capability vocabulary lives in two hand-maintained
	/// copies -- the C# <c>Catalog*</c>/<c>CredentialPurposes</c> constants and migration
	/// 0050's CHECK constraint value lists. This repo's convention is a class-killing drift
	/// guard (cf. <see cref="Migrations_Credentials_AcceptsEveryClosedSetCredentialType"/>
	/// mirroring <c>CredentialTypes</c>, and docs/testing.md's "read it off the detector"
	/// derivation). This parses each closed set's value list OUT OF the authoritative 0050
	/// migration text (embedded resource) and asserts set-equality with the C# constants, so
	/// adding/removing a value on either side without the other fails here -- drift in either
	/// direction, not just additions.
	///
	/// Issue #977 widened <c>catalog_credential_requirements_purpose_check</c> via a LATER
	/// migration (0069's DROP CONSTRAINT IF EXISTS + ADD CONSTRAINT idiom, matching 0022's
	/// own precedent for widening a closed-vocabulary CHECK -- see
	/// <see cref="Migrations_Credentials_AcceptsEveryClosedSetCredentialType"/>'s comment for
	/// why 0047's widening of 0022's constraint also has no static-parse-of-0050-equivalent
	/// test: a migration file's own literal is a point-in-time historical fact, not a live
	/// query of the CURRENT constraint). So this one assertion parses 0050's ORIGINAL four
	/// purposes only -- 0069's added 'vcf-api' purpose is proven instead by
	/// <see cref="Migration0069_WidensCredentialPurposeCheck_ToAdmitVcfApi"/> against the
	/// live, fully-migrated database, the same live-proof idiom already established for
	/// 0047's widening.
	/// </summary>
	[Fact]
	public async Task Migration0050_CheckConstraintValueLists_MatchTheCSharpClosedVocabulary()
	{
		string migration = await ReadMigration0050SqlAsync();

		Assert.Equal(
			CatalogKinds.All.OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_content_releases_kind_check"));
		Assert.Equal(
			CatalogTransports.All.OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_components_transport_check"));
		Assert.Equal(
			CatalogSelectorKinds.All.OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_components_selector_kind_check"));
		Assert.Equal(
			CatalogOutputKinds.All.OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_execution_profiles_output_kind_check"));
		Assert.Equal(
			CredentialPurposes.All.Where(p => p != CredentialPurposes.VcfApi).OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_credential_requirements_purpose_check"));
	}

	/// <summary>
	/// Issue #977: live-database companion to
	/// <see cref="Migration0050_CheckConstraintValueLists_MatchTheCSharpClosedVocabulary"/>'s
	/// deliberately-narrowed 0050-only assertion above -- proves the CURRENT (post-0069)
	/// constraint accepts every value in <see cref="CredentialPurposes"/>'s closed set,
	/// including 'vcf-api', mirroring <see cref="Migrations_Credentials_AcceptsEveryClosedSetCredentialType"/>'s
	/// exact live-proof idiom for 0047's analogous widening of 0022's constraint.
	/// </summary>
	[Theory]
	[InlineData("vsphere-api")]
	[InlineData("vcsa-ssh")]
	[InlineData("nsx-api")]
	[InlineData("srg-ssh")]
	[InlineData("vcf-api")]
	public async Task Migration0069_WidensCredentialPurposeCheck_ToAdmitVcfApi(string purpose)
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// A real catalog_execution_profiles row is required to satisfy the FK -- reuse
		// migration 0069's own seeded vcf-api component/profile, present on every fresh
		// migration.
		await using NpgsqlCommand selectProfile = new(
			"SELECT id FROM catalog_execution_profiles LIMIT 1", connection);
		object? executionProfileId = await selectProfile.ExecuteScalarAsync();
		Assert.NotNull(executionProfileId);

		await using NpgsqlCommand insert = new(
			"INSERT INTO catalog_credential_requirements (execution_profile_id, purpose) VALUES ($1, $2) ON CONFLICT (execution_profile_id, purpose) DO NOTHING RETURNING id",
			connection);
		insert.Parameters.AddWithValue(executionProfileId!);
		insert.Parameters.AddWithValue(purpose);

		// Either a new row is inserted, or the (execution_profile_id, purpose) pair
		// already exists from seeding (e.g. 'vcf-api' itself) -- both are proof the
		// CHECK constraint accepted the value; only a check_violation would fail this.
		await insert.ExecuteScalarAsync();
	}

	/// <summary>
	/// Issue #729: the same class-killing drift guard as
	/// <see cref="Migration0050_CheckConstraintValueLists_MatchTheCSharpClosedVocabulary"/>,
	/// for migration 0051's own closed vocabulary
	/// (<see cref="CatalogImportEntryDispositions"/> vs.
	/// <c>catalog_import_report_entries_disposition_check</c>).
	/// </summary>
	[Fact]
	public async Task Migration0051_CheckConstraintValueList_MatchesTheCSharpClosedVocabulary()
	{
		string migration = await ReadMigrationSqlAsync("0051_catalog_import_reports.sql");

		Assert.Equal(
			CatalogImportEntryDispositions.All.OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_import_report_entries_disposition_check"));
	}

	/// <summary>
	/// Issue #1002: proves migration 0071's historical-preservation shape directly
	/// against its own SQL text -- the least-lossy behavior a fully-migrated database
	/// can no longer exercise (the column is gone by the time <c>ApplyAsync</c>
	/// finishes, so this reconstructs the pre-0071 shape by hand, seeds a legacy row the
	/// same shape an old <c>SetMappingAsync</c> caller would have produced, then
	/// executes 0071's own embedded SQL text against it -- the same "run the real
	/// migration text" idiom <see cref="Migrations_FullPipeline_IsIdempotentAndComplete"/>
	/// already establishes for raw re-apply). A row with a blank reason gets the
	/// synthesized note appended; a row with an existing informative reason keeps it,
	/// with the note appended rather than replacing it -- audit text is never
	/// discarded, only extended.
	/// </summary>
	[Fact]
	public async Task Migration0071_BackfillsReasonBeforeDroppingColumn_PreservingHistoryHonestly()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Reconstruct the pre-0071 shape: 0071 already dropped the column on this fully
		// migrated database, so add it back exactly as 0052 first declared it, then seed
		// two legacy rows: one with no reason at all (the synthesized note becomes the
		// WHOLE reason), one with an existing reason (the note is appended, not
		// replacing what a real Admin/system caller already recorded).
		await using (NpgsqlCommand addColumnBack = new(
			"ALTER TABLE benchmark_component_mappings ADD COLUMN is_srg_no_benchmark BOOLEAN NOT NULL DEFAULT false",
			connection))
		{
			await addColumnBack.ExecuteNonQueryAsync();
		}

		await using NpgsqlCommand insertSourceRevision = new(
			"INSERT INTO catalog_source_revisions (revision_key) VALUES ('migration-0071-test') RETURNING id", connection);
		Guid sourceRevisionId = (Guid)(await insertSourceRevision.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertProduct = new(
			"INSERT INTO catalog_products (source_revision_id, vendor, product_key, display_name) VALUES ($1, 'vmware', 'migration-0071-test', 'Test') RETURNING id", connection);
		insertProduct.Parameters.AddWithValue(sourceRevisionId);
		Guid productId = (Guid)(await insertProduct.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertVersion = new(
			"INSERT INTO catalog_product_versions (product_id, version_key, display_name) VALUES ($1, '1.0.0', 'Test 1.0') RETURNING id", connection);
		insertVersion.Parameters.AddWithValue(productId);
		Guid versionId = (Guid)(await insertVersion.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertComponentBlankReason = new(
			"INSERT INTO catalog_components (product_version_id, component_key, display_name, transport, selector_kind) VALUES ($1, 'blank-reason', 'Blank Reason', 'vmware', 'vcenter') RETURNING id", connection);
		insertComponentBlankReason.Parameters.AddWithValue(versionId);
		Guid blankReasonComponentId = (Guid)(await insertComponentBlankReason.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertComponentExistingReason = new(
			"INSERT INTO catalog_components (product_version_id, component_key, display_name, transport, selector_kind) VALUES ($1, 'existing-reason', 'Existing Reason', 'vmware', 'vcenter') RETURNING id", connection);
		insertComponentExistingReason.Parameters.AddWithValue(versionId);
		Guid existingReasonComponentId = (Guid)(await insertComponentExistingReason.ExecuteScalarAsync())!;

		await using NpgsqlCommand insertBlankReasonMapping = new(
			"""
			INSERT INTO benchmark_component_mappings (catalog_component_id, status, is_srg_no_benchmark, is_current, reason)
			VALUES ($1, 'unmapped', true, true, NULL)
			""", connection);
		insertBlankReasonMapping.Parameters.AddWithValue(blankReasonComponentId);
		await insertBlankReasonMapping.ExecuteNonQueryAsync();

		await using NpgsqlCommand insertExistingReasonMapping = new(
			"""
			INSERT INTO benchmark_component_mappings (catalog_component_id, status, is_srg_no_benchmark, is_current, reason)
			VALUES ($1, 'unmapped', true, true, 'SRG content has no published DISA benchmark')
			""", connection);
		insertExistingReasonMapping.Parameters.AddWithValue(existingReasonComponentId);
		await insertExistingReasonMapping.ExecuteNonQueryAsync();

		// Now execute 0071's own embedded SQL text -- the exact statements a fresh
		// database ran, applied here against this hand-reconstructed pre-state.
		string migration0071 = await ReadMigrationSqlAsync("0071_drop_srg_no_benchmark_flag.sql");
		await using (NpgsqlCommand applyMigration0071 = new(migration0071, connection))
		{
			await applyMigration0071.ExecuteNonQueryAsync();
		}

		// The column is gone again.
		Assert.False(await ColumnExistsAsync(connection, "benchmark_component_mappings", "is_srg_no_benchmark"));

		await using NpgsqlCommand selectBlankReason = new(
			"SELECT reason FROM benchmark_component_mappings WHERE catalog_component_id = $1", connection);
		selectBlankReason.Parameters.AddWithValue(blankReasonComponentId);
		string blankReasonResult = (string)(await selectBlankReason.ExecuteScalarAsync())!;
		Assert.Equal("[historical: recorded is_srg_no_benchmark=true before issue #1002 made SRG mapping status a derived read-state]", blankReasonResult);

		await using NpgsqlCommand selectExistingReason = new(
			"SELECT reason FROM benchmark_component_mappings WHERE catalog_component_id = $1", connection);
		selectExistingReason.Parameters.AddWithValue(existingReasonComponentId);
		string existingReasonResult = (string)(await selectExistingReason.ExecuteScalarAsync())!;
		Assert.StartsWith("SRG content has no published DISA benchmark", existingReasonResult, StringComparison.Ordinal);
		Assert.Contains("[historical: recorded is_srg_no_benchmark=true", existingReasonResult, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1007's migration replay proof: reconstructs the exact real-world
	/// two-tree state a pre-fix content pull left behind (a seeded 'vmware'-vendor
	/// vsphere/8.0 tree with an active baseline, PLUS a second 'VMware vSphere'-vendor
	/// vsphere/8.0 tree the importer's bug created with its OWN component, execution
	/// profile, and a discovered instance component already linked to it), then runs
	/// migration 0072's own embedded SQL text against that reconstructed pre-state --
	/// the same "run the real migration text" idiom
	/// <see cref="Migration0071_BackfillsReasonBeforeDroppingColumn_PreservingHistoryHonestly"/>
	/// already establishes. Asserts: exactly one catalog_products/catalog_product_versions/
	/// catalog_components row survives per natural key, the discovered component's
	/// catalog_component_id is re-pointed onto the surviving canonical component (proving
	/// CatalogLinkageResolver would now find exactly one candidate, not two), the
	/// duplicate's execution profile's declared input/credential requirement/baseline
	/// were preserved by re-pointing rather than silently dropped, and re-running the
	/// same SQL text a second time (replay-safety) is a clean no-op.
	/// </summary>
	[Fact]
	public async Task Migration0072_MergesDuplicateVendorProductTree_ReattachesChildren_IdempotentOnReplay()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Canonical (seeded) tree: vendor = 'vmware', matching every seed migration's
		// literal.
		Guid seedRevisionId = await InsertSourceRevisionAsync(connection, "migration-0072-seed");
		Guid canonicalProductId = await InsertProductAsync(connection, seedRevisionId, "vmware", "migration-0072-test", "Test Product");
		Guid canonicalVersionId = await InsertProductVersionAsync(connection, canonicalProductId, "8.0", "Test Product 8.0");
		Guid canonicalComponentId = await InsertComponentAsync(connection, canonicalVersionId, "vcenter", "vCenter Server");
		Guid canonicalReleaseId = await InsertContentReleaseAsync(connection, "stig", "migration-0072-test:stig:release-1", "Test STIG Release");
		Guid canonicalGroupId = await InsertReportGroupAsync(connection, "migration-0072-vcenter-stig", "vCenter STIG", 3);
		Guid canonicalProfileId = await InsertExecutionProfileAsync(connection, canonicalComponentId, canonicalReleaseId, canonicalGroupId, "v1", "hdf_ckl");
		Guid canonicalContentRevisionId = await InsertContentRevisionAsync(connection, "migration-0072-canonical-commit", "migration-0072-canonical-digest");
		Guid canonicalBaselineId = await InsertActiveBaselineAsync(connection, canonicalContentRevisionId, canonicalProfileId);

		// Duplicate (importer pre-fix) tree: SAME product_key, DIFFERENT vendor value
		// (the display string the bug passed) -- its own product/version/component/
		// execution-profile/baseline, plus a discovered instance component already
		// linked to the duplicate's catalog component (the round-7 shape: discovery
		// ran against the bad tree before this migration ever got a chance to run).
		Guid importRevisionId = await InsertSourceRevisionAsync(connection, "compliance-content");
		Guid duplicateProductId = await InsertProductAsync(connection, importRevisionId, "VMware vSphere", "migration-0072-test", "Test Product");
		Guid duplicateVersionId = await InsertProductVersionAsync(connection, duplicateProductId, "8.0", "8.0");
		Guid duplicateComponentId = await InsertComponentAsync(connection, duplicateVersionId, "vcenter", "vCenter Server");
		Guid duplicateReleaseId = await InsertContentReleaseAsync(connection, "stig", "migration-0072-test:stig:release-2", "Test STIG Release 2");
		Guid duplicateGroupId = await InsertReportGroupAsync(connection, "migration-0072-vcenter-stig-2", "vCenter STIG 2", 3);
		Guid duplicateProfileId = await InsertExecutionProfileAsync(connection, duplicateComponentId, duplicateReleaseId, duplicateGroupId, "v2", "hdf_ckl");
		await InsertCredentialRequirementAsync(connection, duplicateProfileId, "vsphere-api");
		await InsertDeclaredInputAsync(connection, duplicateProfileId, "vcenter_host", "string");

		Guid targetId = await InsertTargetAsync(connection, "migration-0072-test-target");
		Guid discoveredComponentId = await InsertDiscoveredComponentAsync(connection, targetId, duplicateComponentId, "vcenter");

		// Sanity check the pre-state genuinely has two trees before merging.
		Assert.Equal(2, await CountAsync(connection, $"SELECT count(*) FROM catalog_products WHERE product_key = 'migration-0072-test'"));
		Assert.Equal(2, await CountAsync(connection, $"SELECT count(*) FROM catalog_components WHERE component_key = 'vcenter' AND product_version_id IN ('{canonicalVersionId}', '{duplicateVersionId}')"));

		string migration0072 = await ReadMigrationSqlAsync("0072_merge_duplicate_catalog_product_trees.sql");
		await using (NpgsqlCommand applyMigration0072 = new(migration0072, connection))
		{
			await applyMigration0072.ExecuteNonQueryAsync();
		}

		// Exactly one product/version/component per natural key now.
		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM catalog_products WHERE product_key = 'migration-0072-test'"));
		await using (NpgsqlCommand selectSurvivingProduct = new(
			"SELECT id, vendor FROM catalog_products WHERE product_key = 'migration-0072-test'", connection))
		await using (NpgsqlDataReader reader = await selectSurvivingProduct.ExecuteReaderAsync())
		{
			Assert.True(await reader.ReadAsync());
			Assert.Equal(canonicalProductId, reader.GetGuid(0));
			Assert.Equal("vmware", reader.GetString(1));
			Assert.False(await reader.ReadAsync());
		}

		Assert.Equal(1, await CountAsync(connection, $"SELECT count(*) FROM catalog_product_versions WHERE product_id = '{canonicalProductId}' AND version_key = '8.0'"));
		Assert.Equal(0, await CountAsync(connection, $"SELECT count(*) FROM catalog_product_versions WHERE id = '{duplicateVersionId}'"));

		Assert.Equal(1, await CountAsync(connection, $"SELECT count(*) FROM catalog_components WHERE product_version_id = '{canonicalVersionId}' AND component_key = 'vcenter'"));
		Assert.Equal(0, await CountAsync(connection, $"SELECT count(*) FROM catalog_components WHERE id = '{duplicateComponentId}'"));

		// The discovered instance component, previously linked to the now-deleted
		// duplicate catalog component, is re-pointed onto the surviving canonical one --
		// CatalogLinkageResolver now finds exactly one candidate for this component key
		// under this scope, not two.
		await using (NpgsqlCommand selectDiscovered = new(
			"SELECT catalog_component_id FROM components WHERE id = $1", connection))
		{
			selectDiscovered.Parameters.AddWithValue(discoveredComponentId);
			Assert.Equal(canonicalComponentId, (Guid)(await selectDiscovered.ExecuteScalarAsync())!);
		}

		// The duplicate's execution profile (a genuinely different content release --
		// release-2, not a re-promotion of the canonical's release-1) survives, re-pointed
		// onto the canonical component rather than dropped, with its own children intact.
		await using (NpgsqlCommand selectMergedProfile = new(
			"SELECT component_id FROM catalog_execution_profiles WHERE id = $1", connection))
		{
			selectMergedProfile.Parameters.AddWithValue(duplicateProfileId);
			Assert.Equal(canonicalComponentId, (Guid)(await selectMergedProfile.ExecuteScalarAsync())!);
		}

		Assert.Equal(1, await CountAsync(connection, $"SELECT count(*) FROM catalog_credential_requirements WHERE execution_profile_id = '{duplicateProfileId}'"));
		Assert.Equal(1, await CountAsync(connection, $"SELECT count(*) FROM catalog_declared_inputs WHERE execution_profile_id = '{duplicateProfileId}'"));

		// The canonical tree's own active baseline is untouched (no competing active
		// baseline existed on the duplicate's profile in this fixture, so there is
		// nothing to supersede).
		await using (NpgsqlCommand selectBaselineStatus = new(
			"SELECT status FROM baselines WHERE id = $1", connection))
		{
			selectBaselineStatus.Parameters.AddWithValue(canonicalBaselineId);
			Assert.Equal("active", (string)(await selectBaselineStatus.ExecuteScalarAsync())!);
		}

		// Replay-safety: running the exact same SQL text again against the now-merged
		// state finds zero remaining non-'vmware' duplicates for this product_key and is
		// a clean no-op (SchemaMigrationTests.Migrations_ApplyFreshThenReapplyAllViaRunnerAndRawSql_AreAllIdempotent
		// already proves this for the full embedded set; this asserts it directly for
		// 0072's own text against a state that has already been merged once).
		await using (NpgsqlCommand replayMigration0072 = new(migration0072, connection))
		{
			await replayMigration0072.ExecuteNonQueryAsync();
		}

		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM catalog_products WHERE product_key = 'migration-0072-test'"));
		Assert.Equal(1, await CountAsync(connection, $"SELECT count(*) FROM catalog_components WHERE product_version_id = '{canonicalVersionId}' AND component_key = 'vcenter'"));
	}

	private static async Task<Guid> InsertSourceRevisionAsync(NpgsqlConnection connection, string revisionKey)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_source_revisions (revision_key) VALUES ($1) ON CONFLICT (revision_key) DO UPDATE SET revision_key = EXCLUDED.revision_key RETURNING id", connection);
		command.Parameters.AddWithValue(revisionKey);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertProductAsync(NpgsqlConnection connection, Guid sourceRevisionId, string vendor, string productKey, string displayName)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_products (source_revision_id, vendor, product_key, display_name) VALUES ($1, $2, $3, $4) RETURNING id", connection);
		command.Parameters.AddWithValue(sourceRevisionId);
		command.Parameters.AddWithValue(vendor);
		command.Parameters.AddWithValue(productKey);
		command.Parameters.AddWithValue(displayName);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertProductVersionAsync(NpgsqlConnection connection, Guid productId, string versionKey, string displayName)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_product_versions (product_id, version_key, display_name) VALUES ($1, $2, $3) RETURNING id", connection);
		command.Parameters.AddWithValue(productId);
		command.Parameters.AddWithValue(versionKey);
		command.Parameters.AddWithValue(displayName);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertComponentAsync(NpgsqlConnection connection, Guid productVersionId, string componentKey, string displayName)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_components (product_version_id, component_key, display_name, transport, selector_kind) VALUES ($1, $2, $3, 'vmware', 'vcenter') RETURNING id", connection);
		command.Parameters.AddWithValue(productVersionId);
		command.Parameters.AddWithValue(componentKey);
		command.Parameters.AddWithValue(displayName);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertContentReleaseAsync(NpgsqlConnection connection, string kind, string releaseKey, string displayName)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_content_releases (source_revision_id, kind, release_key, display_name)
			VALUES ((SELECT id FROM catalog_source_revisions ORDER BY recorded_at LIMIT 1), $1, $2, $3)
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(kind);
		command.Parameters.AddWithValue(releaseKey);
		command.Parameters.AddWithValue(displayName);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertReportGroupAsync(NpgsqlConnection connection, string groupKey, string displayName, int priority)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_report_groups (group_key, display_name, priority) VALUES ($1, $2, $3) RETURNING id", connection);
		command.Parameters.AddWithValue(groupKey);
		command.Parameters.AddWithValue(displayName);
		command.Parameters.AddWithValue(priority);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertExecutionProfileAsync(NpgsqlConnection connection, Guid componentId, Guid contentReleaseId, Guid reportGroupId, string profileVersion, string outputKind)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
			VALUES ($1, $2, $3, $4, $5)
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(componentId);
		command.Parameters.AddWithValue(contentReleaseId);
		command.Parameters.AddWithValue(reportGroupId);
		command.Parameters.AddWithValue(profileVersion);
		command.Parameters.AddWithValue(outputKind);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task InsertCredentialRequirementAsync(NpgsqlConnection connection, Guid executionProfileId, string purpose)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_credential_requirements (execution_profile_id, purpose) VALUES ($1, $2)", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(purpose);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task InsertDeclaredInputAsync(NpgsqlConnection connection, Guid executionProfileId, string name, string inputType)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO catalog_declared_inputs (execution_profile_id, name, input_type, is_required) VALUES ($1, $2, $3, true)", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(inputType);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<Guid> InsertContentRevisionAsync(NpgsqlConnection connection, string sourceCommit, string contentDigest)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO content_revisions (source_commit, content_digest, staged_relative_path, status)
			VALUES ($1, $2, $3, 'activated')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(sourceCommit);
		command.Parameters.AddWithValue(contentDigest);
		command.Parameters.AddWithValue($"migration-0072-test/{contentDigest}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertActiveBaselineAsync(NpgsqlConnection connection, Guid contentRevisionId, Guid executionProfileId)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO baselines (content_revision_id, catalog_execution_profile_id, status, activated_at, activated_by)
			VALUES ($1, $2, 'active', now(), 'migration-0072-test')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(contentRevisionId);
		command.Parameters.AddWithValue(executionProfileId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertTargetAsync(NpgsqlConnection connection, string name)
	{
		await using NpgsqlCommand insertSite = new(
			"INSERT INTO sites (name) VALUES ($1) RETURNING id", connection);
		insertSite.Parameters.AddWithValue($"{name}-site");
		Guid siteId = (Guid)(await insertSite.ExecuteScalarAsync())!;

		await using NpgsqlCommand command = new(
			"INSERT INTO targets (site_id, name, kind) VALUES ($1, $2, 'vsphere') RETURNING id", connection);
		command.Parameters.AddWithValue(siteId);
		command.Parameters.AddWithValue(name);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<Guid> InsertDiscoveredComponentAsync(NpgsqlConnection connection, Guid targetId, Guid catalogComponentId, string catalogComponentKey)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO components (parent_target_id, catalog_component_id, catalog_component_key, display_name)
			VALUES ($1, $2, $3, 'vCenter Server')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(targetId);
		command.Parameters.AddWithValue(catalogComponentId);
		command.Parameters.AddWithValue(catalogComponentKey);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<bool> ColumnExistsAsync(NpgsqlConnection connection, string table, string column)
	{
		await using NpgsqlCommand command = new(
			"SELECT 1 FROM information_schema.columns WHERE table_name = $1 AND column_name = $2", connection);
		command.Parameters.AddWithValue(table);
		command.Parameters.AddWithValue(column);
		return await command.ExecuteScalarAsync() is not null;
	}

	/// <summary>The raw text of the 0050 migration embedded resource (authoritative CHECK source).</summary>
	private static async Task<string> ReadMigration0050SqlAsync() => await ReadMigrationSqlAsync("0050_compliance_catalog.sql");

	/// <summary>The raw text of one embedded migration resource, matched by its filename suffix.</summary>
	private static async Task<string> ReadMigrationSqlAsync(string fileName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith(fileName, StringComparison.Ordinal)));
		await using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync();
	}

	/// <summary>
	/// Extracts the single-quoted value list of a named <c>... CHECK (col IN ('a', 'b', ...))</c>
	/// constraint from migration SQL, returned ordinal-sorted for order-independent set equality.
	/// </summary>
	private static IEnumerable<string> ParseCheckInList(string sql, string constraintName)
	{
		Match constraint = Regex.Match(
			sql,
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\([^)]*\bIN\s*\(([^)]*)\)",
			RegexOptions.IgnoreCase);
		Assert.True(constraint.Success, $"Could not locate an IN-list CHECK named '{constraintName}' in the 0050 migration.");

		MatchCollection values = Regex.Matches(constraint.Groups[1].Value, "'([^']*)'");
		Assert.NotEmpty(values);
		return values.Select(m => m.Groups[1].Value).OrderBy(v => v, StringComparer.Ordinal);
	}

	/// <summary>
	/// Creates an empty database on the fixture's server and returns a connection string
	/// for it, so a test can exercise the fresh-apply path independently of the shared
	/// database every other test in the collection migrates.
	/// </summary>
	private async Task<string> CreateFreshDatabaseAsync()
	{
		string databaseName = $"waypoint_fresh_{Guid.NewGuid():N}";

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand command = new($"CREATE DATABASE {databaseName}", connection);
			await command.ExecuteNonQueryAsync();
		}

		return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = databaseName }.ToString();
	}

	private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
	{
		await using NpgsqlCommand command = new(
			"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = $1)",
			connection);
		command.Parameters.AddWithValue(tableName);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
	{
		await using NpgsqlCommand command = new(sql, connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// Every embedded <c>Data/Migrations/*.sql</c> resource's raw text, ordered the same
	/// way <c>NpgsqlSchemaMigrator</c> orders them (ordinal on the zero-padded filename
	/// prefix) -- so re-running them all, in order, outside the tracking table is a
	/// faithful "what would a from-scratch raw apply do" check, not just of migration 1.
	/// </summary>
	private static async Task<IReadOnlyList<string>> ReadEmbeddedMigrationSqlInOrderAsync()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Assert.Equal(ExpectedMigrationCount, resourceNames.Length);

		List<string> statements = new(resourceNames.Length);
		foreach (string resourceName in resourceNames)
		{
			await using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			statements.Add(await reader.ReadToEndAsync());
		}

		return statements;
	}

	/// <summary>
	/// An <see cref="ILogger{TCategoryName}"/> that is enabled at every level and keeps the
	/// formatted messages, so a test can assert on what the migrator actually logged.
	/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> reports
	/// <see cref="ILogger.IsEnabled"/> as <c>false</c>, which silently short-circuits the
	/// <c>[LoggerMessage]</c>-generated methods before they format anything.
	/// </summary>
	private sealed class CollectingLogger : ILogger<NpgsqlSchemaMigrator>
	{
		public List<string> Messages { get; } = [];

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			ArgumentNullException.ThrowIfNull(formatter);
			Messages.Add(formatter(state, exception));
		}
	}
}
