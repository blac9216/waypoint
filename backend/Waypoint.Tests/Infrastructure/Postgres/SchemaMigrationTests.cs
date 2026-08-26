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
		"schema_migrations"
	];

	/// <summary>Embedded migration count as of issue #584 (... 0042 adds run_purges + run_purge_tombstones -- the durable retryable purge lifecycle and append-only audit tombstone for the admin-only terminal-compliance-run purge, plus runs.purged_at, 'purge' in jobs_job_type_check/runs_run_type_check, relaxes schedules.last_run_id from RESTRICT to ON DELETE SET NULL, and the compliance-runner's run_purges progress-reporting grant, issue #594, 0043 adds target_credential_bindings -- the normalized purpose-specific credential binding table (ADR-0021), backfills existing targets.credential_id references into the kind-appropriate default-purpose binding, and documents the dual-write contract keeping targets.credential_id and the default-purpose binding consistent until #585 removes the legacy column -- no new runner grants, issue #584, 0044 adds job_credential_bindings -- the immutable per-job per-purpose credential snapshot ledger (ADR-0021 SS5) RunCreationService's scan fan-out populates, with the compliance-runner SELECT-only grant and the jobs.credential_id dual-write/fallback contract documented in the migration header, issue #585, 0045 re-keys run_secrets from one row per run to one row per (run, target, purpose) -- additive columns/indexes only, the unconditional per-terminal-completion DELETE stays run_id-scoped so it covers both shapes with no code change, plus job_credential_bindings.is_run_secret so a job's per-purpose snapshot can name an ad hoc (run_secrets-backed) source instead of a stored credential_id, issue #586, 0046 adds runs.history_deleted_at + run_history_deletion_tombstones -- generic operational-history deletion for TERMINAL runs, structurally separate from run_purges/run_purge_tombstones (a deliberate sibling table, not a shared one) and deferring to that domain purge for scan/remediate runs, entirely API-side with no new runner grants, issue #592, 0047 extends credentials_credential_type_check with 'depot-activation-code' and 'legacy-download-token' (issue #690's non-destructive split of the ambiguous 'depot-token' well-known type -- 'depot-token' itself is RETAINED, not dropped, so pre-existing rows stay valid and visibly legacy; no data rewritten, no new runner grants), issue #691, 0048 adds depot_enrollment -- the singleton (mirrors appliance_state) non-secret Software Depot ID + assisted-enrollment state-machine table, adds 'depot-enrollment' to jobs_job_type_check/runs_run_type_check for the noninteractive tool-invocation job, and grants waypoint_download_runner SELECT/UPDATE on the new table, issue #687, 0049 adds catalog_pull_state -- the singleton (mirrors depot_enrollment) connected-catalog-pull attempt/success tracking table, adds 'catalog-pull' to jobs_job_type_check/runs_run_type_check for the distinct connected vendor-catalog-pull job (separate from the local credential-free catalog-index re-index), and grants waypoint_download_runner SELECT/INSERT/UPDATE on the new table, issue #687, 0050 adds the normalized compliance catalog (issue #728, ADR-0022): catalog_source_revisions, catalog_products, catalog_product_versions, catalog_content_releases, catalog_components, catalog_report_groups, catalog_execution_profiles, catalog_credential_requirements, catalog_benchmark_references, and catalog_remediation_definitions -- the versioned identity tree and closed capability vocabulary for STIG/SRG execution profiles, all FKs ON DELETE RESTRICT so a plan-referenced historical revision cannot be deleted, no new runner grants (every row is catalog-authored, appliance-shipped data), issue #730's PR 1 (0051 is reserved for a parallel #729 persistence PR that had not merged as of this migration's authoring -- whichever of the two PRs merges second must rebase and reconcile this ledger's exact count/table enumeration) adds 0052_xccdf_benchmark_revisions.sql: benchmark_revisions, benchmark_rules, and benchmark_component_mappings -- immutable digest-addressed DISA XCCDF/STIG benchmark revisions and rules plus the exact component-to-benchmark-revision mapping and its versioned audit history (one current row per component via a partial unique index, prior decisions superseded rather than overwritten), no new runner grants (Admin-only mapping writes are API-layer, deferred to issue #730's remainder PR) -- bump this alongside adding a new <c>Data/Migrations/*.sql</c> file.</summary>
	private const int ExpectedMigrationCount = 51;

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
			CredentialPurposes.All.OrderBy(v => v, StringComparer.Ordinal),
			ParseCheckInList(migration, "catalog_credential_requirements_purpose_check"));
	}

	/// <summary>The raw text of the 0050 migration embedded resource (authoritative CHECK source).</summary>
	private static async Task<string> ReadMigration0050SqlAsync()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith("0050_compliance_catalog.sql", StringComparison.Ordinal)));
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
