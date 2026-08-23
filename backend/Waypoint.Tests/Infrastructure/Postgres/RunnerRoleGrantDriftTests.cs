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
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
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

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
		command.Parameters.AddWithValue($"role-grant-drift-cred-{Guid.NewGuid():N}");
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
