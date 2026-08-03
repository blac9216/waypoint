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
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the ADR-0008 queue-claim query — <c>SELECT ... FOR UPDATE SKIP LOCKED</c> —
/// actually behaves correctly under real concurrency: two claimers must never take the
/// same job. A single-threaded test cannot demonstrate this (SKIP LOCKED only matters
/// when transactions genuinely overlap), so every claimer here is a separate
/// <see cref="NpgsqlConnection"/> racing in its own <see cref="Task"/> against a real
/// PostgreSQL 16 container (<see cref="PostgresFixture"/>).
/// </summary>
[Collection("Postgres")]
public sealed class JobsQueueClaimTests : IAsyncLifetime
{
	// Standard Postgres "claim one queued row" idiom: SELECT ... FOR UPDATE SKIP LOCKED
	// inside a CTE, then UPDATE the row it found, RETURNING the id (or nothing, if no
	// claimable row existed). One round trip, one implicit transaction.
	private const string ClaimSql = """
		WITH claimable AS (
			SELECT id FROM jobs
			WHERE run_id = $1 AND state = 'queued'
			ORDER BY priority, created_at
			FOR UPDATE SKIP LOCKED
			LIMIT 1
		)
		UPDATE jobs SET state = 'running', claimed_by = $2, claimed_at = now()
		WHERE id IN (SELECT id FROM claimable)
		RETURNING id
		""";

	private readonly PostgresFixture _fixture;

	public JobsQueueClaimTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
	}

	public Task DisposeAsync()
	{
		return Task.CompletedTask;
	}

	[Fact]
	public async Task ConcurrentClaimers_OneJobEach_NeverClaimTheSameJob()
	{
		const int jobCount = 8;
		Guid runId = await SeedRunWithQueuedJobsAsync(jobCount);

		// Exactly as many claimers as jobs: if SKIP LOCKED were not doing its job (or
		// the claim query raced without it), two claimers could see and update the
		// same row, and this would surface as a duplicate or a missing claim.
		Guid?[] results = await Task.WhenAll(Enumerable.Range(0, jobCount)
			.Select(index => ClaimOneAsync(runId, $"claimer-{index}")));

		Guid[] claimed = results.Where(id => id.HasValue).Select(id => id!.Value).ToArray();

		Assert.Equal(jobCount, claimed.Length);
		Assert.Equal(jobCount, claimed.Distinct().Count());
	}

	[Fact]
	public async Task ConcurrentClaimers_MoreClaimersThanJobs_ExactlyAsManySucceedAsThereAreJobs()
	{
		const int jobCount = 5;
		const int claimerCount = 20;
		Guid runId = await SeedRunWithQueuedJobsAsync(jobCount);

		Guid?[] results = await Task.WhenAll(Enumerable.Range(0, claimerCount)
			.Select(index => ClaimOneAsync(runId, $"claimer-{index}")));

		Guid[] claimed = results.Where(id => id.HasValue).Select(id => id!.Value).ToArray();
		int emptyClaims = results.Count(id => !id.HasValue);

		Assert.Equal(jobCount, claimed.Length);
		Assert.Equal(jobCount, claimed.Distinct().Count());
		Assert.Equal(claimerCount - jobCount, emptyClaims);

		// Every claimed job must actually be 'running' now, and every job that lost
		// the race must still be untouched ('queued') — SKIP LOCKED means "skip", not
		// "silently corrupt".
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand runningCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND state = 'running'", connection);
		runningCount.Parameters.AddWithValue(runId);
		Assert.Equal((long)jobCount, (long)(await runningCount.ExecuteScalarAsync())!);

		await using NpgsqlCommand queuedCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND state = 'queued'", connection);
		queuedCount.Parameters.AddWithValue(runId);
		Assert.Equal(0L, (long)(await queuedCount.ExecuteScalarAsync())!);
	}

	private async Task<Guid> SeedRunWithQueuedJobsAsync(int jobCount)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insertRun = new(
			"INSERT INTO runs (run_type, state) VALUES ('download', 'running') RETURNING id", connection);
		Guid runId = (Guid)(await insertRun.ExecuteScalarAsync())!;

		for (int i = 0; i < jobCount; i++)
		{
			await using NpgsqlCommand insertJob = new(
				"""
				INSERT INTO jobs (run_id, job_type, priority, state, target_name)
				VALUES ($1, 'download', $2, 'queued', $3)
				""", connection);
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue((short)((i % 6) + 1));
			insertJob.Parameters.AddWithValue($"artifact-{i}");
			await insertJob.ExecuteNonQueryAsync();
		}

		return runId;
	}

	private async Task<Guid?> ClaimOneAsync(Guid runId, string claimerName)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(ClaimSql, connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(claimerName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		if (await reader.ReadAsync())
		{
			return reader.GetGuid(0);
		}

		return null;
	}
}
