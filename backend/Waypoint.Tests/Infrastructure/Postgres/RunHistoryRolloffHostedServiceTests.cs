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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #708 (epic #706): end-to-end coverage for
/// <see cref="RunHistoryRolloffHostedService"/> and
/// <see cref="RunHistoryDeletionRepository.FindRolloffCandidatesAsync"/> against real
/// Postgres -- the gate-respect guarantees (compliance runs never swept, only
/// terminal+aged non-compliance runs are candidates), idempotency (a second sweep pass
/// is a no-op for already-deleted runs), and failure isolation (one bad candidate does
/// not halt the pass) all depend on real SQL/service wiring a fake repository cannot
/// exercise end-to-end. Mirrors <see cref="RunHistoryDeletionServiceTests"/>'s fixture
/// and seeding idiom.
/// </summary>
[Collection("Postgres")]
public sealed class RunHistoryRolloffHostedServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private RunHistoryDeletionRepository _deletions = null!;
	private RunHistoryDeletionService _deletionService = null!;

	public RunHistoryRolloffHostedServiceTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_deletions = new RunHistoryDeletionRepository(_fixture.ConnectionString);
		_deletionService = new RunHistoryDeletionService(_jobs, _deletions);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private RunHistoryRolloffHostedService CreateService(FakeTimeProvider clock) =>
		new(_deletions, _deletionService, Options.Create(new RunHistoryRolloffOptions()), NullLogger<RunHistoryRolloffHostedService>.Instance, clock);

	[Fact]
	public void SweepOnceAsync_DisabledByDefault_NewOptionsInstanceHasEnabledFalse()
	{
		// Not a behavioral test of the sweep itself -- pins the "conservative default"
		// design decision (RunHistoryRolloffOptions doc comment) so a future change to
		// the default is a deliberate, reviewed edit to that file, not an accidental
		// flip of a bool default somewhere.
		Assert.False(new RunHistoryRolloffOptions().Enabled);
	}

	[Fact]
	public async Task SweepOnceAsync_TerminalAgedNonComplianceRun_IsDeleted()
	{
		Guid runId = await SeedRunAsync("discover", "completed", completedDaysAgo: 100);
		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.NotNull(await _deletions.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_TerminalButNotYetAged_IsSkipped()
	{
		Guid runId = await SeedRunAsync("discover", "completed", completedDaysAgo: 1);
		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _deletions.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_NonTerminalRun_IsSkippedRegardlessOfAge()
	{
		Guid runId = await SeedRunAsync("discover", "running", completedDaysAgo: null);
		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _deletions.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Theory]
	[InlineData("scan")]
	[InlineData("remediate")]
	public async Task SweepOnceAsync_ComplianceRun_NeverSweptRegardlessOfPurgeState(string complianceRunType)
	{
		// Unpurged: the candidate query excludes scan/remediate outright (never even
		// reaches RunHistoryDeletionService's 409 gate).
		Guid unpurged = await SeedRunAsync(complianceRunType, "completed", completedDaysAgo: 365);

		// Purged: epic #706's "windowed, not deleted" design -- this sweep does not
		// pick up a compliance run even after purge (see
		// RunHistoryRolloffHostedService's doc comment for the justification).
		Guid purged = await SeedRunAsync(complianceRunType, "completed", completedDaysAgo: 365);
		await MarkRunPurgedAsync(purged);

		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.Null(await _deletions.GetTombstoneAsync(unpurged, CancellationToken.None));
		Assert.Null(await _deletions.GetTombstoneAsync(purged, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_AlreadyDeletedRun_IsIdempotent_NoDuplicateTombstone()
	{
		Guid runId = await SeedRunAsync("discover", "completed", completedDaysAgo: 100);
		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);
		RunHistoryDeletionTombstone? first = await _deletions.GetTombstoneAsync(runId, CancellationToken.None);
		Assert.NotNull(first);

		// Second pass: the candidate query no longer returns this run (history_deleted_at
		// is now set), and even if it somehow did, DeleteHistoryAsync's own
		// AlreadyDeleted outcome would no-op rather than double-write.
		await service.SweepOnceAsync(CancellationToken.None);
		RunHistoryDeletionTombstone? second = await _deletions.GetTombstoneAsync(runId, CancellationToken.None);

		Assert.Equal(first!.Id, second!.Id);
	}

	[Fact]
	public async Task SweepOnceAsync_OneCandidateDeletedBetweenLookupAndDeletion_DoesNotHaltThePass()
	{
		// Simulates a race: a concurrent manual DELETE /runs/{id}/history already
		// covered one candidate between FindRolloffCandidatesAsync's read and this
		// sweep's own DeleteHistoryAsync call. The service must treat the resulting
		// AlreadyDeleted outcome as benign and continue -- proven here by seeding a
		// second, genuinely-fresh candidate that must still be swept.
		Guid racedAway = await SeedRunAsync("discover", "completed", completedDaysAgo: 100);
		await _deletionService.DeleteHistoryAsync(racedAway, "manual-admin", CancellationToken.None);

		Guid stillPending = await SeedRunAsync("download", "completed", completedDaysAgo: 100);

		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.NotNull(await _deletions.GetTombstoneAsync(stillPending, CancellationToken.None));
	}

	[Fact]
	public async Task SweepOnceAsync_MultipleEligibleRuns_AllDeletedInOnePass()
	{
		Guid a = await SeedRunAsync("discover", "completed", completedDaysAgo: 200);
		Guid b = await SeedRunAsync("download", "completed_with_failures", completedDaysAgo: 150);
		Guid c = await SeedRunAsync("update", "aborted", completedDaysAgo: 100);

		FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
		RunHistoryRolloffHostedService service = CreateService(clock);

		await service.SweepOnceAsync(CancellationToken.None);

		Assert.NotNull(await _deletions.GetTombstoneAsync(a, CancellationToken.None));
		Assert.NotNull(await _deletions.GetTombstoneAsync(b, CancellationToken.None));
		Assert.NotNull(await _deletions.GetTombstoneAsync(c, CancellationToken.None));
	}

	[Fact]
	public async Task FindRolloffCandidatesAsync_RespectsMaxRunsPerSweepLimit()
	{
		for (int i = 0; i < 5; i++)
		{
			await SeedRunAsync("discover", "completed", completedDaysAgo: 200);
		}

		IReadOnlyList<Guid> candidates = await _deletions.FindRolloffCandidatesAsync(
			DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), limit: 3, CancellationToken.None);

		Assert.Equal(3, candidates.Count);
	}

	// -- seeding helpers -------------------------------------------------------

	private async Task<Guid> SeedRunAsync(string runType, string state, int? completedDaysAgo)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		DateTimeOffset? completedAt = completedDaysAgo is { } days
			? DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) - TimeSpan.FromDays(days)
			: null;
		await using NpgsqlCommand q = new(
			"INSERT INTO runs (run_type, scope, state, completed_at) VALUES ($1, '{}', $2, $3) RETURNING id", c);
		q.Parameters.AddWithValue(runType);
		q.Parameters.AddWithValue(state);
		q.Parameters.AddWithValue((object?)completedAt ?? DBNull.Value);
		return (Guid)(await q.ExecuteScalarAsync())!;
	}

	private async Task MarkRunPurgedAsync(Guid runId)
	{
		await using NpgsqlConnection c = new(_fixture.ConnectionString);
		await c.OpenAsync();
		await using NpgsqlCommand q = new("UPDATE runs SET purged_at = now() WHERE id = $1", c);
		q.Parameters.AddWithValue(runId);
		await q.ExecuteNonQueryAsync();
	}
}

/// <summary>Fixed-instant <see cref="TimeProvider"/> so sweep-age tests are deterministic.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
	private readonly DateTimeOffset _now;
	public FakeTimeProvider(DateTimeOffset now) => _now = now;
	public override DateTimeOffset GetUtcNow() => _now;
}
