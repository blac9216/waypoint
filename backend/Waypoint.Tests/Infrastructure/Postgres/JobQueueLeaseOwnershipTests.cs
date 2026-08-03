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
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Direct coverage for the three claim/lease methods that <see cref="ClaimJobAsync"/>'s
/// own tests never touched: <see cref="JobQueueRepository.RenewLeaseAsync"/>,
/// <see cref="JobQueueRepository.AdvanceStateAsync"/> and
/// <see cref="JobQueueRepository.ReleaseClaimAsync"/>.
///
/// PR #126 tested only the claim, and every later caller of these three exercised them
/// incidentally through a dispatcher -- which means a defect in any of them would have
/// surfaced as a confusing dispatcher failure rather than as a failing unit. Each is
/// tested here for its guarantee, not merely its happy path.
///
/// The guarantee they share is **ownership-scoped compare-and-set**. All three filter on
/// <c>claimed_by = $2</c> and on an expected state, so a worker that has lost the row --
/// because its lease was recovered and the job re-claimed by someone else, or because a
/// run abort moved it -- writes nothing and is told so. Without that, a slow worker
/// waking up after recovery would happily stamp its result over a job another dispatcher
/// is now running. Every "negative" test below is therefore also the positive proof that
/// the compare-and-set predicate is load-bearing: each one is accompanied by a control
/// showing the same call succeeds when ownership does hold, so a method that always
/// returned <c>false</c> could not pass this file.
/// </summary>
[Collection("Postgres")]
public sealed class JobQueueLeaseOwnershipTests : IAsyncLifetime
{
	private const string Owner = "worker-owner";
	private const string Interloper = "worker-interloper";

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;

	public JobQueueLeaseOwnershipTests(PostgresFixture fixture)
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

	// ---- RenewLeaseAsync ------------------------------------------------

