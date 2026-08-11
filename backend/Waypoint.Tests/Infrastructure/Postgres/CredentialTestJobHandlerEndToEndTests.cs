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
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Credentials;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #245 (epic #13) full-loop acceptance, through the REAL loop:
/// <c>POST /credentials/{id}/test</c>'s fan-out shape is exercised directly against
/// the queue (a <c>credential-test</c> job is fanned out) -> the dispatcher claims it
/// -> the real <see cref="CredentialTestJobHandler"/> resolves a target referencing the
/// credential for its host, decrypts the credential under job attribution (the #8
/// store), invokes the stub per-type connectivity function in-process, and flips
/// <c>credentials.health</c> from the job's terminal outcome -- while the password
/// canary never reaches <c>job_events</c> or <c>jobs.note</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class CredentialTestJobHandlerEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointCredentialTestStubModule", "WaypointCredentialTestStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-credtest-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private CredentialTestJobHandler _handler = null!;

	public CredentialTestJobHandlerEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetSitesAndTargetsAsync();
		Environment.SetEnvironmentVariable("WAYPOINT_CREDENTIAL_TEST_STUB_MODE", "success");

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 2 };
		powerShellOptions.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedPsOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedPsOptions, NullLogger<PowerShellExecutor>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);

		_handler = new CredentialTestJobHandler(executor, _secretStore, _credentials, _targets, _repository, _redactor, wrappedPsOptions);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	private JobDispatcherHostedService CreateDispatcher()
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 };
		return new JobDispatcherHostedService(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	[Fact]
	public async Task VCenterCredential_SuccessfulTest_MarksHealthValid()
	{
		const string canary = "invented-credtest-vcenter-canary-9c2e";
		(Guid credentialId, _) = await SeedVCenterCredentialWithTargetAsync(canary);

		Guid jobId = await RunTestOnceAsync(credentialId);
		Assert.Equal("done", await GetJobFieldAsync(jobId, "state"));
		Assert.Equal(CredentialHealthStates.Valid, (await _credentials.GetAsync(credentialId, CancellationToken.None))!.Health);

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	[Fact]
	public async Task VCenterCredential_AuthFailure_MarksHealthAuthFailing()
	{
		(Guid credentialId, _) = await SeedVCenterCredentialWithTargetAsync("invented-credtest-auth-canary-1a7d");
		Environment.SetEnvironmentVariable("WAYPOINT_CREDENTIAL_TEST_STUB_MODE", "auth");

		Guid jobId = await RunTestOnceAsync(credentialId, expectedState: "auth-failed");
		Assert.Equal(CredentialHealthStates.AuthFailing, (await _credentials.GetAsync(credentialId, CancellationToken.None))!.Health);
		Assert.Contains("401", await GetJobNoteAsync(jobId), StringComparison.Ordinal);
	}

	[Fact]
	public async Task VCenterCredential_OrdinaryFailure_MarksHealthAuthFailing_ButJobStateIsFailed()
	{
		(Guid credentialId, _) = await SeedVCenterCredentialWithTargetAsync("invented-credtest-fail-canary-2b8e");
		Environment.SetEnvironmentVariable("WAYPOINT_CREDENTIAL_TEST_STUB_MODE", "failure");

		Guid jobId = await RunTestOnceAsync(credentialId, expectedState: "failed");
		Assert.Equal(CredentialHealthStates.AuthFailing, (await _credentials.GetAsync(credentialId, CancellationToken.None))!.Health);
		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
	}

	[Fact]
	public async Task CredentialWithNoReferencingTarget_FailsCleanly_WithoutThrowing()
	{
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-orphan-{Guid.NewGuid():N}", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false,
			CancellationToken.None, "administrator@example.internal"))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes("invented-secret"), "test", CancellationToken.None);

		Guid jobId = await RunTestOnceAsync(credentialId, expectedState: "failed");
		Assert.Contains("not referenced by any target", await GetJobNoteAsync(jobId), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TokenCredential_IsDecryptOnly_NeverInvokesPowerShell()
	{
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-token-{Guid.NewGuid():N}", CredentialTypes.Token, CredentialOwners.Shared, sudoEnabled: false,
			CancellationToken.None, username: null))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes("invented-token-value"), "test", CancellationToken.None);

		// No target references this credential at all -- a token test must still
		// succeed, proving it never tries to resolve a host.
		Guid jobId = await RunTestOnceAsync(credentialId);
		Assert.Equal(CredentialHealthStates.Valid, (await _credentials.GetAsync(credentialId, CancellationToken.None))!.Health);
		Assert.Contains("no dialable endpoint", await GetJobNoteAsync(jobId), StringComparison.Ordinal);
	}

	[Fact]
	public async Task DepotTokenCredential_IsDecryptOnly_NeverInvokesPowerShell()
	{
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-depot-token-{Guid.NewGuid():N}", CredentialTypes.DepotToken, CredentialOwners.Shared, sudoEnabled: false,
			CancellationToken.None, username: null))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes("invented-depot-token-value"), "test", CancellationToken.None);

		// No target references this credential at all -- a depot-token test must still
		// succeed, proving it never tries to resolve a host (the depot has no
		// connection-target representation to begin with).
		Guid jobId = await RunTestOnceAsync(credentialId);
		Assert.Equal(CredentialHealthStates.Valid, (await _credentials.GetAsync(credentialId, CancellationToken.None))!.Health);
		Assert.Contains("no dialable endpoint", await GetJobNoteAsync(jobId), StringComparison.Ordinal);
	}

	private async Task<Guid> RunTestOnceAsync(Guid credentialId, string expectedState = "done")
	{
		Guid runId = await _repository.CreateRunAsync("credential-test", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("credential-test", 4, CredentialId: credentialId)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal(expectedState, await GetJobFieldAsync(jobIds[0], "state"));
		return jobIds[0];
	}

	private async Task<(Guid CredentialId, Guid TargetId)> SeedVCenterCredentialWithTargetAsync(string secretValue)
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-credtest-{Guid.NewGuid():N}", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false,
			CancellationToken.None, "administrator@example.internal"))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		return (credentialId, targetId!.Value);
	}

	/// <summary>
	/// security.md control 1/4 + the epic #6/#8 canary machinery, proven through THIS
	/// handler: the credential value never reaches job_events payloads or jobs.note,
	/// and the #8 decrypt audit row carries this job's attribution.
	/// </summary>
	private async Task AssertCanaryNeverLeakedAsync(string canary, Guid credentialId)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand leaked = new(
			"SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", connection))
		{
			leaked.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand notes = new(
			"SELECT count(*) FROM jobs WHERE note LIKE '%' || $1 || '%'", connection))
		{
			notes.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await notes.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id IS NOT NULL", connection))
		{
			audited.Parameters.AddWithValue(credentialId);
			Assert.True((long)(await audited.ExecuteScalarAsync())! >= 1);
		}
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobNoteAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT COALESCE(note, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}

	private async Task ResetSitesAndTargetsAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE TABLE targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}
}
