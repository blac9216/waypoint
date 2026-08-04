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
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Exercises <see cref="JobExecutionContext.AdvanceAsync"/> -- the seam a future
/// <see cref="JobShape.Standard"/> handler (the <c>scan</c> job type, not implemented by
/// this issue) uses to report intermediate pipeline stages. No handler calls it yet, so
/// this is what proves it against a real claimed row rather than leaving it dead code.
/// </summary>
[Collection("Postgres")]
public sealed class JobExecutionContextTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;

	public JobExecutionContextTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task AdvanceAsync_LegalTransition_UpdatesTheRowAndEmitsAnEvent_KeepingTheLeaseAlive()
	{
		ClaimedJob job = await ClaimAScanJobAsync();
		JobExecutionContext context = new(job, "worker-a", _events, _repository, JobShape.Standard);

		await context.AdvanceAsync(JobStates.Attesting, "starting attestation", CancellationToken.None);

		Assert.Equal(JobStates.Attesting, context.CurrentState);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand jobRow = new(
			"SELECT state, note, lease_expires_at IS NOT NULL FROM jobs WHERE id = $1", connection))
		{
			jobRow.Parameters.AddWithValue(job.Id);
			await using NpgsqlDataReader reader = await jobRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.Equal("attesting", reader.GetString(0));
			Assert.Equal("starting attestation", reader.GetString(1));
			Assert.True(reader.GetBoolean(2), "the lease must survive a same-tier transition.");
		}

		await using NpgsqlCommand eventCount = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.state'", connection);
		eventCount.Parameters.AddWithValue(job.Id);
		Assert.Equal(1L, (long)(await eventCount.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task AdvanceAsync_IllegalTransition_ThrowsAndTouchesNothing()
	{
		ClaimedJob job = await ClaimAScanJobAsync();
		JobExecutionContext context = new(job, "worker-a", _events, _repository, JobShape.Standard);

		// running -> uploaded is not a legal Standard-shape transition (must pass through attesting/converting first).
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => context.AdvanceAsync(JobStates.Uploaded, null, CancellationToken.None));

		Assert.Equal(JobStates.Running, context.CurrentState);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection);
		jobState.Parameters.AddWithValue(job.Id);
		Assert.Equal("running", (string)(await jobState.ExecuteScalarAsync())!);
	}

	/// <summary>A run aborted out from under a Standard-shape handler mid-pipeline surfaces as a clear exception, not a silent no-op.</summary>
	[Fact]
	public async Task AdvanceAsync_JobNoLongerOwnedByThisWorker_Throws()
	{
		ClaimedJob job = await ClaimAScanJobAsync();
		JobExecutionContext context = new(job, "worker-a", _events, _repository, JobShape.Standard);

		// Simulate the row moving out from under this worker -- e.g. an abort.
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			bool stolen = await _repository.AdvanceStateAsync(
				job.Id, "worker-a", JobStates.Running, JobStates.Cancelled, "aborted", clearLease: true, CancellationToken.None);
			Assert.True(stolen);

			await context.AdvanceAsync(JobStates.Attesting, null, CancellationToken.None);
		});
	}

	private async Task<ClaimedJob> ClaimAScanJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('scan', 1, 'queued') RETURNING id", connection);
		await insert.ExecuteScalarAsync();

		ClaimedJob? claimed = await _repository.ClaimJobAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
		Assert.NotNull(claimed);
		return claimed!;
	}
}
