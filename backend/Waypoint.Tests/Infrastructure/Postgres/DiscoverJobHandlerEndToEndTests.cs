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
using Waypoint.Core.Discovery;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #21 (epic #13) full-loop acceptance, through the REAL loop: a
/// <c>discover</c> job is fanned out, the dispatcher claims it, the real
/// <see cref="DiscoverJobHandler"/> resolves the target's vCenter credential,
/// decrypts it under job attribution (the #8 store), invokes the stub
/// <c>Invoke-WaypointDiscovery</c> module in-process, and the parsed cluster/host/vm
/// rows land in <c>inventory_items</c> via <see cref="InventoryRepository"/> --
/// idempotent on re-discovery (upsert, no duplicate hosts) and detecting removals --
/// while the password canary never reaches <c>job_events</c> or <c>jobs.note</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class DiscoverJobHandlerEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDiscoveryStubModule", "WaypointDiscoveryStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-discover-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private InventoryRepository _inventory = null!;
	private DiscoverJobHandler _handler = null!;

	public DiscoverJobHandlerEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetDiscoveryDataAsync();

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
		_inventory = new InventoryRepository(_fixture.ConnectionString);

		_handler = new DiscoverJobHandler(
			executor, _secretStore, _credentials, _targets, _inventory, _repository, _redactor, wrappedPsOptions);
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
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	/// <summary>
	/// The full loop: fan out a <c>discover</c> job for a seeded vsphere target ->
	/// dispatcher claims it -> the real handler decrypts the credential, invokes the
	/// stub module (pass 1: cluster + 2 hosts + 1 vm) -> inventory_items has the rows,
	/// discover.progress was emitted, and target.discovery_status/last_refreshed
	/// updated -> re-running (pass 2: one host dropped) upserts the survivors
	/// in place (no duplicates) AND marks the dropped host removed.
	/// </summary>
	[Fact]
	public async Task SyncToDispatchToHandler_PopulatesInventory_UpsertsIdempotently_AndDetectsRemovals()
	{
		const string canary = "invented-discover-e2e-canary-f4a1";
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync(canary);

		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		Guid firstRunId = await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 4); // cluster + 2 hosts + 1 vm
		Assert.True(await EventTypeExistsAsync(JobEventTypes.DiscoverProgress, firstRunId));
		Assert.Equal(TargetDiscoveryStatuses.Discovered, (await _targets.GetAsync(targetId, CancellationToken.None))!.DiscoveryStatus);
		Assert.NotNull((await _targets.GetAsync(targetId, CancellationToken.None))!.LastRefreshed);

		// Re-discover with the SAME pass: same morefs upsert in place, not duplicated.
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 4);

		// Pass 2: host-12 is no longer reported -- removal detection marks it (and its
		// child, cascaded by ON DELETE CASCADE semantics N/A here since this is a soft
		// delete -- host-12 itself carries no children in this fixture) removed_at, and
		// EXCLUDES it from the active listing, without deleting the surviving 3 rows.
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "2");
		await RunDiscoverOnceAsync(targetId);
		await AssertInventoryCountAsync(targetId, expectedActive: 3);
		await AssertItemRemovedAsync(targetId, "host-12");

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	[Fact]
	public async Task TargetWithNoCredential_FailsCleanly_WithoutThrowing()
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		string connectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", connectionJson, credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId!.Value });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

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
		Assert.Contains("no credential", note, StringComparison.Ordinal);
		Assert.Equal(TargetDiscoveryStatuses.NeverDiscovered, (await _targets.GetAsync(targetId.Value, CancellationToken.None))!.DiscoveryStatus);
	}

	private async Task<Guid> RunDiscoverOnceAsync(Guid targetId)
	{
		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("discover", 4, TargetId: targetId, Payload: payload)], "tester", CancellationToken.None);

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

	private async Task<(Guid TargetId, Guid CredentialId)> SeedVsphereTargetAsync(string secretValue)
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			"administrator@vsphere.local", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		return (targetId!.Value, credentialId);
	}

	private async Task AssertInventoryCountAsync(Guid targetId, int expectedActive)
	{
		IReadOnlyList<InventoryItem> active = await _inventory.ListAsync(targetId, includeRemoved: false, CancellationToken.None);
		Assert.Equal(expectedActive, active.Count);
	}

	private async Task AssertItemRemovedAsync(Guid targetId, string moref)
	{
		IReadOnlyList<InventoryItem> all = await _inventory.ListAsync(targetId, includeRemoved: true, CancellationToken.None);
		InventoryItem? item = all.FirstOrDefault(i => i.Moref == moref);
		Assert.NotNull(item);
		Assert.NotNull(item!.RemovedAt);
	}

	/// <summary>
	/// security.md control 1/4 + the epic #6/#8 canary machinery, proven through THIS
	/// handler: the credential value never reaches job_events payloads, jobs.note, or
	/// inventory_items, and the #8 decrypt audit row carries this job's attribution.
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

		await using (NpgsqlCommand items = new(
			"SELECT count(*) FROM inventory_items WHERE name LIKE '%' || $1 || '%' OR build LIKE '%' || $1 || '%'", connection))
		{
			items.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await items.ExecuteScalarAsync())!);
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
			"SELECT count(*) FROM job_events e JOIN jobs j ON j.id = e.job_id WHERE e.event_type = $1 AND j.run_id = $2", connection);
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

	private async Task ResetDiscoveryDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE inventory_items, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}
}
