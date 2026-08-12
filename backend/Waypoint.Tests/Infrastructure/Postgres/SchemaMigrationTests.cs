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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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
		"schema_migrations"
	];

	/// <summary>Embedded migration count as of issue #444 (0008 adds downloads.run_id, 0009 adds sites/targets, 0010 adds credential owner CHECK + sudo_enabled, 0011 adds inventory_items + discover.progress, 0012 adds credentials.username (#262/#267), 0013 adds config_docs/config_versions, 0014 adds jobs.cancel_requested, 0015 widens the lease-required CHECK to attesting/converting, 0016 widens idx_jobs_lease_recovery to attesting/converting for #282's crashed-worker recovery, 0017 adds stigman_connections (the global STIG Manager connection singleton), 0018 adds jobs.upload_status/upload_detail (per-target STIG Manager upload outcome, independent of state/stage), 0019 adds 'credential-test' to jobs_job_type_check/runs_run_type_check, 0020 adds BEFORE UPDATE/DELETE triggers enforcing job_events/audit_log append-only, with a carve-out on audit_log for 0006's FK-driven credential_id SET NULL, 0021 adds attestation_snapshots -- the persisted at-scan-time attestations-applied ledger, issue #306, 0022 adds credentials_credential_type_check mirroring CredentialTypes.All, issue #252, 0023 adds run_secrets + jobs.has_run_secret -- the encrypted run-scoped personal-credential handoff, issue #434, 0024 adds idx_jobs_queue_claim -- the partial index backing the allowlist-filtered atomic claim so runners stop scanning every claimable row, issue #435, 0025 grants least-privilege table access to the waypoint_compliance_runner/waypoint_download_runner roles deploy/postgres/initdb/01-runner-roles.sh creates, issue #442, 0026 adds worker_registry -- the runner heartbeat/capability table GET /system reads to report per-domain availability, issue #443, 0027 grants worker_registry access to the two runner roles, issue #443, 0028 adds the SELECT grant 0027 wrongly withheld -- INSERT...ON CONFLICT DO UPDATE requires it even with no explicit SELECT in the application's own SQL text, issue #444) -- bump this alongside adding a new <c>Data/Migrations/*.sql</c> file.</summary>
	private const int ExpectedMigrationCount = 28;

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
	/// Issue #252: every value in the closed <c>CredentialTypes.All</c> set -- including
	/// <c>depot-token</c>, resolved into the set from a real production consumer
	/// (<c>CatalogIndexJobHandler</c>), not a placeholder -- must still insert cleanly
	/// under the new CHECK.
	/// </summary>
	[Theory]
	[InlineData("vcenter")]
	[InlineData("nsx")]
	[InlineData("ssh")]
	[InlineData("token")]
	[InlineData("depot-token")]
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
