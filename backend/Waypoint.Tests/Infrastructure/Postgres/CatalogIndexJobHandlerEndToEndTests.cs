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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Pagination;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #194 (epic #9 slice 2) full-loop acceptance, through the REAL loop: a
/// <c>catalog-index</c> job is fanned out, the dispatcher claims it, the real
/// <see cref="CatalogIndexJobHandler"/> resolves the depot-token credential, decrypts
/// it under job attribution (the #8 store), invokes the stub
/// <c>Invoke-WaypointCatalogIndex</c> module in-process, and the parsed rows land in
/// <c>depot_artifacts</c> via the slice-1 repository's idempotent upsert -- while the
/// token canary never reaches <c>job_events</c>, <c>jobs.note</c>, or argv.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class CatalogIndexJobHandlerEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointCatalogIndexStubModule", "WaypointCatalogIndexStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-catalog-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private CatalogIndexJobHandler _handler = null!;
	private DepotArtifactRepository _artifacts = null!;

	public CatalogIndexJobHandlerEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

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
		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);

		CatalogOptions catalogOptions = new() { DepotPath = "/invented/depot", DepotTokenCredentialType = "depot-token" };
		_handler = new CatalogIndexJobHandler(
			executor, _secretStore, _credentials, _artifacts, _repository, _redactor,
			Options.Create(catalogOptions), wrappedPsOptions);
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

	/// <summary>
	/// The full loop: fan out a <c>catalog-index</c> job (matching
	/// <c>CatalogController.Sync</c>'s <c>CredentialId: null</c> shape -- the handler
	/// must resolve the depot-token credential by type, not by a job-carried id) ->
	/// dispatcher claims it -> the real handler decrypts the token, invokes the stub
	/// module, upserts every row -> <c>depot_artifacts</c> has the rows and
	/// <c>run.progress</c> was emitted -> re-running the same job type again (a second
	/// sync) is idempotent (#193's acceptance criterion, proven through this handler).
	/// </summary>
	[Fact]
	public async Task SyncToDispatchToHandler_PopulatesArtifacts_EmitsProgress_AndIsIdempotentOnRerun()
	{
		const string canary = "invented-catalog-e2e-canary-b7c9";
		Guid credentialId = await SeedDepotTokenCredentialAsync(canary);

		Guid firstRunId = await RunCatalogIndexOnceAsync();
		await AssertArtifactRowCountAsync(3);
		Assert.True(await EventTypeExistsAsync(JobEventTypes.RunProgress, firstRunId));

		// Re-sync: same three external ids upsert in place rather than duplicating.
		await RunCatalogIndexOnceAsync();
		await AssertArtifactRowCountAsync(3);

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	[Fact]
	public async Task NoDepotTokenCredentialConfigured_FailsCleanly_WithoutThrowing()
	{
		Guid runId = await _repository.CreateRunAsync("catalog-index", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("catalog-index", 1, TargetName: "depot")], "tester", CancellationToken.None);

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

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("depot-token", note, StringComparison.Ordinal);
	}

	private async Task<Guid> RunCatalogIndexOnceAsync()
	{
		Guid runId = await _repository.CreateRunAsync("catalog-index", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("catalog-index", 1, TargetName: "depot")], "tester", CancellationToken.None);

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

		Assert.Equal("done", await GetJobFieldAsync(jobIds[0], "state"));
		return runId;
	}

	private async Task<Guid> SeedDepotTokenCredentialAsync(string secretValue)
	{
		Guid? credentialId = await _credentials.CreateAsync($"depot-token-{Guid.NewGuid():N}", "depot-token", "shared", sudoEnabled: false, CancellationToken.None);
		Assert.NotNull(credentialId);
		await _secretStore.StoreAsync(credentialId!.Value, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);
		return credentialId.Value;
	}

	private async Task AssertArtifactRowCountAsync(int expected)
	{
		(IReadOnlyList<DepotArtifact> items, long total) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest(), CancellationToken.None);
		Assert.Equal(expected, total);
		Assert.Equal(expected, items.Count);
	}

	/// <summary>
	/// security.md control 1/4 + the epic #6/#8 canary machinery, proven through THIS
	/// handler: the token never reaches job_events payloads or jobs.note, and the #8
	/// decrypt audit row carries this job's attribution.
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

		await using (NpgsqlCommand artifacts = new(
			"SELECT count(*) FROM depot_artifacts WHERE metadata::text LIKE '%' || $1 || '%'", connection))
		{
			artifacts.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await artifacts.ExecuteScalarAsync())!);
		}

		// The #8 audit trail: at least one secret.decrypted row for this credential,
		// attributed to a job (job_id NOT NULL) -- proven through this handler, not
		// just at the secrets-store unit level.
		await using (NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id IS NOT NULL", connection))
		{
			audited.Parameters.AddWithValue(credentialId);
			Assert.True((long)(await audited.ExecuteScalarAsync())! >= 1);
		}
	}

	private async Task<bool> EventTypeExistsAsync(string eventType, Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE event_type = $1 AND run_id = $2", connection);
		query.Parameters.AddWithValue(eventType);
		query.Parameters.AddWithValue(runId);
		return (long)(await query.ExecuteScalarAsync())! > 0;
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
		Stopwatch stopwatch = Stopwatch.StartNew();
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
}
