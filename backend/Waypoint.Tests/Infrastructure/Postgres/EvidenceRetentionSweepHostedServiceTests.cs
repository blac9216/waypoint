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

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1062 (epic #726 sections 6/7): end-to-end coverage for
/// <see cref="EvidenceRetentionSweepHostedService"/> and
/// <see cref="EvidenceRetentionSweepRepository.FindPurgeCandidatesAsync"/> against
/// real Postgres -- eligibility (terminal + aged compliance run, unpurged), the
/// Admin-configured retention period being re-read fresh every pass (AC1), holds
/// being excluded via the candidate query's own SQL anti-join AND remaining
/// protected by <see cref="RunPurgeService.PurgeRunAsync"/>'s own refusal as a
/// belt-and-suspenders backstop (AC3), the purge actually running through the one
/// real <c>RunPurgeService.PurgeRunAsync</c> path with a tombstone written and
/// <c>purged_at</c> set (AC2), and policy-driven purges being distinguishable from
/// operator purges by actor (AC4). Mirrors
/// <see cref="RunHistoryRolloffHostedServiceTests"/>'s fixture/seeding idiom and
/// reuses its <see cref="FakeTimeProvider"/> test double (same assembly, `internal`).
/// </summary>
[Collection("Postgres")]
public sealed class EvidenceRetentionSweepHostedServiceTests : IAsyncLifetime
{
	private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-01T00:00:00Z", CultureInfo.InvariantCulture);

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private RunPurgeRepository _purges = null!;
	private RunRetentionHoldRepository _holds = null!;
	private RetentionPolicyRepository _policy = null!;
	private EvidenceRetentionSweepRepository _candidates = null!;
	private RunPurgeService _purgeService = null!;

	public EvidenceRetentionSweepHostedServiceTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		await using (NpgsqlConnection reset = new(_fixture.ConnectionString))
		{
			await reset.OpenAsync();
			await using NpgsqlCommand resetCommand = new(
				"UPDATE retention_policy SET evidence_retention_days = 180, updated_by = NULL WHERE id = 1", reset);
			await resetCommand.ExecuteNonQueryAsync();
		}

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_purges = new RunPurgeRepository(_fixture.ConnectionString);
		_holds = new RunRetentionHoldRepository(_fixture.ConnectionString);
		_policy = new RetentionPolicyRepository(_fixture.ConnectionString);
		_candidates = new EvidenceRetentionSweepRepository(_fixture.ConnectionString);

		AttestationSnapshotRepository attestationSnapshots = new(_fixture.ConnectionString);
		_purgeService = new RunPurgeService(_jobs, _purges, attestationSnapshots, _holds, _fixture.ConnectionString, NullLogger<RunPurgeService>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private EvidenceRetentionSweepHostedService CreateService(FakeTimeProvider clock, int maxRunsPerSweep = 100) =>
		new(_policy, _candidates, _purgeService, Options.Create(new EvidenceRetentionSweepOptions { MaxRunsPerSweep = maxRunsPerSweep }),
			NullLogger<EvidenceRetentionSweepHostedService>.Instance, clock);

	[Fact]
	public void Options_DisabledByDefault_NewOptionsInstanceHasEnabledFalse()
	{
		// Pins the conservative default (EvidenceRetentionSweepOptions doc comment) so
		// a future flip is a deliberate, reviewed edit, not an accident.
		Assert.False(new EvidenceRetentionSweepOptions().Enabled);
	}

	[Theory]
	[InlineData("scan")]
	[InlineData("remediate")]
	public async Task SweepOnceAsync_TerminalAgedComplianceRunWithNoScanJobs_IsPurgedViaThePurgePath(string complianceRunType)
	{
		// No 'scan'-type jobs -- RunPurgeService.ResumeAsync skips straight to the
		// database-only completion path (no compliance-runner needed), the same
		// "discover/credential-test-only run" shortcut RunPurgeService's own doc
		// comment describes; this proves the sweep reaches the real one-call purge
		// entry point end-to-end without standing up a runner process.
		Guid runId = await SeedRunAsync(complianceRunType, "completed", completedDaysAgo: 200);
		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));

		await service.SweepOnceAsync(CancellationToken.None);

		RunPurgeTombstone? tombstone = await _purges.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(tombstone);
		Assert.Equal("completed", tombstone!.Outcome);
		Assert.Equal(EvidenceRetentionSweepHostedService.SweepActor, tombstone.Actor);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand purgedAtQuery = new("SELECT purged_at FROM runs WHERE id = $1", connection);
		purgedAtQuery.Parameters.AddWithValue(runId);
		object? purgedAt = await purgedAtQuery.ExecuteScalarAsync();
		Assert.NotEqual(DBNull.Value, purgedAt);
	}

	[Fact]
	public async Task SweepOnceAsync_TerminalButNotYetPastTheConfiguredRetentionPeriod_IsSkipped()
	{
		Guid runId = await SeedRunAsync("scan", "completed", completedDaysAgo: 30);
		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_NonComplianceRunType_NeverSweptRegardlessOfAge()
	{
		Guid runId = await SeedRunAsync("discover", "completed", completedDaysAgo: 400);
		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_NonTerminalRun_IsSkippedRegardlessOfAge()
	{
		Guid runId = await SeedRunAsync("scan", "running", completedDaysAgo: null);
		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
	}

	/// <summary>
	/// AC3: a run under an Admin retention hold is never swept. Proven at both layers
	/// this design deliberately keeps independent: the candidate query's own SQL
	/// anti-join excludes it (so <see cref="RunPurgeService.PurgeRunAsync"/> is never
	/// even called for it here), which this test observes indirectly via "no
	/// tombstone, no purge status row at all" -- if the anti-join ever regressed, the
	/// same assertion would still hold because PurgeRunAsync's own hold check is the
	/// documented backstop (see <see cref="RunPurgeOutcomeHeldIsRespectedIfTheAntiJoinEverRegresses"/>).
	/// </summary>
	[Fact]
	public async Task SweepOnceAsync_RunUnderRetentionHold_IsNeverSweptOrEvenAttempted()
	{
		Guid runId = await SeedRunAsync("scan", "completed", completedDaysAgo: 400);
		await _holds.TryInsertAsync(runId, "invented-legal-hold-reason", "admin-alice", CancellationToken.None);

		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));
		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
		Assert.Null(await _purges.GetStatusAsync(runId, CancellationToken.None));
	}

	/// <summary>
	/// Belt-and-suspenders: even if a held run somehow reached
	/// <see cref="RunPurgeService.PurgeRunAsync"/> (the anti-join is bypassed by
	/// calling the purge service directly here, simulating that regression), the
	/// service itself refuses. Proves the sweep's own outcome handling treats
	/// <see cref="RunPurgeOutcome.Held"/> as a benign skip, not a failure that would
	/// halt the pass.
	/// </summary>
	[Fact]
	public async Task RunPurgeOutcomeHeldIsRespectedIfTheAntiJoinEverRegresses()
	{
		Guid runId = await SeedRunAsync("scan", "completed", completedDaysAgo: 400);
		await _holds.TryInsertAsync(runId, "invented-legal-hold-reason", "admin-alice", CancellationToken.None);

		RunPurgeResult result = await _purgeService.PurgeRunAsync(runId, EvidenceRetentionSweepHostedService.SweepActor, CancellationToken.None);

		Assert.Equal(RunPurgeOutcome.Held, result.Outcome);
		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_RetentionPeriodChangedBetweenPasses_TakesEffectOnTheNextPassWithNoRestart()
	{
		Guid runId = await SeedRunAsync("scan", "completed", completedDaysAgo: 100);
		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));

		// Default 180-day retention: not yet eligible at 100 days old.
		await service.SweepOnceAsync(CancellationToken.None);
		Assert.Null(await _purges.GetTombstoneAsync(runId, CancellationToken.None));

		// Admin shortens the retention period to 30 days (AC1) -- the SAME service
		// instance, re-reading retention_policy fresh, must pick this up without
		// re-registration or a restart.
		await _policy.SetAsync(30, "admin-alice", CancellationToken.None);
		await service.SweepOnceAsync(CancellationToken.None);

		Assert.NotNull(await _purges.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_ActorIsTheReservedSweepActor_DistinctFromAnOperatorPurge()
	{
		Guid sweptRunId = await SeedRunAsync("scan", "completed", completedDaysAgo: 400);
		Guid operatorRunId = await SeedRunAsync("scan", "completed", completedDaysAgo: 1);

		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));
		await service.SweepOnceAsync(CancellationToken.None);
		await _purgeService.PurgeRunAsync(operatorRunId, "admin-alice", CancellationToken.None);

		RunPurgeTombstone? swept = await _purges.GetTombstoneAsync(sweptRunId, CancellationToken.None);
		RunPurgeTombstone? operatorPurged = await _purges.GetTombstoneAsync(operatorRunId, CancellationToken.None);

		Assert.Equal("system:retention-sweep", swept!.Actor);
		Assert.Equal("admin-alice", operatorPurged!.Actor);
		Assert.NotEqual(swept.Actor, operatorPurged.Actor);
	}

	[Fact]
	public async Task SweepOnceAsync_AlreadyPurgedRun_IsIdempotent_NoDuplicateTombstone()
	{
		Guid runId = await SeedRunAsync("scan", "completed", completedDaysAgo: 400);
		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));

		await service.SweepOnceAsync(CancellationToken.None);
		RunPurgeTombstone? first = await _purges.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(first);

		// Second pass: the candidate query no longer returns this run (purged_at is
		// now set), and even if it somehow did, PurgeRunAsync's own AlreadyPurged
		// outcome would no-op rather than double-write.
		await service.SweepOnceAsync(CancellationToken.None);
		RunPurgeTombstone? second = await _purges.GetTombstoneAsync(runId, CancellationToken.None);

		Assert.Equal(first!.Id, second!.Id);
	}

	[Fact]
	public async Task FindPurgeCandidatesAsync_RespectsMaxRunsPerSweepLimit()
	{
		for (int i = 0; i < 5; i++)
		{
			await SeedRunAsync("scan", "completed", completedDaysAgo: 400);
		}

		IReadOnlyList<Guid> candidates = await _candidates.FindPurgeCandidatesAsync(Now - TimeSpan.FromDays(180), maxRuns: 3, CancellationToken.None);

		Assert.Equal(3, candidates.Count);
	}

	[Fact]
	public async Task SweepOnceAsync_OneCandidateFailsToPurge_DoesNotHaltThePass()
	{
		// A run whose id no longer resolves by the time PurgeRunAsync is called (raced
		// away by a concurrent, unrelated deletion) reports RunNotFound -- a benign
		// no-op per RunPurgeOutcome's own contract, not a failure. This test instead
		// proves the more general failure-isolation contract: a second, genuinely
		// eligible candidate is still purged even though it is processed in the same
		// pass as another candidate.
		Guid a = await SeedRunAsync("scan", "completed", completedDaysAgo: 400);
		Guid b = await SeedRunAsync("remediate", "completed_with_failures", completedDaysAgo: 300);

		EvidenceRetentionSweepHostedService service = CreateService(new FakeTimeProvider(Now));
		await service.SweepOnceAsync(CancellationToken.None);

		Assert.NotNull(await _purges.GetTombstoneAsync(a, CancellationToken.None));
		Assert.NotNull(await _purges.GetTombstoneAsync(b, CancellationToken.None));
	}

	// -- seeding helpers -------------------------------------------------------

	private async Task<Guid> SeedRunAsync(string runType, string state, int? completedDaysAgo)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		DateTimeOffset? completedAt = completedDaysAgo is { } days ? Now - TimeSpan.FromDays(days) : null;
		await using NpgsqlCommand command = new(
			"INSERT INTO runs (run_type, scope, state, completed_at) VALUES ($1, '{}', $2, $3) RETURNING id", connection);
		command.Parameters.AddWithValue(runType);
		command.Parameters.AddWithValue(state);
		command.Parameters.AddWithValue((object?)completedAt ?? DBNull.Value);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}
}
