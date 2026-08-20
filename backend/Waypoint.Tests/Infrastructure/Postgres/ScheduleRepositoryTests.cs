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
using Npgsql;
using Waypoint.Core.Scheduling;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Scheduling;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #31 storage-layer coverage against real Postgres: name uniqueness, full
/// replacement update semantics (null-leaves-unchanged, matching
/// <c>TargetRepository.UpdateAsync</c>'s convention), the credential-clear tri-state,
/// and the due-schedule listing the dispatch sweep reads.
/// </summary>
[Collection("Postgres")]
public sealed class ScheduleRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ScheduleRepository _schedules = null!;

	public ScheduleRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		_schedules = new ScheduleRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task CreateAsync_DuplicateName_ReturnsNull()
	{
		string name = $"dup-{Guid.NewGuid():N}";
		DateTimeOffset next = DateTimeOffset.UtcNow.AddHours(1);

		Guid? first = await _schedules.CreateAsync(name, "scan", "0 2 * * *", "{}", null, next, "alice", CancellationToken.None);
		Guid? second = await _schedules.CreateAsync(name, "scan", "0 2 * * *", "{}", null, next, "bob", CancellationToken.None);

		Assert.NotNull(first);
		Assert.Null(second);
	}

	[Fact]
	public async Task GetAsync_RoundTripsEveryField()
	{
		DateTimeOffset next = DateTimeOffset.UtcNow.AddHours(2);
		Guid id = (await _schedules.CreateAsync(
			$"roundtrip-{Guid.NewGuid():N}", "discover", "0 */4 * * *", """{"site_id":"11111111-1111-1111-1111-111111111111"}""",
			null, next, "alice", CancellationToken.None))!.Value;

		Schedule? schedule = await _schedules.GetAsync(id, CancellationToken.None);

		Assert.NotNull(schedule);
		Assert.Equal("discover", schedule!.JobType);
		Assert.Equal("0 */4 * * *", schedule.CronExpression);
		Assert.Equal("alice", schedule.CreatedBy);
		Assert.True(schedule.Enabled);
		Assert.Null(schedule.PausedReason);
		Assert.Null(schedule.LastRunAt);
		Assert.Null(schedule.LastResult);
	}

	[Fact]
	public async Task UpdateAsync_NullFields_LeaveExistingValuesUnchanged()
	{
		Guid id = (await _schedules.CreateAsync(
			$"partial-update-{Guid.NewGuid():N}", "scan", "0 2 * * *", "{}", null,
			DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None))!.Value;

		ScheduleWriteOutcome outcome = await _schedules.UpdateAsync(
			id, name: null, cronExpression: null, scopeJson: null, credentialId: null, clearCredential: false,
			enabled: false, nextRunAt: null, CancellationToken.None);

		Assert.Equal(ScheduleWriteOutcome.Ok, outcome);
		Schedule updated = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.Equal("scan", updated.JobType);
		Assert.Equal("0 2 * * *", updated.CronExpression);
		Assert.False(updated.Enabled);
	}

	[Fact]
	public async Task UpdateAsync_UnknownId_ReturnsNotFound()
	{
		ScheduleWriteOutcome outcome = await _schedules.UpdateAsync(
			Guid.NewGuid(), null, null, null, null, false, null, null, CancellationToken.None);

		Assert.Equal(ScheduleWriteOutcome.NotFound, outcome);
	}

	[Fact]
	public async Task UpdateAsync_RenameToTakenName_ReturnsNameTaken()
	{
		string takenName = $"taken-{Guid.NewGuid():N}";
		await _schedules.CreateAsync(takenName, "scan", "0 2 * * *", "{}", null, DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None);
		Guid otherId = (await _schedules.CreateAsync(
			$"other-{Guid.NewGuid():N}", "scan", "0 2 * * *", "{}", null, DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None))!.Value;

		ScheduleWriteOutcome outcome = await _schedules.UpdateAsync(
			otherId, takenName, null, null, null, false, null, null, CancellationToken.None);

		Assert.Equal(ScheduleWriteOutcome.NameTaken, outcome);
	}

	[Fact]
	public async Task DeleteAsync_RemovesTheRow()
	{
		Guid id = (await _schedules.CreateAsync(
			$"delete-me-{Guid.NewGuid():N}", "scan", "0 2 * * *", "{}", null,
			DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None))!.Value;

		Assert.True(await _schedules.DeleteAsync(id, CancellationToken.None));
		Assert.Null(await _schedules.GetAsync(id, CancellationToken.None));
		Assert.False(await _schedules.DeleteAsync(id, CancellationToken.None));
	}

	[Fact]
	public async Task ListDueAsync_OnlyReturnsEnabledUnpausedAndDue()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		string marker = Guid.NewGuid().ToString("N");

		Guid dueId = (await _schedules.CreateAsync(
			$"due-{marker}", "scan", "0 2 * * *", "{}", null, now.AddMinutes(-1), "alice", CancellationToken.None))!.Value;
		Guid notYetDueId = (await _schedules.CreateAsync(
			$"future-{marker}", "scan", "0 2 * * *", "{}", null, now.AddHours(1), "alice", CancellationToken.None))!.Value;
		Guid disabledId = (await _schedules.CreateAsync(
			$"disabled-{marker}", "scan", "0 2 * * *", "{}", null, now.AddMinutes(-1), "alice", CancellationToken.None))!.Value;
		await _schedules.UpdateAsync(disabledId, null, null, null, null, false, enabled: false, nextRunAt: null, CancellationToken.None);
		Guid pausedId = (await _schedules.CreateAsync(
			$"paused-{marker}", "catalog-index", "0 2 * * *", "{}", null, now.AddMinutes(-1), "alice", CancellationToken.None))!.Value;
		await _schedules.SetPausedReasonAsync(pausedId, "disconnected_mode", CancellationToken.None);

		IReadOnlyList<Schedule> due = await _schedules.ListDueAsync(now, CancellationToken.None);
		HashSet<Guid> dueIds = due.Select(s => s.Id).ToHashSet();

		Assert.Contains(dueId, dueIds);
		Assert.DoesNotContain(notYetDueId, dueIds);
		Assert.DoesNotContain(disabledId, dueIds);
		Assert.DoesNotContain(pausedId, dueIds);
	}

	/// <summary>
	/// Issue #518: <c>ListDueAsync</c> is a plain read with no row lock held past its own
	/// connection -- a schedule it returns stays "due" for any subsequent call until
	/// something actually advances <c>next_run_at</c> (<see cref="MarkDispatchedAsync"/>).
	/// Pins that no-lingering-lock behavior directly, since the previous <c>FOR UPDATE
	/// SKIP LOCKED</c> clause never actually spanned into dispatch anyway (it was released
	/// the instant this method's connection closed) -- removing it changes nothing
	/// observable, which this test demonstrates.
	/// </summary>
	[Fact]
	public async Task ListDueAsync_CalledAgainBeforeDispatch_StillReturnsTheSameSchedule()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Guid id = (await _schedules.CreateAsync(
			$"redue-{Guid.NewGuid():N}", "scan", "0 2 * * *", "{}", null, now.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		IReadOnlyList<Schedule> firstSweep = await _schedules.ListDueAsync(now, CancellationToken.None);
		IReadOnlyList<Schedule> secondSweep = await _schedules.ListDueAsync(now, CancellationToken.None);

		Assert.Contains(firstSweep, s => s.Id == id);
		Assert.Contains(secondSweep, s => s.Id == id);
	}

	[Fact]
	public async Task MarkDispatchedAsync_AdvancesNextRunAt_AndStampsLastRun()
	{
		Guid runId = await SeedRunAsync();
		Guid id = (await _schedules.CreateAsync(
			$"dispatch-{Guid.NewGuid():N}", "scan", "0 2 * * *", "{}", null,
			DateTimeOffset.UtcNow.AddMinutes(-1), "alice", CancellationToken.None))!.Value;

		DateTimeOffset newNextRun = DateTimeOffset.UtcNow.AddDays(1);
		await _schedules.MarkDispatchedAsync(id, newNextRun, runId, CancellationToken.None);

		Schedule updated = (await _schedules.GetAsync(id, CancellationToken.None))!;
		// Postgres timestamptz is microsecond-precision; .NET DateTimeOffset is
		// tick-precision (100ns) -- round-tripping through the column loses the last
		// sub-microsecond digit, so compare with a tolerance rather than exact equality.
		Assert.True((newNextRun - updated.NextRunAt!.Value).Duration() < TimeSpan.FromMilliseconds(1));
		Assert.Equal(runId, updated.LastRunId);
		Assert.NotNull(updated.LastRunAt);
	}

	/// <summary>Seeds a minimal <c>runs</c> row -- <c>schedules.last_run_id</c> has a real FK, so <see cref="ScheduleRepository.MarkDispatchedAsync"/> needs a row that actually exists.</summary>
	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO runs (run_type, state, scope) VALUES ('scan', 'pending', '{}'::jsonb) RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	[Fact]
	public async Task SetLastResultAsync_RecordsTheOutcome()
	{
		Guid id = (await _schedules.CreateAsync(
			$"result-{Guid.NewGuid():N}", "scan", "0 2 * * *", "{}", null,
			DateTimeOffset.UtcNow.AddHours(1), "alice", CancellationToken.None))!.Value;

		await _schedules.SetLastResultAsync(id, "success", CancellationToken.None);

		Schedule updated = (await _schedules.GetAsync(id, CancellationToken.None))!;
		Assert.Equal("success", updated.LastResult);
	}
}
