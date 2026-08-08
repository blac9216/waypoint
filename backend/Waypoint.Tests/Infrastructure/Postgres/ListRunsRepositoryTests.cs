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
/// and the same per-job <c>FILTER</c> aggregation <see cref="GetRunAsync"/> uses,
/// grouped per run.
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
			await using NpgsqlCommand states = new(
				"""
				UPDATE jobs SET state = CASE id
					WHEN $1 THEN 'running'
					WHEN $2 THEN 'done'
					WHEN $3 THEN 'failed'
					WHEN $4 THEN 'blocked'
					ELSE state
				END
				WHERE id IN ($1, $2, $3, $4)
				""", connection);
			states.Parameters.AddWithValue(jobIds[1]);
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
}
