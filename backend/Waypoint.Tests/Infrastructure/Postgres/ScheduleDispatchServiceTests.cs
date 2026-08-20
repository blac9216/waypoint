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
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Discovery;
using Waypoint.Core.Logging;
using Waypoint.Core.Scheduling;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Scheduling;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Infrastructure.SystemState;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #31, end to end against real Postgres: a due schedule is dispatched into a
/// real run/job pair via the same <see cref="Waypoint.Core.Jobs.IJobControlRepository"/>/
/// <see cref="RunCreationService"/> surface every controller uses (never claims/executes
/// -- ADR-0013's control-plane-producer boundary), the run's initiator is recorded as
/// "scheduled" (docs/domain-model.md Scheduling), next_run_at advances, and a depot-kind
/// schedule auto-pauses/auto-resumes as the appliance's mode flips. Exercises the
/// non-scan read-only types (discover/credential-test/catalog-index) end to end;
/// scheduled `scan`'s per-target fan-out reuses <see cref="RunCreationService.CreateScanRunAsync"/>
/// verbatim, already covered against real Postgres by <c>ScanRunFanOutTests</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync/Dispose remove the temp key dir.
public sealed class ScheduleDispatchServiceTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-schedule-dispatch-key").FullName;
	private ScheduleRepository _schedules = null!;
	private ScheduleDispatchService _dispatch = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;

	public ScheduleDispatchServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await SetModeAsync("connected");

		_schedules = new ScheduleRepository(_fixture.ConnectionString);
		JobQueueRepository jobs = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(keyPath));
		RunSecretStore runSecrets = new(
			_fixture.ConnectionString, cipher, new InPlaySecretRedactor(), Options.Create(new RunSecretOptions()), NullLogger<RunSecretStore>.Instance);

		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		RunCreationService runCreation = new(
			jobs, _sites, _targets,
			runSecrets, Options.Create(new DiscoveryOptions()), Options.Create(new RunSecretOptions()));

		IApplianceStateRepository applianceState = new ApplianceStateRepository(_fixture.ConnectionString);
		_dispatch = new ScheduleDispatchService(_schedules, jobs, applianceState, runCreation, NullLogger<ScheduleDispatchService>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose() => Directory.Delete(_keyDirectory, recursive: true);

	[Fact]
	public async Task SweepAsync_DueDiscoverSchedule_DispatchesARun_RecordsScheduledInitiator_AndAdvancesNextRun()
	{
		Guid id = (await _schedules.CreateAsync(
			$"sweep-discover-{Guid.NewGuid():N}", "discover", "* * * * *", "{}", null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.NotNull(after.LastRunId);
		Assert.True(after.NextRunAt > DateTimeOffset.UtcNow);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT initiated_by, run_type FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(after.LastRunId!.Value);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(ScheduleDispatchService.ScheduledInitiator, reader.GetString(0));
		Assert.Equal("discover", reader.GetString(1));
	}

	/// <summary>
	/// Issue #515: the dispatcher must stamp <c>runs.schedule_id</c> on the run it
	/// creates -- the non-scan branch calls <see cref="Waypoint.Core.Jobs.IJobControlRepository.CreateRunAsync"/>
	/// directly (not through <see cref="RunCreationService.CreateScanRunAsync"/>), so
	/// this is a separate code path from the scan one covered below.
	/// </summary>
	[Fact]
	public async Task SweepAsync_DiscoverSchedule_StampsScheduleIdOnTheCreatedRun()
	{
		Guid id = (await _schedules.CreateAsync(
			$"sweep-discover-schedid-{Guid.NewGuid():N}", "discover", "* * * * *", "{}", null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.NotNull(after.LastRunId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT schedule_id FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(after.LastRunId!.Value);
		object? scheduleId = await command.ExecuteScalarAsync();
		Assert.Equal(id, (Guid)scheduleId!);
	}

	/// <summary>
	/// Issue #515: the scan branch dispatches through <see cref="RunCreationService.CreateScanRunAsync"/>
	/// (a different code path from the direct <see cref="Waypoint.Core.Jobs.IJobControlRepository.CreateRunAsync"/>
	/// call the non-scan branch uses) -- both must stamp <c>schedule_id</c>.
	/// </summary>
	[Fact]
	public async Task SweepAsync_ScanSchedule_StampsScheduleIdOnTheCreatedRun()
	{
		Guid siteId = (await _sites.CreateAsync("schedule-scan-site", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.Ssh, "schedule-scan-target", """{"host":"esxi-01.example.internal"}""",
			credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		Assert.NotNull(targetId);

		string scopeJson = System.Text.Json.JsonSerializer.Serialize(new { site_id = siteId });
		Guid id = (await _schedules.CreateAsync(
			$"sweep-scan-schedid-{Guid.NewGuid():N}", "scan", "* * * * *", scopeJson, null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.NotNull(after.LastRunId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT schedule_id FROM runs WHERE id = $1", connection);
		command.Parameters.AddWithValue(after.LastRunId!.Value);
		object? scheduleId = await command.ExecuteScalarAsync();
		Assert.Equal(id, (Guid)scheduleId!);
	}

	[Fact]
	public async Task SweepAsync_CredentialTestSchedule_DispatchesOneJob()
	{
		Guid id = (await _schedules.CreateAsync(
			$"sweep-credtest-{Guid.NewGuid():N}", "credential-test", "* * * * *", "{}", null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.NotNull(after.LastRunId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'credential-test'", connection);
		command.Parameters.AddWithValue(after.LastRunId!.Value);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task SweepAsync_NotYetDueSchedule_IsNotDispatched()
	{
		Guid id = (await _schedules.CreateAsync(
			$"sweep-future-{Guid.NewGuid():N}", "discover", "0 2 * * *", "{}", null,
			DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None))!.Value;

		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.Null(after.LastRunId);
	}

	[Fact]
	public async Task SweepAsync_DepotKindSchedule_AutoPausesWhenDisconnected_AndResumesWhenReconnected()
	{
		Guid id = (await _schedules.CreateAsync(
			$"sweep-depot-{Guid.NewGuid():N}", "catalog-index", "* * * * *", "{}", null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		await SetModeAsync("disconnected");
		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule paused = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.Equal(ScheduleDispatchService.AutoPauseReason, paused.PausedReason);
		Assert.Null(paused.LastRunId); // never dispatched while paused

		await SetModeAsync("connected");
		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule resumed = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.Null(resumed.PausedReason);
		Assert.NotNull(resumed.LastRunId); // now dispatches once unpaused and due
	}

	/// <summary>A non-depot schedule (scan/discover/credential-test) is never touched by the disconnected-mode auto-pause -- only catalog-index is a depot kind.</summary>
	[Fact]
	public async Task SweepAsync_NonDepotSchedule_IsNeverAutoPaused()
	{
		Guid id = (await _schedules.CreateAsync(
			$"sweep-nondepot-{Guid.NewGuid():N}", "credential-test", "0 2 * * *", "{}", null,
			DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None))!.Value;

		await SetModeAsync("disconnected");
		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.Null(after.PausedReason);
	}

	private async Task SetModeAsync(string mode)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE appliance_state SET mode = $1 WHERE id = 1", connection);
		command.Parameters.AddWithValue(mode);
		await command.ExecuteNonQueryAsync();
	}
}
