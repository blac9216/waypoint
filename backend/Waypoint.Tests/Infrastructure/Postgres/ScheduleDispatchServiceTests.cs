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

using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Discovery;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Scheduling;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.ComplianceContent;
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
	private ProfileRepository _profiles = null!;

	/// <summary>Issue #639: scan schedules now require scope.profile_id -- seeded once per test (like the site/target rows) rather than per scan-schedule test.</summary>
	private Guid _profileId;

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
		_profiles = new ProfileRepository(_fixture.ConnectionString);
		await _profiles.ReplaceAllAsync(
			[new ProfileUpsert("vsphere-schedule-profile", "vSphere Schedule Test Profile", "1.0.0", "invented-commit-schedule", ProfileStates.Current)],
			CancellationToken.None);
		_profileId = (await _profiles.ListAsync(CancellationToken.None)).Single().Id;

		Waypoint.Infrastructure.Components.ComponentRepository componentRepository = new(_fixture.ConnectionString, new CatalogRepository(_fixture.ConnectionString));
		CatalogRepository catalogRepository = new(_fixture.ConnectionString);
		BaselineRepository baselineRepository = new(_fixture.ConnectionString);
		Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository configDocRepository = new(_fixture.ConnectionString);
		RunCreationService runCreation = new(
			jobs, _sites, _targets,
			new TargetCredentialBindingRepository(_fixture.ConnectionString),
			new Waypoint.Infrastructure.Secrets.CredentialRepository(_fixture.ConnectionString),
			_profiles,
			runSecrets, Options.Create(new DiscoveryOptions()), Options.Create(new RunSecretOptions()),
			new ScopeResolutionService(_targets, componentRepository, catalogRepository),
			new RunScopeSnapshotRepository(_fixture.ConnectionString),
			new ScanPlannerService(
				componentRepository, catalogRepository, baselineRepository, _targets,
				new Waypoint.Infrastructure.ConfigDocs.PlanConfigResolutionService(configDocRepository)),
			new ScanPlanRepository(_fixture.ConnectionString),
			componentRepository);

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
			await SeedSshCredentialAsync(), CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		Assert.NotNull(targetId);

		string scopeJson = System.Text.Json.JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId });
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

	/// <summary>
	/// Issue #520 / issue #31 acceptance criterion 1 ("a scheduled scan fires unattended
	/// with initiator 'scheduled'"): pins the scan branch end to end -- a due scan
	/// schedule's <c>site_id</c> scope is parsed by <see cref="RunCreationService.CreateScanRunAsync"/>,
	/// a target under that site is fanned out into a job, the created run's
	/// <c>initiated_by</c> is the dispatcher's <see cref="ScheduleDispatchService.ScheduledInitiator"/>
	/// (never the schedule's own <see cref="Schedule.CreatedBy"/>, which the API layer
	/// carries separately per the class doc on <see cref="ScheduleDispatchService"/>), and
	/// the schedule's <c>CreatedBy</c> attribution is left untouched by dispatch.
	/// </summary>
	[Fact]
	public async Task SweepAsync_DueScanSchedule_DispatchesRunWithScheduledInitiator_ParsesScopeAndFansOutTarget()
	{
		Guid siteId = (await _sites.CreateAsync("schedule-scan-e2e-site", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.Ssh, "schedule-scan-e2e-target", """{"host":"esxi-02.example.internal"}""",
			await SeedSshCredentialAsync(), CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		Assert.NotNull(targetId);

		string scopeJson = System.Text.Json.JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId });
		Guid id = (await _schedules.CreateAsync(
			$"sweep-scan-e2e-{Guid.NewGuid():N}", "scan", "* * * * *", scopeJson, null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "bob", CancellationToken.None))!.Value;

		await _dispatch.SweepAsync(CancellationToken.None);

		Schedule after = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.NotNull(after.LastRunId);
		// The schedule's own creator attribution is unaffected by dispatch -- it is the
		// API layer's job (SchedulesController.MapSchedule) to carry it on the wire, not
		// the dispatcher's job to touch it.
		Assert.Equal("bob", after.CreatedBy);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand runCommand = new("SELECT initiated_by, run_type FROM runs WHERE id = $1", connection);
		runCommand.Parameters.AddWithValue(after.LastRunId!.Value);
		await using NpgsqlDataReader runReader = await runCommand.ExecuteReaderAsync();
		Assert.True(await runReader.ReadAsync());
		Assert.Equal(ScheduleDispatchService.ScheduledInitiator, runReader.GetString(0));
		Assert.Equal("scan", runReader.GetString(1));
		await runReader.DisposeAsync();

		await using NpgsqlCommand jobCommand = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'scan' AND target_name = $2", connection);
		jobCommand.Parameters.AddWithValue(after.LastRunId!.Value);
		jobCommand.Parameters.AddWithValue("schedule-scan-e2e-target");
		Assert.Equal(1L, (long)(await jobCommand.ExecuteScalarAsync())!);
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

	/// <summary>
	/// Issue #585: scan dispatch resolves each target's required credential purpose
	/// from its bindings, so scan-schedule fixtures assign an ssh-type credential --
	/// TargetRepository's 0043 dual-write mirrors it into the srg-ssh binding the
	/// resolution step reads. This is also the stored-schedule compatibility proof:
	/// the schedule rows themselves are created with the SAME pre-#585 scope shape
	/// ({site_id, profile_id}), and dispatch keeps working unchanged.
	/// </summary>
	private async Task<Guid> SeedSshCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type, username) VALUES ($1, 'ssh', 'svc-schedule@example.internal') RETURNING id", connection);
		command.Parameters.AddWithValue($"schedule-scan-cred-{Guid.NewGuid():N}");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}
}
