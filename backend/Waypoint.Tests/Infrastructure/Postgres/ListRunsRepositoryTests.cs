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

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the guarantees issue #210 and GET /api/v1/runs actually depend on but that
/// live entirely inside <see cref="JobQueueRepository.ListRunsAsync"/>'s SQL, which no
/// fake-repository controller test can exercise: newest-first ordering (with a
/// deterministic tiebreaker for same-instant rows), a total count that reflects the
/// full <c>runs</c> table rather than the returned page, correct limit/offset slicing,
/// and the same per-job <c>FILTER</c> aggregation <see cref="JobQueueRepository.GetRunAsync"/>
/// uses, grouped per run.
///
/// Also covers <see cref="JobQueueRepository.GetRunAsync"/> itself (round-2 review
/// finding 2): both methods now share the same <c>RunSummaryProjectionSql</c>
/// constant, so a defect in that shared SQL has a blast radius covering both call
/// sites, and both must be pinned by a real Postgres round trip for the sharing to be
/// safe -- a fake-repository test cannot catch a SQL-assembly bug in either one.
/// </summary>
[Collection("Postgres")]
public sealed class ListRunsRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public ListRunsRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// Backdates a run's <c>created_at</c> to a specific, distinct instant. Runs are
	/// created via <see cref="JobQueueRepository.CreateRunAsync"/> (which always stamps
	/// <c>now()</c>), then this test explicitly rewrites the column the same way
	/// <see cref="RunFanOutPauseAbortTests"/> rewrites job state after the fact --
	/// direct SQL is the only way to get deterministic, well-separated timestamps
	/// instead of depending on wall-clock ordering between INSERT statements.
	/// </summary>
	private async Task SetCreatedAtAsync(Guid runId, DateTimeOffset createdAt)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE runs SET created_at = $1 WHERE id = $2", connection);
		command.Parameters.AddWithValue(createdAt);
		command.Parameters.AddWithValue(runId);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task ListRunsAsync_OrdersNewestFirst_AndTotalCountReflectsFullTableNotThePage()
	{
		DateTimeOffset baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		Guid oldest = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(oldest, baseline);

		Guid middle = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(middle, baseline.AddMinutes(5));

		Guid newest = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(newest, baseline.AddMinutes(10));

		// Full page: newest-first ordering across all three rows.
		RunListResult all = await _repository.ListRunsAsync(limit: 50, offset: 0, CancellationToken.None);
		Assert.Equal(3, all.TotalCount);
		Assert.Equal([newest, middle, oldest], all.Items.Select(r => r.Id).ToArray());

		// A narrow window still reports the full table's total, not the page size --
		// this is the exact guarantee ListRuns_SetsXTotalCountHeader_FromRepositoryTotal_NotPageSize
		// (the controller-level fake test) cannot prove, because the fake's total is
		// whatever the test hands it rather than something the SQL actually computed.
		RunListResult firstPage = await _repository.ListRunsAsync(limit: 1, offset: 0, CancellationToken.None);
		Assert.Equal(3, firstPage.TotalCount);
		Assert.Equal([newest], firstPage.Items.Select(r => r.Id).ToArray());

		// limit/offset slicing: the middle page of a 3-row table with page size 1.
		RunListResult secondPage = await _repository.ListRunsAsync(limit: 1, offset: 1, CancellationToken.None);
		Assert.Equal(3, secondPage.TotalCount);
		Assert.Equal([middle], secondPage.Items.Select(r => r.Id).ToArray());

		RunListResult thirdPage = await _repository.ListRunsAsync(limit: 1, offset: 2, CancellationToken.None);
		Assert.Equal([oldest], thirdPage.Items.Select(r => r.Id).ToArray());

		// Past the end of the table: empty items, but the total still reflects all 3 rows.
		RunListResult pastEnd = await _repository.ListRunsAsync(limit: 1, offset: 3, CancellationToken.None);
		Assert.Empty(pastEnd.Items);
		Assert.Equal(3, pastEnd.TotalCount);
	}

	/// <summary>
	/// docs/api-contract.md's "?limit/offset" convention says nothing about how ties in
	/// the sort key resolve, but an ORDER BY with no tiebreaker is a non-deterministic
	/// sort in Postgres -- two runs created in the same instant could be returned in
	/// either order on different calls, corrupting a paginated walk across them (a row
	/// could be duplicated across two pages or skipped entirely). `ORDER BY
	/// r.created_at DESC, r.id DESC` fixes the order deterministically; this test seeds
	/// two runs at the exact same instant and asserts the id-descending tiebreak wins,
	/// repeated across several calls to rule out one lucky ordering.
	/// </summary>
	[Fact]
	public async Task ListRunsAsync_TiesOnCreatedAt_BreakDeterministicallyByIdDescending()
	{
		DateTimeOffset sameInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		Guid first = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		Guid second = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(first, sameInstant);
		await SetCreatedAtAsync(second, sameInstant);

		Guid[] expected = new[] { first, second }.OrderByDescending(id => id).ToArray();

		for (int attempt = 0; attempt < 5; attempt++)
		{
			RunListResult result = await _repository.ListRunsAsync(limit: 50, offset: 0, CancellationToken.None);
			Assert.Equal(expected, result.Items.Select(r => r.Id).ToArray());
		}
	}

	[Fact]
	public async Task ListRunsAsync_PerJobFilterCounts_MatchMixedJobStates_AndZeroJobRunsReportZero()
	{
		Guid runWithJobs = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runWithJobs,
			[
				new JobSpec("scan", 1, TargetName: "a"),
				new JobSpec("scan", 1, TargetName: "b"),
				new JobSpec("scan", 1, TargetName: "c"),
				new JobSpec("scan", 1, TargetName: "d"),
				new JobSpec("scan", 1, TargetName: "e")
			],
			"tester",
			CancellationToken.None);

		// Mixed states: one stays queued, and the other four are driven to running,
		// done, failed and blocked respectively -- the same states GetRunAsync's
		// FILTER clauses distinguish, now proven through ListRunsAsync's copy of them.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();

			// The 'running' transition alone must stamp a lease -- migration 0002's
			// jobs_running_requires_lease_check (CHECK (state <> 'running' OR
			// lease_expires_at IS NOT NULL), issue #107) rejects a bare state flip.
			// Seeded the same way AuthFailureHaltTests.TransitionToAuthFailedAsync and
			// JobQueueRepositoryClaimTests claim a job directly by id.
			await using NpgsqlCommand runningState = new(
				"""
				UPDATE jobs SET
					state = 'running', claimed_by = 'worker-a', claimed_at = now(),
					lease_expires_at = now() + interval '5 minutes', heartbeat_at = now()
				WHERE id = $1
				""", connection);
			runningState.Parameters.AddWithValue(jobIds[1]);
			await runningState.ExecuteNonQueryAsync();

			// The other three transitions are terminal/blocked states with no lease
			// constraint, so a single CASE update is fine for them.
			await using NpgsqlCommand states = new(
				"""
				UPDATE jobs SET state = CASE id
					WHEN $1 THEN 'done'
					WHEN $2 THEN 'failed'
					WHEN $3 THEN 'blocked'
					ELSE state
				END
				WHERE id IN ($1, $2, $3)
				""", connection);
			states.Parameters.AddWithValue(jobIds[2]);
			states.Parameters.AddWithValue(jobIds[3]);
			states.Parameters.AddWithValue(jobIds[4]);
			await states.ExecuteNonQueryAsync();
		}

		Guid runWithNoJobs = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);

		RunListResult result = await _repository.ListRunsAsync(limit: 50, offset: 0, CancellationToken.None);

		RunSummary withJobs = Assert.Single(result.Items, r => r.Id == runWithJobs);
		Assert.Equal(5, withJobs.JobCount);
		Assert.Equal(1, withJobs.JobCountQueued);
		Assert.Equal(1, withJobs.JobCountRunning);
		Assert.Equal(1, withJobs.JobCountCompleted);
		Assert.Equal(1, withJobs.JobCountFailed);
		Assert.Equal(1, withJobs.JobCountBlocked);

		// A run with zero jobs must report zero counts across the board, not one --
		// the LEFT JOIN's "j.id IS NOT NULL" guard against counting the join's single
		// all-NULL row is what this pins down (the same guard GetRunAsync relies on).
		RunSummary noJobs = Assert.Single(result.Items, r => r.Id == runWithNoJobs);
		Assert.Equal(0, noJobs.JobCount);
		Assert.Equal(0, noJobs.JobCountQueued);
		Assert.Equal(0, noJobs.JobCountRunning);
		Assert.Equal(0, noJobs.JobCountCompleted);
		Assert.Equal(0, noJobs.JobCountFailed);
		Assert.Equal(0, noJobs.JobCountBlocked);
	}

	// -- round-2 review finding 2: GetRunAsync coverage (shares RunSummaryProjectionSql
	// with ListRunsAsync above; a SQL-assembly defect in the shared const has to be
	// pinned at both call sites, not just one) -----------------------------------------

	[Fact]
	public async Task GetRunAsync_ReturnsCorrectFields_AndPerJobFilterCounts_ForMixedJobStates()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", """{"targets":["a"]}""", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[
				new JobSpec("scan", 1, TargetName: "a"),
				new JobSpec("scan", 1, TargetName: "b"),
				new JobSpec("scan", 1, TargetName: "c"),
				new JobSpec("scan", 1, TargetName: "d"),
				new JobSpec("scan", 1, TargetName: "e")
			],
			"tester",
			CancellationToken.None);

		// Same mixed-state seeding idiom as the ListRunsAsync test above: one job stays
		// queued, and the 'running' transition stamps a lease to satisfy migration
		// 0002's jobs_running_requires_lease_check.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();

			await using NpgsqlCommand runningState = new(
				"""
				UPDATE jobs SET
					state = 'running', claimed_by = 'worker-a', claimed_at = now(),
					lease_expires_at = now() + interval '5 minutes', heartbeat_at = now()
				WHERE id = $1
				""", connection);
			runningState.Parameters.AddWithValue(jobIds[1]);
			await runningState.ExecuteNonQueryAsync();

			await using NpgsqlCommand states = new(
				"""
				UPDATE jobs SET state = CASE id
					WHEN $1 THEN 'done'
					WHEN $2 THEN 'failed'
					WHEN $3 THEN 'blocked'
					ELSE state
				END
				WHERE id IN ($1, $2, $3)
				""", connection);
			states.Parameters.AddWithValue(jobIds[2]);
			states.Parameters.AddWithValue(jobIds[3]);
			states.Parameters.AddWithValue(jobIds[4]);
			await states.ExecuteNonQueryAsync();
		}

		RunSummary? summary = await _repository.GetRunAsync(runId, CancellationToken.None);

		Assert.NotNull(summary);
		Assert.Equal(runId, summary!.Id);
		Assert.Equal("scan", summary.RunType);
		Assert.Equal("running", summary.State);
		Assert.False(summary.Paused);
		Assert.False(summary.Blocked);
		Assert.Null(summary.BlockedReason);
		// Parse rather than compare the raw string: jsonb's ::text output normalizes
		// whitespace (e.g. a space after ':') independently of how the value was
		// written, so a literal string comparison would be pinning Postgres's
		// formatting choice rather than the actual round-tripped value.
		using JsonDocument scopeDocument = JsonDocument.Parse(summary.ScopeJson);
		Assert.Equal("a", scopeDocument.RootElement.GetProperty("targets")[0].GetString());
		Assert.Equal(credentialId, summary.CredentialId);
		Assert.Equal("tester", summary.InitiatedBy);
		Assert.NotNull(summary.CreatedAt);
		Assert.Equal(5, summary.JobCount);
		Assert.Equal(1, summary.JobCountQueued);
		Assert.Equal(1, summary.JobCountRunning);
		Assert.Equal(1, summary.JobCountCompleted);
		Assert.Equal(1, summary.JobCountFailed);
		Assert.Equal(1, summary.JobCountBlocked);
	}

	[Fact]
	public async Task GetRunAsync_RunWithNoJobs_ReportsZeroCounts()
	{
		Guid runId = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);

		RunSummary? summary = await _repository.GetRunAsync(runId, CancellationToken.None);

		Assert.NotNull(summary);
		Assert.Equal(0, summary!.JobCount);
		Assert.Equal(0, summary.JobCountQueued);
		Assert.Equal(0, summary.JobCountRunning);
		Assert.Equal(0, summary.JobCountCompleted);
		Assert.Equal(0, summary.JobCountFailed);
		Assert.Equal(0, summary.JobCountBlocked);
	}

	[Fact]
	public async Task GetRunAsync_UnknownRun_ReturnsNull()
	{
		RunSummary? summary = await _repository.GetRunAsync(Guid.NewGuid(), CancellationToken.None);

		Assert.Null(summary);
	}

	/// <summary>Seeds a minimal credential row so a run can carry a real, non-null <c>credential_id</c>.</summary>
	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO credentials (name, credential_type, owner)
			VALUES ('list-runs-test-credential', 'token', 'shared')
			RETURNING id
			""", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	// -- issue #708/#689: ListRunHistoryAsync ---------------------------------------

	private async Task SetStateAsync(Guid runId, string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE runs SET state = $1 WHERE id = $2", connection);
		command.Parameters.AddWithValue(state);
		command.Parameters.AddWithValue(runId);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task ListRunHistoryAsync_NoFilters_OrdersNewestFirst_AndPagesByCursor()
	{
		DateTimeOffset baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		Guid oldest = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(oldest, baseline);
		Guid middle = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(middle, baseline.AddMinutes(5));
		Guid newest = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(newest, baseline.AddMinutes(10));

		RunHistoryPage all = await _repository.ListRunHistoryAsync(new RunHistoryQuery(null, null, null, null, null, null, 50), CancellationToken.None);
		Assert.False(all.HasMore);
		Assert.Equal([newest, middle, oldest], all.Items.Select(r => r.Id).ToArray());

		// Page 1: limit 1 -- must report HasMore and the item must be the newest row.
		RunHistoryPage page1 = await _repository.ListRunHistoryAsync(new RunHistoryQuery(null, null, null, null, null, null, 1), CancellationToken.None);
		Assert.True(page1.HasMore);
		Assert.Equal([newest], page1.Items.Select(r => r.Id).ToArray());

		// Page 2: cursor from page1's last item.
		RunSummary last1 = page1.Items[^1];
		RunHistoryPage page2 = await _repository.ListRunHistoryAsync(
			new RunHistoryQuery(null, null, null, null, DateTimeOffset.Parse(last1.CreatedAt!, System.Globalization.CultureInfo.InvariantCulture), last1.Id, 1), CancellationToken.None);
		Assert.True(page2.HasMore);
		Assert.Equal([middle], page2.Items.Select(r => r.Id).ToArray());

		// Page 3: cursor from page2's last item -- reaches the end (HasMore false).
		RunSummary last2 = page2.Items[^1];
		RunHistoryPage page3 = await _repository.ListRunHistoryAsync(
			new RunHistoryQuery(null, null, null, null, DateTimeOffset.Parse(last2.CreatedAt!, System.Globalization.CultureInfo.InvariantCulture), last2.Id, 1), CancellationToken.None);
		Assert.False(page3.HasMore);
		Assert.Equal([oldest], page3.Items.Select(r => r.Id).ToArray());
	}

	[Fact]
	public async Task ListRunHistoryAsync_StateFilter_NarrowsToMatchingRunsOnly()
	{
		Guid completed = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetStateAsync(completed, "completed");
		Guid aborted = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetStateAsync(aborted, "aborted");
		Guid pending = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		// pending stays at its default state.

		RunHistoryPage result = await _repository.ListRunHistoryAsync(
			new RunHistoryQuery(["completed", "aborted"], null, null, null, null, null, 50), CancellationToken.None);

		Assert.Equal(2, result.Items.Count);
		Assert.Contains(result.Items, r => r.Id == completed);
		Assert.Contains(result.Items, r => r.Id == aborted);
		Assert.DoesNotContain(result.Items, r => r.Id == pending);
	}

	[Fact]
	public async Task ListRunHistoryAsync_RunTypeFilter_NarrowsToMatchingRunsOnly()
	{
		Guid scan = await _repository.CreateRunAsync("scan", "{}", null, "tester", CancellationToken.None);
		Guid download = await _repository.CreateRunAsync("download", "{}", null, "tester", CancellationToken.None);

		RunHistoryPage result = await _repository.ListRunHistoryAsync(
			new RunHistoryQuery(null, ["download"], null, null, null, null, 50), CancellationToken.None);

		Assert.Single(result.Items);
		Assert.Equal(download, result.Items[0].Id);
		Assert.DoesNotContain(result.Items, r => r.Id == scan);
	}

	[Fact]
	public async Task ListRunHistoryAsync_SinceUntilFilter_BoundsCreatedAtInclusively()
	{
		DateTimeOffset baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		Guid tooOld = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(tooOld, baseline);
		Guid inWindow = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(inWindow, baseline.AddDays(5));
		Guid tooNew = await _repository.CreateRunAsync("discover", "{}", null, "tester", CancellationToken.None);
		await SetCreatedAtAsync(tooNew, baseline.AddDays(10));

		RunHistoryPage result = await _repository.ListRunHistoryAsync(
			new RunHistoryQuery(null, null, baseline.AddDays(1), baseline.AddDays(9), null, null, 50), CancellationToken.None);

		Assert.Single(result.Items);
		Assert.Equal(inWindow, result.Items[0].Id);
	}
}
