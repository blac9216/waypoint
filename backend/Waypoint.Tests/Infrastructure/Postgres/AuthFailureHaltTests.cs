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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves the ADR-0008 consecutive-auth-failure queue halt (default threshold 3): the
/// Nth-in-a-row <c>auth-failed</c> job against one credential blocks that credential's
/// still-queued jobs (and every run that had one), while a run of failures broken up by
/// a success never trips it -- the boundary that shows "consecutive" is actually
/// enforced, not just "N total".
/// </summary>
[Collection("Postgres")]
public sealed class AuthFailureHaltTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private CapturingLogger<JobQueueRepository> _logger = null!;

	public AuthFailureHaltTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		_logger = new CapturingLogger<JobQueueRepository>();
		_repository = new JobQueueRepository(_fixture.ConnectionString, _logger);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task ThreeConsecutiveAuthFailures_BlocksTheRunAndItsQueuedJobs()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);

		// stillQueuedJobId is seeded oldest, on purpose: "3 most recent" (created_at
		// DESC) must land on exactly the two auth-failed history rows plus the job about
		// to become the third -- if the still-queued row fell inside that recency
		// window instead, it would break the "consecutive" condition itself and the test
		// would pass for the wrong reason (a queued job isn't 'auth-failed' either).
		Guid stillQueuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		Guid thirdFailureJobId = await SeedQueuedJobAsync(runId, credentialId);

		await TransitionToAuthFailedAsync(thirdFailureJobId);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);

		Assert.Contains(runId, result.BlockedRunIds);
		Assert.Contains(stillQueuedJobId, result.BlockedJobIds);
		Assert.Contains(_logger.EntriesAt(LogLevel.Error), entry => entry.Message.Contains("consecutive auth failures", StringComparison.Ordinal));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand runBlocked = new("SELECT blocked, blocked_reason FROM runs WHERE id = $1", connection))
		{
			runBlocked.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await runBlocked.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.True(reader.GetBoolean(0));
			Assert.False(reader.IsDBNull(1));
		}

		await using NpgsqlCommand jobBlocked = new("SELECT state FROM jobs WHERE id = $1", connection);
		jobBlocked.Parameters.AddWithValue(stillQueuedJobId);
		Assert.Equal("blocked", (string)(await jobBlocked.ExecuteScalarAsync())!);
	}

	/// <summary>The boundary that proves this can fail: two failures, one success, then
	/// a third failure is NOT three *consecutive* failures -- the halt must not trip.</summary>
	[Fact]
	public async Task FailuresInterruptedBySuccess_DoNotTripTheHalt()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);

		// Same "seed the queued job oldest" reasoning as the trip-the-halt test above --
		// here the interrupting 'done' success is what should break the "recent 3" run,
		// not an incidentally-included queued row.
		Guid queuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.Done);
		Guid thirdJobId = await SeedQueuedJobAsync(runId, credentialId);

		await TransitionToAuthFailedAsync(thirdJobId);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);

		Assert.Empty(result.BlockedRunIds);
		Assert.Empty(result.BlockedJobIds);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection);
		jobState.Parameters.AddWithValue(queuedJobId);
		Assert.Equal("queued", (string)(await jobState.ExecuteScalarAsync())!);
	}

	/// <summary>Fewer than the threshold's worth of history for a credential can never trip the halt, no matter their state.</summary>
	[Fact]
	public async Task FewerJobsThanThreshold_NeverTrips()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Empty(result.BlockedRunIds);
		Assert.Empty(result.BlockedJobIds);
	}

	/// <summary>A second, redundant call after the halt already tripped finds nothing left to block -- idempotent under a duplicate/concurrent caller.</summary>
	[Fact]
	public async Task CallingTwice_TheSecondCallIsANoOp()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid queuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		Guid thirdJobId = await SeedQueuedJobAsync(runId, credentialId);
		await TransitionToAuthFailedAsync(thirdJobId);

		AuthFailureHaltResult first = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Contains(queuedJobId, first.BlockedJobIds);

		AuthFailureHaltResult second = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Empty(second.BlockedRunIds);
		Assert.Empty(second.BlockedJobIds);
	}

	/// <summary>Issue #175: when the halt re-trips on a credential that is already halted,
	/// queue_halted_at must retain the original trip time — it must not drift to the
	/// latest re-check.</summary>
	[Fact]
	public async Task ReTrippingAnAlreadyHaltedCredential_PreservesOriginalHaltTimestamp()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid queuedJobId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		Guid thirdJobId = await SeedQueuedJobAsync(runId, credentialId);
		await TransitionToAuthFailedAsync(thirdJobId);

		// First call — trips the halt
		await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);

		// Capture the original halt timestamp
		DateTime firstHaltTimestamp = await GetQueueHaltedAtAsync(credentialId);

		// Wait briefly so a second call would produce a different timestamp if it overwrote
		await Task.Delay(10);

		// Second call — re-trips (the window still holds 3 auth-failed outcomes)
		await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);

		// The timestamp must be the same as the original — COALESCE(queue_halted_at, now())
		// preserves the first trip's time
		DateTime secondHaltTimestamp = await GetQueueHaltedAtAsync(credentialId);
		Assert.Equal(firstHaltTimestamp, secondHaltTimestamp);
	}

	[Fact]
	public async Task NewerQueuedJobsFromASecondRun_DoNotSuppressResolvedFailureStreak()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid firstRunId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid firstQueuedId = await SeedQueuedJobAsync(firstRunId, credentialId);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);

		Guid secondRunId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> newerQueuedIds = await _repository.FanOutJobsAsync(
			secondRunId,
			[new JobSpec("scan", 1, CredentialId: credentialId), new JobSpec("scan", 1, CredentialId: credentialId), new JobSpec("scan", 1, CredentialId: credentialId)],
			"tester",
			CancellationToken.None);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None);

		Assert.Contains(firstRunId, result.BlockedRunIds);
		Assert.Contains(secondRunId, result.BlockedRunIds);
		Assert.Contains(firstQueuedId, result.BlockedJobIds);
		Assert.All(newerQueuedIds, id => Assert.Contains(id, result.BlockedJobIds));
	}

	[Fact]
	public async Task EqualFinishedTimes_UseIdAsARepeatableTotalOrder()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		DateTime finishedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

		await SeedTerminalJobWithIdAsync(Guid.Parse("00000000-0000-0000-0000-000000000001"), runId, credentialId, JobStates.Done, finishedAt);
		await SeedTerminalJobWithIdAsync(Guid.Parse("00000000-0000-0000-0000-000000000002"), runId, credentialId, JobStates.AuthFailed, finishedAt);
		await SeedTerminalJobWithIdAsync(Guid.Parse("00000000-0000-0000-0000-000000000003"), runId, credentialId, JobStates.AuthFailed, finishedAt);
		await SeedTerminalJobWithIdAsync(Guid.Parse("00000000-0000-0000-0000-000000000004"), runId, credentialId, JobStates.AuthFailed, finishedAt);

		for (int iteration = 0; iteration < 8; iteration++)
		{
			// The first iteration durably halts the credential, which would make every
			// later seed 'blocked' at insert (migration 0005) before the window is even
			// consulted. Clear the halt each pass so each iteration re-proves that the
			// resolved-outcome window itself picks the same three rows.
			await ClearQueueHaltAsync(credentialId);
			Guid queuedId = await SeedQueuedJobAsync(runId, credentialId);
			AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None);
			Assert.Contains(queuedId, result.BlockedJobIds);
		}
	}

	[Fact]
	public async Task ConcurrentCallers_BlockEachQueuedJobExactlyOnce()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid queuedId = await SeedQueuedJobAsync(runId, credentialId);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		JobQueueRepository secondRepository = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		AuthFailureHaltResult[] results = await Task.WhenAll(
			_repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None),
			secondRepository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None));

		Assert.Equal(1, results.Sum(result => result.BlockedJobIds.Count(id => id == queuedId)));
		Assert.Equal(1, results.Sum(result => result.BlockedRunIds.Count(id => id == runId)));
	}

	[Fact]
	public async Task InvalidThresholdAndMissingCredential_DoNotInspectOutcomes()
	{
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => _repository.CheckConsecutiveAuthFailuresAsync(Guid.NewGuid(), 0, CancellationToken.None));
		AuthFailureHaltResult missing = await _repository.CheckConsecutiveAuthFailuresAsync(Guid.NewGuid(), 3, CancellationToken.None);
		Assert.Empty(missing.BlockedRunIds);
		Assert.Empty(missing.BlockedJobIds);
	}

	[Fact]
	public async Task StandaloneQueuedJob_IsBlockedWithoutInventingARun()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, null, CancellationToken.None);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new("INSERT INTO jobs (job_type, priority, state, credential_id) VALUES ('scan', 1, 'queued', $1) RETURNING id", connection);
		insert.Parameters.AddWithValue(credentialId);
		Guid standaloneId = (Guid)(await insert.ExecuteScalarAsync())!;

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None);
		Assert.Empty(result.BlockedRunIds);
		Assert.Contains(standaloneId, result.BlockedJobIds);
	}

	/// <summary>PR #144 round 1, finding 1: the halt must be a durable credential state,
	/// not a point-in-time sweep -- a run fanned out *after* the halt must not create
	/// claimable work for the halted credential.</summary>
	[Fact]
	public async Task FanOutAfterTheHalt_CreatesBlockedJobsAndABlockedRun()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid firstRunId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		AuthFailureHaltResult halt = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None);
		Assert.True(halt.HaltTripped); // #147: tripping is visible even with nothing to block
		Assert.True(halt.BlockedRunIds.Count == 0 && halt.BlockedJobIds.Count == 0); // nothing was queued -- the halt is the credential state itself

		Guid laterRunId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			laterRunId,
			[new JobSpec("scan", 1, CredentialId: credentialId), new JobSpec("scan", 1, CredentialId: credentialId)],
			"tester",
			CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		foreach (Guid jobId in jobIds)
		{
			await using NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = $1", connection);
			jobState.Parameters.AddWithValue(jobId);
			Assert.Equal("blocked", (string)(await jobState.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand runBlocked = new("SELECT blocked, blocked_reason FROM runs WHERE id = $1", connection))
		{
			runBlocked.Parameters.AddWithValue(laterRunId);
			await using NpgsqlDataReader reader = await runBlocked.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.True(reader.GetBoolean(0));
			Assert.Contains("consecutive auth failures", reader.GetString(1), StringComparison.Ordinal);
		}

		// The claim predicate only sees 'queued': nothing from this fan-out is claimable.
		Assert.Null(await _repository.ClaimJobAsync("worker-post-halt", TimeSpan.FromMinutes(5), CancellationToken.None));
	}

	/// <summary>The credential row records the halt durably, with the why and when the CHECK demands.</summary>
	[Fact]
	public async Task TrippedHalt_PersistsQueueHaltedOnTheCredential()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand halted = new(
			"SELECT queue_halted, queue_halted_reason, queue_halted_at FROM credentials WHERE id = $1", connection);
		halted.Parameters.AddWithValue(credentialId);
		await using NpgsqlDataReader reader = await halted.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.True(reader.GetBoolean(0));
		Assert.Contains("consecutive auth failures", reader.GetString(1), StringComparison.Ordinal);
		Assert.False(reader.IsDBNull(2));
	}

	/// <summary>PR #144 round 1, finding 1 (interleaving): a fan-out that starts while the
	/// halt transaction holds the credential's FOR UPDATE lock must wait on the trigger's
	/// FOR SHARE and then observe the committed halt -- not slip queued rows past it.</summary>
	[Fact]
	public async Task FanOutInterleavedWithTheHaltCommit_StillCreatesBlockedJobs()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);

		await using NpgsqlConnection haltConnection = new(_fixture.ConnectionString);
		await haltConnection.OpenAsync();
		await using NpgsqlTransaction haltTransaction = await haltConnection.BeginTransactionAsync();
		await using (NpgsqlCommand lockAndHalt = new(
			"UPDATE credentials SET queue_halted = true, queue_halted_reason = 'halted mid-fan-out', queue_halted_at = now() WHERE id = $1",
			haltConnection, haltTransaction))
		{
			lockAndHalt.Parameters.AddWithValue(credentialId);
			await lockAndHalt.ExecuteNonQueryAsync();
		}

		Task<IReadOnlyList<Guid>> fanOut = _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 1, CredentialId: credentialId)], "tester", CancellationToken.None);

		// The insert trigger's FOR SHARE must be waiting on the uncommitted halt's row lock.
		Task completedFirst = await Task.WhenAny(fanOut, Task.Delay(TimeSpan.FromMilliseconds(500)));
		Assert.NotSame(fanOut, completedFirst);

		await haltTransaction.CommitAsync();
		IReadOnlyList<Guid> jobIds = await fanOut;

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobState = new("SELECT state, note FROM jobs WHERE id = $1", connection);
		jobState.Parameters.AddWithValue(jobIds.Single());
		await using NpgsqlDataReader reader = await jobState.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("blocked", reader.GetString(0));
		Assert.Equal("halted mid-fan-out", reader.GetString(1));
	}

	/// <summary>Lease recovery's running -> queued requeue is also a queued write: under a
	/// halted credential it must surface as 'blocked', not return claimable work.</summary>
	[Fact]
	public async Task LeaseRecoveryUnderAHaltedCredential_RequeuesAsBlocked()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand expireRunning = new(
			"""
			UPDATE jobs SET state = 'running', claimed_by = 'worker-a', claimed_at = now(),
				lease_expires_at = now() - interval '1 minute', heartbeat_at = now(), attempt_count = 1, max_attempts = 3
			WHERE id = $1
			""", connection))
		{
			expireRunning.Parameters.AddWithValue(jobId);
			await expireRunning.ExecuteNonQueryAsync();
		}

		await using (NpgsqlCommand haltCredential = new(
			"UPDATE credentials SET queue_halted = true, queue_halted_reason = 'halted before recovery', queue_halted_at = now() WHERE id = $1", connection))
		{
			haltCredential.Parameters.AddWithValue(credentialId);
			await haltCredential.ExecuteNonQueryAsync();
		}

		IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(10, CancellationToken.None);

		RecoveredJob job = Assert.Single(recovered, r => r.Id == jobId);
		Assert.Equal("blocked", job.NewState);
		Assert.Null(await _repository.ClaimJobAsync("worker-after-recovery", TimeSpan.FromMinutes(5), CancellationToken.None));
	}

	/// <summary>#147: a fan-out that is born blocked (credential already halted) emits a
	/// run-scoped queue.state and an appliance-wide system.notice -- the only actor that
	/// knows this happened is the repository, post-commit.
	///
	/// #174: the payload must also identify *which* credential's halt caused the block
	/// (mirroring the dispatcher's own halt-trip `credential_id`), and it must carry
	/// only the credential's identity -- a canary plaintext marker planted in
	/// `credential_secrets.ciphertext` (never read by this path) proves the fix didn't
	/// somehow widen the payload to include secret material.</summary>
	[Fact]
	public async Task AFanOutBornBlocked_EmitsQueueStateAndSystemNoticeWithCredentialId()
	{
		Guid credentialId = await SeedCredentialAsync();
		string secretCanary = $"do-not-leak-{Guid.NewGuid():N}";
		await SeedCredentialSecretAsync(credentialId, secretCanary);
		Guid firstRunId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(firstRunId, credentialId, JobStates.AuthFailed);
		await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, 3, CancellationToken.None);

		JobQueueRepository emittingRepository = new(
			_fixture.ConnectionString,
			NullLogger<JobQueueRepository>.Instance,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), NullLogger<JobEventPublisher>.Instance));
		Guid laterRunId = await emittingRepository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await emittingRepository.FanOutJobsAsync(
			laterRunId, [new JobSpec("scan", 1, CredentialId: credentialId)], "tester", CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand queueState = new(
			"SELECT payload FROM job_events WHERE event_type = 'queue.state' AND run_id = $1 AND payload->>'blocked' = 'true'", connection))
		{
			queueState.Parameters.AddWithValue(laterRunId);
			await using NpgsqlDataReader reader = await queueState.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			string payload = reader.GetString(0);
			Assert.Contains(credentialId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(secretCanary, payload, StringComparison.Ordinal);
			Assert.False(await reader.ReadAsync());
		}

		await using (NpgsqlCommand notice = new(
			"SELECT payload FROM job_events WHERE event_type = 'system.notice' AND payload->>'born_blocked_job_count' = '1'", connection))
		{
			await using NpgsqlDataReader reader = await notice.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			string payload = reader.GetString(0);
			Assert.Contains(credentialId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(secretCanary, payload, StringComparison.Ordinal);
			Assert.False(await reader.ReadAsync());
		}
	}

	[Fact]
	public async Task UnblockCredential_ClearsHaltAndUnblocksJobsAndRuns()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 1, CredentialId: credentialId), new JobSpec("scan", 1, CredentialId: credentialId)], "tester", CancellationToken.None);

		// Trip the halt with 3 consecutive auth failures.
		await SeedQueuedJobAsync(runId, credentialId);
		await TransitionToAuthFailedAsync(await SeedQueuedJobAsync(runId, credentialId));
		await TransitionToAuthFailedAsync(await SeedQueuedJobAsync(runId, credentialId));
		await TransitionToAuthFailedAsync(await SeedQueuedJobAsync(runId, credentialId));
		AuthFailureHaltResult halt = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.True(halt.HaltTripped);

		CredentialUnblockResult result = await _repository.UnblockCredentialAsync(credentialId, "operator cleared", CancellationToken.None);
		Assert.True(result.WasHalted);
		Assert.Equal(halt.BlockedJobIds.Order(), result.UnblockedJobIds.Order());
		Assert.Equal(halt.BlockedRunIds.Order(), result.UnblockedRunIds.Order());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Credential is no longer halted.
		await using (NpgsqlCommand cmd = new(
			"SELECT queue_halted FROM credentials WHERE id = $1", connection))
		{
			cmd.Parameters.AddWithValue(credentialId);
			Assert.False((bool)(await cmd.ExecuteScalarAsync())!);
		}

		// The released jobs are claimably 'queued' again — the 0005 trigger no longer
		// coerces them.
		await using (NpgsqlCommand blockedLeft = new(
			"SELECT count(*) FROM jobs WHERE credential_id = $1 AND state = 'blocked'", connection))
		{
			blockedLeft.Parameters.AddWithValue(credentialId);
			Assert.Equal(0L, (long)(await blockedLeft.ExecuteScalarAsync())!);
		}

		// Issue #146 acceptance: after unblock, fan-out for the credential creates
		// 'queued' jobs again instead of born-blocked ones.
		Guid laterRunId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		Guid laterJobId = Assert.Single(await _repository.FanOutJobsAsync(
			laterRunId, [new JobSpec("scan", 1, CredentialId: credentialId)], "tester", CancellationToken.None));
		await using (NpgsqlCommand laterJobState = new(
			"SELECT state FROM jobs WHERE id = $1", connection))
		{
			laterJobState.Parameters.AddWithValue(laterJobId);
			Assert.Equal("queued", (string)(await laterJobState.ExecuteScalarAsync())!);
		}
	}

	/// <summary>Issue #20: the halt is also the credential's operator-visible health
	/// signal -- tripping it flips <c>credentials.health</c> to 'auth_failing', not
	/// only <c>queue_halted</c>.</summary>
	[Fact]
	public async Task ThreeConsecutiveAuthFailures_AlsoFlipsCredentialHealthToAuthFailing()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		Guid thirdFailureJobId = await SeedQueuedJobAsync(runId, credentialId);
		await TransitionToAuthFailedAsync(thirdFailureJobId);

		AuthFailureHaltResult result = await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.True(result.HaltTripped);

		Assert.Equal("auth_failing", await ReadHealthAsync(credentialId));
	}

	/// <summary>Issue #20: unblocking a halted credential re-queues its work but does
	/// NOT prove the credential is valid again -- health stays 'auth_failing' until a
	/// successful <c>/credentials/{id}/test</c> call says otherwise (see
	/// <c>JobQueueRepository.UnblockCredentialAsync</c>'s doc comment).</summary>
	[Fact]
	public async Task UnblockCredential_DoesNotClearHealth()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId, "tester", CancellationToken.None);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await SeedTerminalJobAsync(runId, credentialId, JobStates.AuthFailed);
		await TransitionToAuthFailedAsync(await SeedQueuedJobAsync(runId, credentialId));
		await _repository.CheckConsecutiveAuthFailuresAsync(credentialId, threshold: 3, CancellationToken.None);
		Assert.Equal("auth_failing", await ReadHealthAsync(credentialId));

		CredentialUnblockResult result = await _repository.UnblockCredentialAsync(credentialId, "operator cleared", CancellationToken.None);
		Assert.True(result.WasHalted);

		Assert.Equal("auth_failing", await ReadHealthAsync(credentialId));
	}

	private async Task<string> ReadHealthAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT health FROM credentials WHERE id = $1", connection);
		command.Parameters.AddWithValue(credentialId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	[Fact]
	public async Task UnblockCredential_WhenNotHalted_IsNoOp()
	{
		Guid credentialId = await SeedCredentialAsync();

		CredentialUnblockResult result = await _repository.UnblockCredentialAsync(credentialId, "operator cleared", CancellationToken.None);
		Assert.False(result.WasHalted);
		Assert.Empty(result.UnblockedJobIds);
		Assert.Empty(result.UnblockedRunIds);
	}

	[Fact]
	public async Task UnblockCredential_ForNonExistentCredential_IsNoOp()
	{
		CredentialUnblockResult result = await _repository.UnblockCredentialAsync(Guid.NewGuid(), null, CancellationToken.None);
		Assert.False(result.WasHalted);
		Assert.Empty(result.UnblockedJobIds);
		Assert.Empty(result.UnblockedRunIds);
	}

	private async Task ClearQueueHaltAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand clear = new(
			"UPDATE credentials SET queue_halted = false, queue_halted_reason = NULL, queue_halted_at = NULL WHERE id = $1", connection);
		clear.Parameters.AddWithValue(credentialId);
		await clear.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'service') RETURNING id", connection);
		insert.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>#174 canary support: plants a plaintext marker as if it were the
	/// decrypted secret, purely so the born-blocked payload test can assert it never
	/// shows up there. Real secrets are opaque ciphertext (ADR-0005); this stands in for
	/// "any secret material" without needing the real envelope-encryption machinery.</summary>
	private async Task SeedCredentialSecretAsync(Guid credentialId, string plaintextMarker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO credential_secrets (credential_id, ciphertext, data_key_wrapped, master_key_id)
			VALUES ($1, $2, $3, 'test-master-key')
			""", connection);
		insert.Parameters.AddWithValue(credentialId);
		insert.Parameters.AddWithValue(System.Text.Encoding.UTF8.GetBytes(plaintextMarker));
		insert.Parameters.AddWithValue(System.Text.Encoding.UTF8.GetBytes("wrapped-key-placeholder"));
		await insert.ExecuteNonQueryAsync();
	}

	private async Task SeedTerminalJobWithIdAsync(Guid jobId, Guid runId, Guid credentialId, string terminalState, DateTime finishedAt)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (id, run_id, job_type, priority, state, credential_id, finished_at) VALUES ($1, $2, 'scan', 1, $3, $4, $5)", connection);
		insert.Parameters.AddWithValue(jobId);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(terminalState);
		insert.Parameters.AddWithValue(credentialId);
		insert.Parameters.AddWithValue(finishedAt);
		await insert.ExecuteNonQueryAsync();
	}

	private async Task SeedTerminalJobAsync(Guid runId, Guid credentialId, string terminalState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, credential_id, finished_at)
			VALUES ($1, 'scan', 1, $2, $3, now())
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(terminalState);
		insert.Parameters.AddWithValue(credentialId);
		await insert.ExecuteNonQueryAsync();

		// created_at ordering matters for "N most recent" -- force a tiny, deterministic gap.
		await Task.Delay(TimeSpan.FromMilliseconds(5));
	}

	private async Task<Guid> SeedQueuedJobAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id) VALUES ($1, 'scan', 1, 'queued', $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(credentialId);
		Guid id = (Guid)(await insert.ExecuteScalarAsync())!;
		await Task.Delay(TimeSpan.FromMilliseconds(5));
		return id;
	}

	/// <summary>
	/// Directly claims <paramref name="jobId"/> by id (bypassing
	/// <see cref="JobQueueRepository.ClaimJobAsync"/>'s global "next in priority order"
	/// pick, which would otherwise contend with the other queued jobs these tests seed
	/// alongside it) and then advances it to <c>auth-failed</c> through the real
	/// repository method under test.
	/// </summary>
	private async Task TransitionToAuthFailedAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand claim = new(
			"""
			UPDATE jobs SET
				state = 'running', claimed_by = 'worker-a', claimed_at = now(),
				lease_expires_at = now() + interval '5 minutes', heartbeat_at = now(), attempt_count = attempt_count + 1
			WHERE id = $1
			""", connection);
		claim.Parameters.AddWithValue(jobId);
		await claim.ExecuteNonQueryAsync();

		bool advanced = await _repository.AdvanceStateAsync(
			jobId, "worker-a", JobStates.Running, JobStates.AuthFailed, "credential rejected", clearLease: true, CancellationToken.None);
		Assert.True(advanced);
	}

	private async Task<DateTime> GetQueueHaltedAtAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand cmd = new(
			"SELECT queue_halted_at FROM credentials WHERE id = $1", connection);
		cmd.Parameters.AddWithValue(credentialId);
		return (DateTime)(await cmd.ExecuteScalarAsync())!;
	}
}