	/// <summary>
	/// The heartbeat's whole purpose: push <c>lease_expires_at</c> further out so the
	/// recovery sweep does not claw back a job that is merely slow. Asserted by
	/// observing the stored timestamp actually move, not just by the boolean -- a method
	/// that returned true and wrote nothing would satisfy the boolean.
	/// </summary>
	[Fact]
	public async Task RenewLease_ByTheOwner_ExtendsTheStoredExpiry()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromSeconds(30));
		DateTime before = await GetLeaseExpiryAsync(jobId);

		bool renewed = await _repository.RenewLeaseAsync(jobId, Owner, TimeSpan.FromMinutes(10), CancellationToken.None);

		Assert.True(renewed);
		Assert.True(await GetLeaseExpiryAsync(jobId) > before, "RenewLeaseAsync reported success but lease_expires_at did not move.");
	}

	/// <summary>
	/// A worker that no longer owns the job must not be able to extend its lease. This is
	/// the case that matters after a recovery sweep hands the job to someone else: the
	/// old owner is still alive, still heartbeating, and must be told it has lost the row
	/// rather than silently keeping a lease on work another dispatcher now owns.
	/// </summary>
	[Fact]
	public async Task RenewLease_ByANonOwner_FailsAndDoesNotTouchTheRow()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromSeconds(30));
		DateTime before = await GetLeaseExpiryAsync(jobId);

		bool renewed = await _repository.RenewLeaseAsync(jobId, Interloper, TimeSpan.FromMinutes(10), CancellationToken.None);

		Assert.False(renewed);
		Assert.Equal(before, await GetLeaseExpiryAsync(jobId));
		Assert.Equal(Owner, await GetClaimedByAsync(jobId));
	}

	/// <summary>
	/// A lease may only be renewed while the job is in an active state. A terminal job's
	/// lease is cleared, and re-stamping one would resurrect a row the recovery sweep has
	/// no business seeing again.
	/// </summary>
	[Theory]
	[InlineData(JobStates.Done)]
	[InlineData(JobStates.Failed)]
	[InlineData(JobStates.Cancelled)]
	public async Task RenewLease_OnATerminalJob_Fails(string terminalState)
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));
		Assert.True(await _repository.AdvanceStateAsync(
			jobId, Owner, JobStates.Running, terminalState, "done for", clearLease: true, CancellationToken.None));

		Assert.False(await _repository.RenewLeaseAsync(jobId, Owner, TimeSpan.FromMinutes(10), CancellationToken.None));
		Assert.Null(await GetNullableLeaseExpiryAsync(jobId));
	}

	// ---- AdvanceStateAsync ----------------------------------------------

	/// <summary>
	/// A same-tier advance keeps the lease alive (<c>clearLease: false</c>) -- the job is
	/// still executing, so recovery must still not claw it back.
	/// </summary>
	[Fact]
	public async Task AdvanceState_SameTier_KeepsTheLease()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));

		bool advanced = await _repository.AdvanceStateAsync(
			jobId, Owner, JobStates.Running, JobStates.Attesting, "attesting", clearLease: false, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal(JobStates.Attesting, await GetStateAsync(jobId));
		Assert.NotNull(await GetNullableLeaseExpiryAsync(jobId));
	}

	/// <summary>
	/// A terminal advance clears the lease and stamps <c>finished_at</c>. The lease part
	/// is not a nicety: <c>jobs_running_requires_lease_check</c> (#107) constrains only
	/// <c>running</c>, so a terminal row keeping a stale lease would be accepted by the
	/// schema and then swept by recovery forever.
	/// </summary>
	[Fact]
	public async Task AdvanceState_Terminal_ClearsTheLeaseAndStampsFinishedAt()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));

		bool advanced = await _repository.AdvanceStateAsync(
			jobId, Owner, JobStates.Running, JobStates.Done, "ok", clearLease: true, CancellationToken.None);

		Assert.True(advanced);
		Assert.Equal(JobStates.Done, await GetStateAsync(jobId));
		Assert.Null(await GetNullableLeaseExpiryAsync(jobId));
		Assert.NotNull(await GetFinishedAtAsync(jobId));
	}

	/// <summary>
	/// The compare-and-set. A worker whose idea of the current state is stale -- because
	/// something else moved the row while it was working -- must lose the race and write
	/// nothing, rather than overwrite the newer state with its own conclusion.
	/// </summary>
	[Fact]
	public async Task AdvanceState_FromAStaleExpectedState_FailsAndWritesNothing()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));
		Assert.True(await _repository.AdvanceStateAsync(
			jobId, Owner, JobStates.Running, JobStates.Attesting, "attesting", clearLease: false, CancellationToken.None));

		// The worker still believes the job is 'running'.
		bool advanced = await _repository.AdvanceStateAsync(
			jobId, Owner, JobStates.Running, JobStates.Done, "stale conclusion", clearLease: true, CancellationToken.None);

		Assert.False(advanced);
		Assert.Equal(JobStates.Attesting, await GetStateAsync(jobId));
		Assert.NotEqual("stale conclusion", await GetNoteAsync(jobId));
	}

	/// <summary>The ownership half of the same compare-and-set: right state, wrong worker.</summary>
	[Fact]
	public async Task AdvanceState_ByANonOwner_FailsAndWritesNothing()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));

		bool advanced = await _repository.AdvanceStateAsync(
			jobId, Interloper, JobStates.Running, JobStates.Done, "not mine to finish", clearLease: true, CancellationToken.None);

		Assert.False(advanced);
		Assert.Equal(JobStates.Running, await GetStateAsync(jobId));
		Assert.NotEqual("not mine to finish", await GetNoteAsync(jobId));
	}

	// ---- ReleaseClaimAsync ----------------------------------------------

	/// <summary>
	/// Release puts the job back exactly as it was found, so a later dispatch is not
	/// penalised for a claim that was never executed. <c>attempt_count</c> in particular
	/// must come back down -- otherwise a run paused and resumed a few times burns
	/// through <c>max_attempts</c> without a single execution and the job fails having
	/// never run.
	/// </summary>
	[Fact]
	public async Task ReleaseClaim_ReturnsTheJobToTheQueueAndUndoesTheAttempt()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));
		Assert.Equal(1, await GetAttemptCountAsync(jobId));

		bool released = await _repository.ReleaseClaimAsync(jobId, Owner, CancellationToken.None);

		Assert.True(released);
		Assert.Equal(JobStates.Queued, await GetStateAsync(jobId));
		Assert.Null(await GetClaimedByAsync(jobId));
		Assert.Null(await GetNullableLeaseExpiryAsync(jobId));
		Assert.Equal(0, await GetAttemptCountAsync(jobId));
	}

	/// <summary>A released job is genuinely claimable again -- asserted by claiming it, not by reading its state.</summary>
	[Fact]
	public async Task ReleaseClaim_MakesTheJobClaimableAgain()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));
		Assert.True(await _repository.ReleaseClaimAsync(jobId, Owner, CancellationToken.None));

		ClaimedJob? reclaimed = await _repository.ClaimJobAsync("worker-next", TimeSpan.FromMinutes(5), CancellationToken.None);

		Assert.NotNull(reclaimed);
		Assert.Equal(jobId, reclaimed.Id);
	}

	/// <summary>A worker that does not own the job cannot push it back onto the queue under another dispatcher.</summary>
	[Fact]
	public async Task ReleaseClaim_ByANonOwner_FailsAndLeavesTheJobRunning()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));

		bool released = await _repository.ReleaseClaimAsync(jobId, Interloper, CancellationToken.None);

		Assert.False(released);
		Assert.Equal(JobStates.Running, await GetStateAsync(jobId));
		Assert.Equal(Owner, await GetClaimedByAsync(jobId));
	}

	/// <summary>
	/// Release only applies to a job still <c>running</c>. Releasing one that has already
	/// reached a terminal state would resurrect finished work -- and, for a terminal
	/// state, would put a row back on the queue that a caller already has a result for.
	/// </summary>
	[Fact]
	public async Task ReleaseClaim_OnAnAlreadyCompletedJob_Fails()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));
		Assert.True(await _repository.AdvanceStateAsync(
			jobId, Owner, JobStates.Running, JobStates.Done, "ok", clearLease: true, CancellationToken.None));

		Assert.False(await _repository.ReleaseClaimAsync(jobId, Owner, CancellationToken.None));
		Assert.Equal(JobStates.Done, await GetStateAsync(jobId));
	}

	// ---- helpers ---------------------------------------------------------

	private async Task<Guid> SeedAndClaimAsync(TimeSpan leaseDuration)
	{
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand insert = new(
				"INSERT INTO jobs (job_type, priority, state) VALUES ('download', 1, 'queued')", connection);
			await insert.ExecuteNonQueryAsync();
		}

		ClaimedJob? claimed = await _repository.ClaimJobAsync(Owner, leaseDuration, CancellationToken.None);
		Assert.NotNull(claimed);
		return claimed.Id;
	}

	private async Task<T> ScalarAsync<T>(Guid jobId, string column)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new($"SELECT {column} FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);

		object? value = await command.ExecuteScalarAsync();
		return value is null or DBNull ? default! : (T)value;
	}

	private Task<string> GetStateAsync(Guid jobId) => ScalarAsync<string>(jobId, "state");

	private Task<string?> GetNoteAsync(Guid jobId) => ScalarAsync<string?>(jobId, "note");

	private Task<string?> GetClaimedByAsync(Guid jobId) => ScalarAsync<string?>(jobId, "claimed_by");

	private Task<int> GetAttemptCountAsync(Guid jobId) => ScalarAsync<int>(jobId, "attempt_count");

	private Task<DateTime> GetLeaseExpiryAsync(Guid jobId) => ScalarAsync<DateTime>(jobId, "lease_expires_at");

	private async Task<DateTime?> GetNullableLeaseExpiryAsync(Guid jobId)
	{
		DateTime value = await ScalarAsync<DateTime>(jobId, "lease_expires_at");
		return value == default ? null : value;
	}

	private async Task<DateTime?> GetFinishedAtAsync(Guid jobId)
	{
		DateTime value = await ScalarAsync<DateTime>(jobId, "finished_at");
		return value == default ? null : value;
	}
}
