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

using System.Reflection;
using System.Text.RegularExpressions;
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
///
/// **Round 1 review found a fixture monoculture in this very file** (docs/testing.md,
/// "Fixture monoculture" -- the fifth instance in this repo, and the most embarrassing
/// one, since the file exists specifically so these three methods stop being untested).
/// Every fixture above renews while <c>running</c>, so narrowing
/// <see cref="JobQueueRepository.RenewLeaseAsync"/>'s guard from
/// <c>state IN ('running', 'attesting', 'converting')</c> to <c>state = 'running'</c>
/// passed all 201 tests. The properties every fixture here shared, and what was done
/// about each, are in the "monoculture sweep" section at the bottom of this file.
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

	// ---- monoculture sweep ------------------------------------------------
	//
	// What every fixture above has in common, asked deliberately per docs/testing.md
	// rather than discovered by a reviewer a second time:
	//
	//   1. state at renew is always 'running'          -> LOAD-BEARING. Closed below by
	//      RenewLease_IsAllowedExactlyInTheStatesAWorkerCanBeExecutingIn.
	//   2. the lease is always still fresh             -> LOAD-BEARING. Closed below by
	//      RenewLease_OnAnExpiredButStillOwnedLease_StillSucceeds.
	//   3. release is always from 'running'            -> LOAD-BEARING, in the opposite
	//      direction: the release guard is deliberately NARROWER than the renew guard,
	//      and nothing pinned that asymmetry. Closed below.
	//   4. attempt_count is always 0 before the claim  -> LOAD-BEARING, but it belongs to
	//      the claim rather than the lease: see
	//      JobQueueRepositoryClaimTests.ClaimJobAsync_ClaimsAJobThatAlreadyFailedAnAttempt.
	//   5. one owner identity ("worker-owner") and one interloper -> judged NOT
	//      load-bearing, and this is a judgement rather than an omission. claimed_by is
	//      TEXT and every guard compares it with plain '=', so the only drift a second
	//      identity shape could catch is a prefix/pattern match (LIKE) or a padded CHAR
	//      column; neither is reachable without changing the column type or the operator,
	//      both of which are visible one line away in the same statement the ownership
	//      tests already drive. Adding fixtures here would raise the case count without
	//      raising what the file can falsify.
	//   6. every fixture claims through ClaimJobAsync -> deliberate, and kept: it is what
	//      makes these tests exercise the real claim path rather than a hand-seeded row.
	//      The matrix below forces the state afterwards precisely so state is the ONLY
	//      thing that varies across its cases.

	/// <summary>
	/// The states a worker may heartbeat in, derived rather than hand-listed. A worker
	/// holds the lease from its claim (<c>running</c>) until the job reaches a state with
	/// nowhere left to go, so "a state a worker can be executing in" is exactly: reachable
	/// from <c>running</c> in either shape, and still has an outgoing transition. Today
	/// that resolves to running/attesting/converting, but a pipeline stage added to
	/// <see cref="JobStateMachine"/> later joins this set on its own -- which is the point.
	/// A hand-written list is a monoculture with extra steps (docs/testing.md).
	/// </summary>
	private static readonly HashSet<string> ExecutingStates = DeriveExecutingStates();

	/// <summary>
	/// The full <c>jobs.state</c> vocabulary, read off <see cref="JobStates"/>'s own
	/// constants by reflection so a state added there joins this matrix without anyone
	/// remembering to add it. <see cref="TheStateAxisIsTheWholeSchemaVocabulary"/> pins
	/// that list against the database's <c>jobs_state_check</c>, so it cannot drift from
	/// the schema either.
	/// </summary>
	public static TheoryData<string> EveryJobState()
	{
		TheoryData<string> data = [];
		foreach (string state in StateVocabulary)
		{
			data.Add(state);
		}

		return data;
	}

	/// <summary>
	/// The finding this file was blocked on. Renewal must succeed in every state a worker
	/// can be mid-execution in and fail in every other, and both halves are asserted from
	/// one axis so neither can be quietly dropped.
	///
	/// Ownership is held constant at the owner and the state is forced directly, so state
	/// is the only variable -- a case that fails here fails because of the state filter and
	/// nothing else. Narrowing that filter to <c>state = 'running'</c> fails exactly the
	/// <c>attesting</c> and <c>converting</c> cases (2 of 12 in this Theory).
	///
	/// Why it matters past the count: a <c>Standard</c>-shape job spends <c>attesting</c>
	/// and <c>converting</c> doing the slowest work in the pipeline (SAF attest,
	/// <c>hdf2ckl</c>). A worker that cannot heartbeat there loses its lease while
	/// perfectly healthy, and #129's recovery sweep claws the job back and re-dispatches
	/// it -- a duplicate execution. #124 records that <c>attesting</c>/<c>converting</c>
	/// carry no lease-tied CHECK, so there is no schema backstop underneath this test.
	/// </summary>
	[Theory]
	[MemberData(nameof(EveryJobState))]
	public async Task RenewLease_IsAllowedExactlyInTheStatesAWorkerCanBeExecutingIn(string state)
	{
		bool shouldRenew = ExecutingStates.Contains(state);

		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromSeconds(30));
		await ForceStateAsync(jobId, state);
		DateTime before = await GetLeaseExpiryAsync(jobId);

		bool renewed = await _repository.RenewLeaseAsync(jobId, Owner, TimeSpan.FromMinutes(10), CancellationToken.None);

		Assert.True(
			renewed == shouldRenew,
			$"RenewLeaseAsync returned {renewed} for a job owned by its claimant in state '{state}'; expected {shouldRenew}. " +
			$"States a worker can be executing in, derived from JobStateMachine: {string.Join(", ", ExecutingStates.Order(StringComparer.Ordinal))}.");

		DateTime after = await GetLeaseExpiryAsync(jobId);
		if (shouldRenew)
		{
			Assert.True(after > before, $"RenewLeaseAsync reported success in state '{state}' but lease_expires_at did not move.");
		}
		else
		{
			Assert.Equal(before, after);
		}
	}

	/// <summary>
	/// The axis above is only as good as its coverage of the real vocabulary, so it is
	/// pinned against the database rather than trusted. <see cref="JobStates"/> claims in
	/// its own doc comment to match <c>jobs_state_check</c> verbatim; this reads the
	/// constraint back out of Postgres and holds it to that. A state added to the schema
	/// and not to <see cref="JobStates"/> (or the reverse) fails here, and the renewal
	/// matrix therefore cannot silently stop covering a state that exists.
	/// </summary>
	[Fact]
	public async Task TheStateAxisIsTheWholeSchemaVocabulary()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'jobs_state_check'", connection);
		string? definition = (string?)await command.ExecuteScalarAsync();
		Assert.False(string.IsNullOrWhiteSpace(definition), "jobs_state_check is missing from the database.");

		string[] schemaStates = Regex.Matches(definition!, "'([^']+)'::text")
			.Select(match => match.Groups[1].Value)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(schemaStates, StateVocabulary);

		// And the derived subset must be a real subset of it -- a derivation that produced
		// a state the schema has never heard of would make every negative case vacuous.
		Assert.All(ExecutingStates, state => Assert.Contains(state, schemaStates));
		Assert.NotEmpty(ExecutingStates);
	}

	/// <summary>
	/// Every other fixture in this file renews a lease that has not expired yet. That is a
	/// shared property, and it turns out to be load-bearing: the guard filters on ownership
	/// and state, deliberately <em>not</em> on <c>lease_expires_at &gt; now()</c>, so a
	/// worker that stalled just past its lease can still heartbeat its way back.
	///
	/// That is the correct behaviour and it is worth pinning, because tightening it looks
	/// like a safety improvement. It is the opposite: #129's recovery sweep is what decides
	/// a lease is forfeit, and until the sweep actually moves the row, the claimant is
	/// still the claimant. If the sweep has taken it, <c>claimed_by</c>/<c>state</c> have
	/// already changed and the ownership arm rejects the renewal -- which is the
	/// <c>ByANonOwner</c> test above. Adding a lease-freshness arm here would make a
	/// momentarily slow worker permanently unable to heartbeat, which is the same
	/// duplicate-execution failure as the state finding, reached from the other side.
	/// </summary>
	[Fact]
	public async Task RenewLease_OnAnExpiredButStillOwnedLease_StillSucceeds()
	{
		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromSeconds(30));

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand expire = new(
				"UPDATE jobs SET lease_expires_at = now() - interval '1 hour' WHERE id = $1", connection);
			expire.Parameters.AddWithValue(jobId);
			Assert.Equal(1, await expire.ExecuteNonQueryAsync());
		}

		DateTime before = await GetLeaseExpiryAsync(jobId);
		Assert.True(before < DateTime.UtcNow, "The fixture failed to expire the lease, so this test would prove nothing.");

		bool renewed = await _repository.RenewLeaseAsync(jobId, Owner, TimeSpan.FromMinutes(10), CancellationToken.None);

		Assert.True(renewed, "An owner whose lease has expired but whose row no sweep has taken must still be able to heartbeat.");
		Assert.True(await GetLeaseExpiryAsync(jobId) > DateTime.UtcNow, "The renewed lease is still in the past.");
	}

	/// <summary>
	/// The mirror of the renewal matrix, and the reason it is worth having both: the two
	/// guards look alike and are deliberately different widths. Renewal accepts every
	/// executing state; release accepts <c>running</c> only, because release means "this
	/// claim never executed, put it back untouched" -- and a job that has reached
	/// <c>attesting</c> demonstrably did execute. Requeueing it would re-run completed work
	/// and, via the <c>attempt_count</c> decrement, hide that it had.
	///
	/// Without this test, widening release's filter to match renewal's (an obvious-looking
	/// consistency tidy-up, made likelier by the renewal matrix now sitting a few lines
	/// above it) breaks nothing.
	/// </summary>
	[Theory]
	[InlineData(JobStates.Attesting)]
	[InlineData(JobStates.Converting)]
	public async Task ReleaseClaim_FromAnExecutingStateOtherThanRunning_Fails(string state)
	{
		Assert.Contains(state, ExecutingStates);

		Guid jobId = await SeedAndClaimAsync(TimeSpan.FromMinutes(5));
		Assert.True(await _repository.RenewLeaseAsync(jobId, Owner, TimeSpan.FromMinutes(5), CancellationToken.None));
		await ForceStateAsync(jobId, state);

		bool released = await _repository.ReleaseClaimAsync(jobId, Owner, CancellationToken.None);

		Assert.False(released, $"ReleaseClaimAsync requeued a job in '{state}', discarding work that had already run.");
		Assert.Equal(state, await GetStateAsync(jobId));
		Assert.Equal(Owner, await GetClaimedByAsync(jobId));
		Assert.Equal(1, await GetAttemptCountAsync(jobId));
	}

	// ---- helpers ---------------------------------------------------------

	/// <summary>
	/// The <c>jobs.state</c> vocabulary, by reflection over <see cref="JobStates"/>'s
	/// public string constants -- not a hand-written copy of them.
	/// </summary>
	private static readonly IReadOnlyList<string> StateVocabulary = typeof(JobStates)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
		.Select(field => (string)field.GetRawConstantValue()!)
		.Order(StringComparer.Ordinal)
		.ToArray();

	private static HashSet<string> DeriveExecutingStates()
	{
		JobShape[] shapes = Enum.GetValues<JobShape>();

		// Everything a job can reach once it has been claimed.
		HashSet<string> reachableFromAClaim = new(StringComparer.Ordinal) { JobStates.Running };
		Queue<string> pending = new();
		pending.Enqueue(JobStates.Running);

		while (pending.Count > 0)
		{
			string state = pending.Dequeue();
			foreach (JobShape shape in shapes)
			{
				foreach (string next in JobStateMachine.AllowedNextStates(shape, state))
				{
					if (reachableFromAClaim.Add(next))
					{
						pending.Enqueue(next);
					}
				}
			}
		}

		// ...minus the terminal ones. A state with no outgoing transition in any shape is
		// somewhere a worker has finished, not somewhere it is working.
		return reachableFromAClaim
			.Where(state => shapes.Any(shape => JobStateMachine.AllowedNextStates(shape, state).Count > 0))
			.ToHashSet(StringComparer.Ordinal);
	}

	private async Task ForceStateAsync(Guid jobId, string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Deliberately bypasses AdvanceStateAsync: this must put the row in the given state
		// whether or not any legal transition reaches it, so that the guard under test is
		// the only thing deciding the outcome. The lease is left alone, so
		// jobs_running_requires_lease_check (#107) stays satisfied.
		await using NpgsqlCommand update = new("UPDATE jobs SET state = $2 WHERE id = $1", connection);
		update.Parameters.AddWithValue(jobId);
		update.Parameters.AddWithValue(state);
		Assert.Equal(1, await update.ExecuteNonQueryAsync());
	}

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
