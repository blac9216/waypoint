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

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #556 regression test, closing the exact gap
/// <see cref="WorkerRegistryRunnerRoleGrantTests"/>'s doc comment already names as a
/// class of defect: every prior test covering <see cref="RunSecretStore"/> and
/// <see cref="TargetRepository"/> ran through <see cref="PostgresFixture.ConnectionString"/>
/// -- the full-privilege test owner role -- never the actual least-privilege
/// <c>waypoint_compliance_runner</c> role migrations 0025/0033 grant to. That let two
/// real operations ship broken: <see cref="RunSecretStore.DecryptAsync"/>'s
/// sliding-expiry <c>UPDATE run_secrets SET expires_at = ...</c> (issue #469) and
/// <see cref="TargetRepository.SetDiscoveryStatusAsync"/>'s
/// <c>UPDATE targets SET discovery_status = ..., last_refreshed = ...</c> both hit
/// live <c>42501: permission denied</c> against a fully-migrated fresh Compose stack
/// even though every domain-level unit test for both methods passed. This file
/// connects as the actual runner role for each required operation (migration 0033's
/// fix) and for two representative operations that must STILL be denied (the
/// least-privilege boundary migration 0033 deliberately did not widen), so grant
/// drift in either direction breaks a test again instead of shipping silently.
/// </summary>
[Collection("Postgres")]
public sealed class RunnerRoleGrantDriftTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-runner-role-grant-drift-test").FullName;
	private string _complianceRunnerConnectionString = string.Empty;
	private string _downloadRunnerConnectionString = string.Empty;
	private InPlaySecretRedactor _redactor = null!;

	public RunnerRoleGrantDriftTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		// Same fixed test-role password convention as WorkerRegistryRunnerRoleGrantTests:
		// PostgresFixture.CreateRunnerRolesAsync provisions both roles with "waypoint_test"
		// against the same host/port/db as the owner connection string.
		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_compliance_runner",
			Password = "waypoint_test",
		};
		_complianceRunnerConnectionString = builder.ConnectionString;

		NpgsqlConnectionStringBuilder downloadBuilder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = downloadBuilder.ConnectionString;

		_redactor = new InPlaySecretRedactor();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	/// <summary>
	/// The issue's example 1, reproduced and fixed: a second (and therefore
	/// sliding-window) decrypt as the real compliance-runner role must succeed, and
	/// must actually have pushed <c>expires_at</c> out -- not just avoid throwing.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_DecryptAsync_SlidesExpiryWithoutPermissionDenied()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);

		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(WriteKeyFile()));
		// Store through the owner connection: INSERT on run_secrets stays API-only
		// (RunsController), never granted to the runner role -- proven denied below.
		RunSecretStore ownerStore = new(_fixture.ConnectionString, cipher, _redactor, Microsoft.Extensions.Options.Options.Create(new RunSecretOptions()), NullLogger<RunSecretStore>.Instance);
		await ownerStore.StoreAsync(runId, new RunSecretCredential("adhoc-user@example.internal", "invented-adhoc-token-9f2c"), "tester", TimeSpan.FromMinutes(5), CancellationToken.None);

		DateTimeOffset expiresBefore = await ReadExpiresAtAsync(runId);

		RunSecretStore runnerStore = new(_complianceRunnerConnectionString, cipher, _redactor, Microsoft.Extensions.Options.Options.Create(new RunSecretOptions { Expiry = TimeSpan.FromHours(2) }), NullLogger<RunSecretStore>.Instance);
		using (DecryptedRunSecret? handle = await runnerStore.DecryptAsync(runId, jobId, "engine", CancellationToken.None))
		{
			Assert.NotNull(handle);
		}

		DateTimeOffset expiresAfter = await ReadExpiresAtAsync(runId);
		Assert.True(expiresAfter > expiresBefore, "expected the compliance-runner role's decrypt to slide expires_at forward, not merely avoid throwing.");
	}

	/// <summary>
	/// Least-privilege boundary check for run_secrets: the runner role gets
	/// <c>UPDATE (expires_at)</c> only. INSERT stays API-only (RunsController
	/// registers the secret at run-creation time) -- this must still fail 42501.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotInsertRunSecrets()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
			VALUES ($1, 'x', '\x00'::bytea, '\x00'::bytea, 'k', 'AES-256-GCM', now() + interval '1 hour')
			""", connection);
		insert.Parameters.AddWithValue(Guid.NewGuid());

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// The issue's example 2, reproduced and fixed: <see cref="TargetRepository.SetDiscoveryStatusAsync"/>
	/// as the real compliance-runner role must succeed for both the non-stamping
	/// ("discovering") and stamping (terminal) transitions <see cref="Core.Discovery"/>
	/// job handling actually performs.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_SetDiscoveryStatusAsync_SucceedsForBothTransitionShapes()
	{
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId);

		TargetRepository runnerTargets = new(_complianceRunnerConnectionString);

		bool discoveringOk = await runnerTargets.SetDiscoveryStatusAsync(targetId, "discovering", stampLastRefreshed: false, CancellationToken.None);
		Assert.True(discoveringOk);

		bool discoveredOk = await runnerTargets.SetDiscoveryStatusAsync(targetId, "discovered", stampLastRefreshed: true, CancellationToken.None);
		Assert.True(discoveredOk);

		(string status, DateTimeOffset? lastRefreshed) = await ReadDiscoveryStateAsync(targetId);
		Assert.Equal("discovered", status);
		Assert.NotNull(lastRefreshed);
	}

	/// <summary>
	/// Least-privilege boundary check for targets: the runner role's UPDATE grant is
	/// column-scoped to <c>discovery_status, last_refreshed</c> only -- every other
	/// targets column (name, connection, credential_id, kind) stays API-only
	/// (TargetsController), exactly as 0025's original comment intended. Representative
	/// prohibited operation: <see cref="TargetRepository.UpdateAsync"/>'s full-replacement
	/// update touches <c>name</c>, so it must still fail 42501 even though the runner
	/// role now has SOME UPDATE privilege on this table.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotUpdateTargetName()
	{
		Guid siteId = await SeedSiteAsync();
		Guid targetId = await SeedTargetAsync(siteId);

		TargetRepository runnerTargets = new(_complianceRunnerConnectionString);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
			() => runnerTargets.UpdateAsync(targetId, null, "renamed-by-runner", null, null, clearCredential: false, CancellationToken.None));
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// The audit finding beyond the issue's two named examples: migration 0025 never
	/// granted the compliance-runner role anything on <c>config_docs</c>/
	/// <c>config_versions</c> at all, even though <c>ScanJobHandler</c>'s attest stage
	/// resolves attestation config docs via <see cref="ConfigDocRepository.FindWithLatestVersionAsync"/>
	/// before applying them to a scan's HDF report (issue #266). Migration 0033 adds
	/// SELECT-only (authoring stays API-only, POST /config-docs).
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CanReadConfigDocsForAttestationResolution()
	{
		ConfigDocRepository ownerConfigDocs = new(_fixture.ConnectionString);
		Guid targetId = Guid.NewGuid();
		(ConfigDocSaveOutcome outcome, _, _) = await ownerConfigDocs.SaveAsync(
			targetId, ConfigDocKinds.Attestation, "role-grant-drift-profile", ConfigDocLayers.Global, null,
			"tester", "control-1: applied", CancellationToken.None);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome);

		ConfigDocRepository runnerConfigDocs = new(_complianceRunnerConnectionString);
		ConfigDocWithLatestVersion? resolved = await runnerConfigDocs.FindWithLatestVersionAsync(
			ConfigDocKinds.Attestation, "role-grant-drift-profile", ConfigDocLayers.Global, null, CancellationToken.None);

		Assert.NotNull(resolved);
		Assert.Equal("control-1: applied", resolved!.LatestVersion.BodyYaml);
	}

	/// <summary>
	/// Least-privilege boundary check for config_docs: authoring stays API-only. The
	/// runner role must still be denied a write, even though it can now read the table.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotWriteConfigDocs()
	{
		ConfigDocRepository runnerConfigDocs = new(_complianceRunnerConnectionString);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
			() => runnerConfigDocs.SaveAsync(
				Guid.NewGuid(), ConfigDocKinds.Attestation, "runner-write-attempt", ConfigDocLayers.Global, null,
				"runner", "control-1: applied", CancellationToken.None));
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Issue #560 (migration 0034), same class of drift as this file's other cases:
	/// migration 0025's column-scoped grants on <c>credentials</c> predate the two
	/// columns 0034 adds. <see cref="CredentialTestJobHandler"/> runs as the real
	/// compliance-runner role and, per test job, calls
	/// <see cref="CredentialRepository.GetAsync"/> (whose ProjectionSql now selects
	/// <c>last_tested_at, expires_at</c>) and <see cref="CredentialRepository.MarkTestOutcomeAsync"/>
	/// (<c>UPDATE credentials SET health = ..., last_tested_at = now()</c>). Both must
	/// succeed as the runner role, not just as the fixture owner.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CredentialTestOutcome_ReadsAndStampsWithoutPermissionDenied()
	{
		Guid credentialId = await SeedCredentialAsync();

		CredentialRepository runnerCredentials = new(_complianceRunnerConnectionString);

		// SELECT path: ProjectionSql now includes last_tested_at, expires_at -- would
		// hit 42501 without the 0034 SELECT grant on those two columns.
		CredentialResponse? beforeTest = await runnerCredentials.GetAsync(credentialId, CancellationToken.None);
		Assert.NotNull(beforeTest);
		Assert.Null(beforeTest!.LastTestedAt);

		// UPDATE path: MarkTestOutcomeAsync stamps last_tested_at (+ health) -- would
		// hit 42501 without the 0034 UPDATE grant on last_tested_at.
		CredentialWriteOutcome outcome = await runnerCredentials.MarkTestOutcomeAsync(credentialId, succeeded: true, CancellationToken.None);
		Assert.Equal(CredentialWriteOutcome.Ok, outcome);

		CredentialResponse? afterTest = await runnerCredentials.GetAsync(credentialId, CancellationToken.None);
		Assert.NotNull(afterTest);
		Assert.NotNull(afterTest!.LastTestedAt);
	}

	/// <summary>
	/// Least-privilege boundary check for the 0034 credentials columns: the runner
	/// role gets <c>UPDATE (last_tested_at)</c> only. <c>expires_at</c> is SELECT-only
	/// for runners (never fabricated; any upstream-supplied expiry is an API-side
	/// write), so a runner-role UPDATE of <c>expires_at</c> must still fail 42501 even
	/// though the role now has SOME UPDATE privilege on this table.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotUpdateCredentialExpiresAt()
	{
		Guid credentialId = await SeedCredentialAsync();

		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE credentials SET expires_at = now() + interval '30 days' WHERE id = $1", connection);
		update.Parameters.AddWithValue(credentialId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Second least-privilege boundary check for credentials: the runner's UPDATE grant
	/// stays column-scoped. Representative prohibited column -- <c>name</c> is API-only
	/// (CredentialsController) and must still fail 42501.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotUpdateCredentialName()
	{
		Guid credentialId = await SeedCredentialAsync();

		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE credentials SET name = 'renamed-by-runner' WHERE id = $1", connection);
		update.Parameters.AddWithValue(credentialId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Issue #622: the third instance of the #556 grant-drift class, this time against
	/// waypoint_download_runner. <see cref="CredentialRepository.FindByTypeAsync"/> --
	/// called by both <c>CatalogIndexJobHandler</c> (catalog-index) and
	/// <c>ManagedToolInstallJobHandler</c>'s depot-fetch path (tool-install) to resolve
	/// the stored depot-token credential -- shares <c>ProjectionSql</c> with
	/// <see cref="CredentialRepository.GetAsync"/>, which migration 0034 extended to
	/// select <c>last_tested_at, expires_at</c>. Migration 0035 granted those two
	/// columns to waypoint_compliance_runner only; the download-runner role was never
	/// updated, so this call hit 42501 the moment a real download-runner claimed a
	/// catalog-index or depot-fetch tool-install job. Migration 0039 closes it.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_FindByTypeAsync_ResolvesDepotTokenWithoutPermissionDenied()
	{
		await SeedCredentialOfTypeAsync("depot-token");

		CredentialRepository runnerCredentials = new(_downloadRunnerConnectionString);
		CredentialResponse? resolved = await runnerCredentials.FindByTypeAsync("depot-token", CancellationToken.None);

		Assert.NotNull(resolved);
		Assert.Null(resolved!.LastTestedAt);
		Assert.Null(resolved.ExpiresAt);
	}

	/// <summary>
	/// Least-privilege boundary check for the download-runner's 0039 grant: it is
	/// SELECT-only on <c>last_tested_at</c>/<c>expires_at</c> -- neither
	/// <c>CatalogIndexJobHandler</c> nor <c>ManagedToolInstallJobHandler</c> ever calls
	/// <see cref="CredentialRepository.MarkTestOutcomeAsync"/> (that stays a
	/// compliance-runner-only write, migration 0035), so an UPDATE attempt as the
	/// download-runner role must still fail 42501.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotUpdateCredentialLastTestedAt()
	{
		Guid credentialId = await SeedCredentialOfTypeAsync("depot-token");

		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE credentials SET last_tested_at = now() WHERE id = $1", connection);
		update.Parameters.AddWithValue(credentialId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Second least-privilege boundary check for the download-runner role: 0039 adds
	/// SELECT only, never UPDATE, on <c>expires_at</c> (never fabricated by a runner --
	/// an upstream-supplied expiry is an API-side write, same rule 0035 already applies
	/// to the compliance-runner role). An UPDATE attempt as the download-runner role
	/// must still fail 42501, proving 0039 did not accidentally grant a write.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotUpdateCredentialExpiresAt()
	{
		Guid credentialId = await SeedCredentialOfTypeAsync("depot-token");

		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE credentials SET expires_at = now() + interval '30 days' WHERE id = $1", connection);
		update.Parameters.AddWithValue(credentialId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Issue #569 (migration 0036), added at authoring time rather than after a live
	/// 42501 -- the lesson of this file's own header (and of 0033/0034 before it) is
	/// that a new runner-executed table without a role-contract test ships grant drift
	/// silently. Exercises the full capacity-pool protocol as the real
	/// compliance-runner role: derived-capacity registration (INSERT ... ON CONFLICT
	/// needs SELECT+INSERT+UPDATE on capacity_pool), claim, reservation, renewal,
	/// release, and reap on capacity_leases.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CapacityPoolProtocol_FullLifecycleWithoutPermissionDenied()
	{
		await ResetCapacityTablesAsync();
		Guid jobId = await SeedJobAsync(await SeedRunAsync());
		Guid starvedJobId = await SeedJobAsync(await SeedRunAsync());

		Waypoint.Infrastructure.Capacity.CapacityLeasePoolRepository runnerPool = new(_complianceRunnerConnectionString);

		await runnerPool.RegisterPoolCapacityAsync("compliance-runner-test", 4.0, 4096, operatorSet: false, CancellationToken.None);
		Assert.True(await runnerPool.TryClaimAsync(jobId, "compliance-runner-test", "scan", 2.0, 1024, TimeSpan.FromMinutes(5), CancellationToken.None));
		Assert.True(await runnerPool.TryReserveAsync(starvedJobId, "compliance-runner-test", "scan", 3.0, 3000, TimeSpan.FromMinutes(5), CancellationToken.None));
		Assert.True(await runnerPool.RenewAsync(jobId, "compliance-runner-test", TimeSpan.FromMinutes(5), CancellationToken.None));
		await runnerPool.ReleaseAsync(jobId, CancellationToken.None);
		await runnerPool.ReleaseAsync(starvedJobId, CancellationToken.None);
		Assert.Equal(0, await runnerPool.ReapExpiredAsync(CancellationToken.None));
	}

	/// <summary>Same protocol as the real download-runner role -- both execution domains share the one pool (migration 0036 grants both roles).</summary>
	[Fact]
	public async Task DownloadRunnerRole_CapacityPoolProtocol_ClaimAndReleaseWithoutPermissionDenied()
	{
		await ResetCapacityTablesAsync();
		Guid jobId = await SeedJobAsync(await SeedRunAsync());

		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		Waypoint.Infrastructure.Capacity.CapacityLeasePoolRepository runnerPool = new(builder.ConnectionString);

		await runnerPool.RegisterPoolCapacityAsync("download-runner-test", 2.0, 2048, operatorSet: false, CancellationToken.None);
		Assert.True(await runnerPool.TryClaimAsync(jobId, "download-runner-test", "download", 0.5, 512, TimeSpan.FromMinutes(5), CancellationToken.None));
		await runnerPool.ReleaseAsync(jobId, CancellationToken.None);
	}

	/// <summary>
	/// Issue #642 (root cause of #640): the fourth instance of the #556 grant-drift
	/// class. <see cref="JobQueueRepository.AdvanceStateAsync"/>'s terminal-state
	/// write runs <c>TryCompleteRunAsync</c> -&gt; <c>DeleteRunSecretIfPresentAsync</c>
	/// (<c>DELETE FROM run_secrets WHERE run_id = $1</c>) inside the SAME transaction
	/// for EVERY job of EITHER runner domain that lands a run's last job on a
	/// terminal state -- not just jobs that registered a run secret; a no-op delete
	/// still executes the statement and still needs the grant. Before migration 0040
	/// this throws 42501 as the real download-runner role, which rolls back the
	/// entire transaction (including the jobs UPDATE) -- reproducing #640's exact
	/// symptom: a tool-install job whose handler returns Failed never reaches
	/// 'failed' at all. This drives a job to 'failed' as the real
	/// waypoint_download_runner role and asserts it actually lands.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_AdvanceStateAsync_CompletesRunTerminalWithoutPermissionDenied()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);
		await ClaimSeededJobAsync(jobId, "download-runner-test-worker");

		JobQueueRepository runnerRepository = new(_downloadRunnerConnectionString, NullLogger<JobQueueRepository>.Instance);

		bool advanced = await runnerRepository.AdvanceStateAsync(
			jobId, "download-runner-test-worker", JobStates.Running, JobStates.Failed,
			note: "verification rejected", clearLease: true, CancellationToken.None);

		Assert.True(advanced, "expected the download-runner role's terminal AdvanceStateAsync to succeed, not hit 42501 on run_secrets.");

		(string jobState, string runState) = await ReadJobAndRunStateAsync(jobId, runId);
		Assert.Equal(JobStates.Failed, jobState);
		Assert.Equal("completed_with_failures", runState);
	}

	/// <summary>
	/// Same root cause as <see cref="DownloadRunnerRole_AdvanceStateAsync_CompletesRunTerminalWithoutPermissionDenied"/>,
	/// exercised via the OTHER shared completion path: lease recovery
	/// (<see cref="JobQueueRepository.RecoverExpiredLeasesAsync"/>) also calls
	/// <c>TryCompleteRunAsync</c> in-transaction whenever an exhausted-attempt job
	/// gets recovered straight to 'failed' -- the exact mechanism #642's issue body
	/// documents as the "duplicate ledger row per lease-recovery cycle" loop for
	/// #640. Seeds a job at its last attempt with an already-expired lease and drives
	/// the sweep as the real download-runner role.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_RecoverExpiredLeasesAsync_CompletesRunTerminalWithoutPermissionDenied()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);
		await ClaimSeededJobAsync(jobId, "download-runner-test-worker");
		await ExpireLeaseAndExhaustAttemptsAsync(jobId);

		JobQueueRepository runnerRepository = new(_downloadRunnerConnectionString, NullLogger<JobQueueRepository>.Instance);

		IReadOnlyList<Waypoint.Core.Jobs.RecoveredJob> recovered = await runnerRepository.RecoverExpiredLeasesAsync(10, CancellationToken.None);

		Assert.Contains(recovered, job => job.Id == jobId && job.NewState == JobStates.Failed);

		(string jobState, string runState) = await ReadJobAndRunStateAsync(jobId, runId);
		Assert.Equal(JobStates.Failed, jobState);
		Assert.Equal("completed_with_failures", runState);
	}

	/// <summary>
	/// Least-privilege boundary check mirroring
	/// <see cref="ComplianceRunnerRole_CannotInsertRunSecrets"/>: migration 0040 gives
	/// waypoint_download_runner the identical SELECT, DELETE, UPDATE (expires_at)
	/// shape as the compliance-runner role -- never INSERT, which stays API-only
	/// (RunsController registers the secret at run-creation time).
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotInsertRunSecrets()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO run_secrets (run_id, username, ciphertext, data_key_wrapped, master_key_id, algorithm, expires_at)
			VALUES ($1, 'x', '\x00'::bytea, '\x00'::bytea, 'k', 'AES-256-GCM', now() + interval '1 hour')
			""", connection);
		insert.Parameters.AddWithValue(Guid.NewGuid());

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// The issue's example, reproduced and fixed for the download-runner role: a
	/// decrypt as the real waypoint_download_runner role must succeed and must slide
	/// expires_at forward, mirroring <see cref="ComplianceRunnerRole_DecryptAsync_SlidesExpiryWithoutPermissionDenied"/>.
	/// ADR-0011's personal-credential tier was never actually restricted to
	/// compliance jobs at the persistence layer -- only under-exercised by download
	/// jobs until #558's live validation surfaced this gap.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_DecryptAsync_SlidesExpiryWithoutPermissionDenied()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);

		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(WriteKeyFile()));
		RunSecretStore ownerStore = new(_fixture.ConnectionString, cipher, _redactor, Microsoft.Extensions.Options.Options.Create(new RunSecretOptions()), NullLogger<RunSecretStore>.Instance);
		await ownerStore.StoreAsync(runId, new RunSecretCredential("adhoc-user@example.internal", "invented-adhoc-token-6a1d"), "tester", TimeSpan.FromMinutes(5), CancellationToken.None);

		DateTimeOffset expiresBefore = await ReadExpiresAtAsync(runId);

		RunSecretStore runnerStore = new(_downloadRunnerConnectionString, cipher, _redactor, Microsoft.Extensions.Options.Options.Create(new RunSecretOptions { Expiry = TimeSpan.FromHours(2) }), NullLogger<RunSecretStore>.Instance);
		using (DecryptedRunSecret? handle = await runnerStore.DecryptAsync(runId, jobId, "engine", CancellationToken.None))
		{
			Assert.NotNull(handle);
		}

		DateTimeOffset expiresAfter = await ReadExpiresAtAsync(runId);
		Assert.True(expiresAfter > expiresBefore, "expected the download-runner role's decrypt to slide expires_at forward, not merely avoid throwing.");
	}

	/// <summary>
	/// Least-privilege boundary for capacity_pool: DELETE is deliberately withheld
	/// from both runner roles (migration 0036's header) -- destroying the pool row
	/// denies all admission appliance-wide and stays an owner/migration action.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotDeleteCapacityPoolRow()
	{
		await ResetCapacityTablesAsync();
		Waypoint.Infrastructure.Capacity.CapacityLeasePoolRepository ownerPool = new(_fixture.ConnectionString);
		await ownerPool.RegisterPoolCapacityAsync("owner-test", 4.0, 4096, operatorSet: false, CancellationToken.None);

		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM capacity_pool WHERE id = 1", connection);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Issue #598 (migration 0038): <c>ContentPullJobHandler</c> persists each
	/// profile's parsed control inventory via <c>ProfileControlRepository.ReplaceForProfileAsync</c>
	/// as the compliance-runner role -- same replace-per-parent shape 0035's
	/// <c>profiles</c> grant already covers, extended to the new table.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CanReplaceProfileControls()
	{
		Waypoint.Infrastructure.ComplianceContent.ProfileRepository ownerProfiles = new(_fixture.ConnectionString);
		await ownerProfiles.ReplaceAllAsync(
			[new ProfileUpsert("role-grant-drift-profile", "Role Grant Drift Profile", "1.0", "commit1", ProfileStates.Current)],
			CancellationToken.None);
		Guid profileId = Assert.Single(await ownerProfiles.ListAsync(CancellationToken.None), p => p.ProfileKey == "role-grant-drift-profile").Id;

		Waypoint.Infrastructure.ComplianceContent.ProfileControlRepository runnerProfileControls = new(_complianceRunnerConnectionString);
		await runnerProfileControls.ReplaceForProfileAsync(
			profileId, [new ProfileControlUpsert("V-9001", "Runner-written control", "medium")], CancellationToken.None);

		Waypoint.Infrastructure.ComplianceContent.ProfileControlRepository ownerProfileControls = new(_fixture.ConnectionString);
		ProfileControl control = Assert.Single(await ownerProfileControls.ListByProfileAsync(profileId, CancellationToken.None));
		Assert.Equal("V-9001", control.ControlId);
	}

	/// <summary>
	/// Issue #593 (migration 0041): proves the new runs/jobs credential attribution
	/// snapshot columns did NOT silently need a new grant -- migration 0025 already
	/// grants both runner roles whole-table (not column-scoped) SELECT on runs and
	/// SELECT/UPDATE on jobs, so JobQueueRepository.GetRunAsync/GetJobAsync (which
	/// now select credential_name/credential_type/credential_username) must keep
	/// working unchanged as either runner role. Written at authoring time, same
	/// discipline as <see cref="ComplianceRunnerRole_CapacityPoolProtocol_FullLifecycleWithoutPermissionDenied"/>'s
	/// doc comment -- this migration's own header claims no grant change was needed;
	/// this is what proves that claim rather than asserting it in a comment only.
	/// </summary>
	[Theory]
	[InlineData("waypoint_compliance_runner")]
	[InlineData("waypoint_download_runner")]
	public async Task RunnerRole_ReadsCredentialAttributionSnapshotColumns_WithoutPermissionDenied(string role)
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);

		await using (NpgsqlConnection owner = new(_fixture.ConnectionString))
		{
			await owner.OpenAsync();
			await using NpgsqlCommand snapshotRun = new(
				"UPDATE runs SET credential_name = 'role-grant-drift-cred', credential_type = 'token', credential_username = 'svc@example.internal' WHERE id = $1", owner);
			snapshotRun.Parameters.AddWithValue(runId);
			await snapshotRun.ExecuteNonQueryAsync();

			await using NpgsqlCommand snapshotJob = new(
				"UPDATE jobs SET credential_name = 'role-grant-drift-cred', credential_type = 'token', credential_username = 'svc@example.internal' WHERE id = $1", owner);
			snapshotJob.Parameters.AddWithValue(jobId);
			await snapshotJob.ExecuteNonQueryAsync();
		}

		string connectionString = role == "waypoint_compliance_runner" ? _complianceRunnerConnectionString : _downloadRunnerConnectionString;
		JobQueueRepository runnerRepository = new(connectionString, NullLogger<JobQueueRepository>.Instance);

		RunSummary? run = await runnerRepository.GetRunAsync(runId, CancellationToken.None);
		Assert.NotNull(run);
		Assert.Equal("role-grant-drift-cred", run!.CredentialName);

		JobSummary? job = await runnerRepository.GetJobAsync(jobId, CancellationToken.None);
		Assert.NotNull(job);
		Assert.Equal("svc@example.internal", job!.CredentialUsername);
	}

	/// <summary>
	/// Issue #594 (migration 0042): <c>PurgeJobHandler</c> reports its own outcome back
	/// into <c>run_purges</c> (<c>artifacts_phase</c>, <c>artifacts_total</c>,
	/// <c>artifacts_deleted</c>, <c>last_error</c>, <c>completed_at</c>) as the real
	/// compliance-runner role -- proves the narrow column-scoped UPDATE grant migration
	/// 0042 adds actually covers <see cref="RunPurgeRepository.ReportArtifactOutcomeAsync"/>
	/// and the reverse <see cref="RunPurgeRepository.FindRunIdByArtifactJobIdAsync"/>
	/// lookup the handler uses to resolve which run its own job id belongs to, written
	/// at authoring time per this file's own "a new runner-executed table without a
	/// role-contract test ships grant drift silently" lesson (see 0036's companion
	/// test) rather than after a live 42501.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_ReportsPurgeArtifactOutcome_WithoutPermissionDenied()
	{
		Guid runId = await SeedRunAsync();
		Guid purgeJobId = await SeedJobAsync(runId);

		RunPurgeRepository ownerPurges = new(_fixture.ConnectionString);
		await ownerPurges.CreateAsync(runId, "admin-tester", "completed", CancellationToken.None);
		await ownerPurges.MarkArtifactJobEnqueuedAsync(runId, purgeJobId, artifactsTotal: 3, CancellationToken.None);

		RunPurgeRepository runnerPurges = new(_complianceRunnerConnectionString);

		Guid? resolvedRunId = await runnerPurges.FindRunIdByArtifactJobIdAsync(purgeJobId, CancellationToken.None);
		Assert.Equal(runId, resolvedRunId);

		await runnerPurges.ReportArtifactOutcomeAsync(runId, succeeded: true, artifactsDeleted: 3, lastError: null, CancellationToken.None);

		Waypoint.Core.Runs.RunPurgeStatus? status = await ownerPurges.GetStatusAsync(runId, CancellationToken.None);
		Assert.NotNull(status);
		Assert.Equal("done", status!.ArtifactsPhase);
		Assert.Equal(3, status.ArtifactsDeleted);
	}

	/// <summary>
	/// Least-privilege boundary check for <c>run_purges</c>: the compliance-runner role
	/// gets SELECT plus the five narrow reporting columns' UPDATE, never INSERT/DELETE
	/// (those stay API-only -- <see cref="Waypoint.Infrastructure.Runs.RunPurgeService"/>
	/// creates and completes/removes the row) and never <c>requested_by</c>/
	/// <c>prior_state</c>/<c>db_phase_done</c> (API-owned columns) via UPDATE.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotInsertOrDeleteRunPurges()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO run_purges (run_id, requested_by, prior_state) VALUES ($1, 'x', 'completed')", connection);
		insert.Parameters.AddWithValue(Guid.NewGuid());

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Second least-privilege boundary check for <c>run_purges</c>: <c>db_phase_done</c>
	/// is API-owned (RunPurgeService's own database-phase completion), never written by
	/// the runner-executed artifact job -- an UPDATE naming it must still fail 42501
	/// even though the role now has SOME UPDATE privilege on this table.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CannotUpdateDbPhaseDone()
	{
		Guid runId = await SeedRunAsync();
		RunPurgeRepository ownerPurges = new(_fixture.ConnectionString);
		await ownerPurges.CreateAsync(runId, "admin-tester", "completed", CancellationToken.None);

		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new("UPDATE run_purges SET db_phase_done = true WHERE run_id = $1", connection);
		update.Parameters.AddWithValue(runId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	/// <summary>
	/// Issue #594: proves 0042's carve-out trigger on <c>attestation_snapshots</c>
	/// works through <see cref="AttestationSnapshotRepository.DeleteForRunAsync"/> as
	/// the OWNER connection (RunPurgeService always runs the database phase through
	/// its own API-side connection, never as a runner role -- see that service's doc
	/// comment) -- included in this file because it is the other structural
	/// DB-permission change 0042 makes (a trigger carve-out, not a GRANT) and belongs
	/// alongside this file's other "prove the migration's access-control claim against
	/// real Postgres" cases rather than only in RunPurgeServiceTests' higher-level flow.
	/// </summary>
	[Fact]
	public async Task OwnerConnection_CanDeleteAttestationSnapshotsForARun_ViaPurgeCarveOut()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);

		await using (NpgsqlConnection owner = new(_fixture.ConnectionString))
		{
			await owner.OpenAsync();
			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO attestation_snapshots (run_id, job_id, target_id, profile, scope, applied, expired)
				VALUES ($1, $2, $3, 'vsphere-stig', 'target', true, false)
				""", owner);
			insert.Parameters.AddWithValue(runId);
			insert.Parameters.AddWithValue(jobId);
			insert.Parameters.AddWithValue(Guid.NewGuid());
			await insert.ExecuteNonQueryAsync();
		}

		AttestationSnapshotRepository attestationSnapshots = new(_fixture.ConnectionString);
		int deleted = await attestationSnapshots.DeleteForRunAsync(runId, CancellationToken.None);

		Assert.Equal(1, deleted);
	}

	/// <summary>
	/// The carve-out is scoped to a matching <c>waypoint.purge_run_id</c> GUC only --
	/// a bare DELETE with no GUC set (any caller other than
	/// <see cref="AttestationSnapshotRepository.DeleteForRunAsync"/>) must still hit the
	/// original 0021 append-only trigger, proving 0042 did not accidentally relax the
	/// guarantee generally.
	/// </summary>
	[Fact]
	public async Task OwnerConnection_CannotDeleteAttestationSnapshots_WithoutThePurgeGuc()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedJobAsync(runId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO attestation_snapshots (run_id, job_id, target_id, profile, scope, applied, expired)
			VALUES ($1, $2, $3, 'vsphere-stig', 'target', true, false)
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(jobId);
		insert.Parameters.AddWithValue(Guid.NewGuid());
		await insert.ExecuteNonQueryAsync();

		await using NpgsqlCommand delete = new("DELETE FROM attestation_snapshots WHERE run_id = $1", connection);
		delete.Parameters.AddWithValue(runId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
	}

	private async Task ResetCapacityTablesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE TABLE capacity_leases; DELETE FROM capacity_pool", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
		command.Parameters.AddWithValue($"role-grant-drift-cred-{Guid.NewGuid():N}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedCredentialOfTypeAsync(string credentialType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, $2) RETURNING id", connection);
		command.Parameters.AddWithValue($"role-grant-drift-cred-{Guid.NewGuid():N}");
		command.Parameters.AddWithValue(credentialType);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private string WriteKeyFile()
	{
		string path = Path.Combine(_keyDirectory, $"key-{Guid.NewGuid():N}");
		File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
		return path;
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}', 'running') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, has_run_secret) VALUES ($1, 'scan', 1, 'queued', true) RETURNING id", connection);
		command.Parameters.AddWithValue(runId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedSiteAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO sites (name) VALUES ($1) RETURNING id", connection);
		command.Parameters.AddWithValue($"role-grant-drift-site-{Guid.NewGuid():N}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedTargetAsync(Guid siteId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, discovery_status)
			VALUES ($1, 'vsphere', $2, '{"host":"vcsa-01.example.internal"}'::jsonb, 'never_discovered')
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(siteId);
		command.Parameters.AddWithValue($"role-grant-drift-target-{Guid.NewGuid():N}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task ClaimSeededJobAsync(Guid jobId, string workerId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE jobs SET state = 'running', claimed_by = $2, claimed_at = now(),
				lease_expires_at = now() + interval '5 minutes', heartbeat_at = now(),
				attempt_count = attempt_count + 1
			WHERE id = $1
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(workerId);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>Sets the job's lease in the past and attempt_count = max_attempts so RecoverSql's CASE picks the 'failed' arm (attempts exhausted) rather than requeuing to 'queued'.</summary>
	private async Task ExpireLeaseAndExhaustAttemptsAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"UPDATE jobs SET lease_expires_at = now() - interval '1 minute', attempt_count = max_attempts WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<(string JobState, string RunState)> ReadJobAndRunStateAsync(Guid jobId, Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT (SELECT state FROM jobs WHERE id = $1), (SELECT state FROM runs WHERE id = $2)", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		await reader.ReadAsync();
		return (reader.GetString(0), reader.GetString(1));
	}

	private async Task<DateTimeOffset> ReadExpiresAtAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT expires_at FROM run_secrets WHERE run_id = $1", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		await reader.ReadAsync();
		return reader.GetFieldValue<DateTimeOffset>(0);
	}

	private async Task<(string Status, DateTimeOffset? LastRefreshed)> ReadDiscoveryStateAsync(Guid targetId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT discovery_status, last_refreshed FROM targets WHERE id = $1", connection);
		command.Parameters.AddWithValue(targetId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		await reader.ReadAsync();
		return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
	}
}
