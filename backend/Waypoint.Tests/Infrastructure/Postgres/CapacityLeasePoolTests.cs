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
using Waypoint.Core.Capacity;
using Waypoint.Infrastructure.Capacity;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #569 (ADR-0020): the shared capacity lease pool's claim/heartbeat/release/
/// reap protocol against real Postgres -- multi-runner contention (concurrent claims
/// racing for the last slot), lease loss and expiry semantics, the reservation-based
/// anti-starvation policy, operator-vs-derived pool capacity authority, and the
/// fail-safe "no pool row admits nothing" posture.
/// </summary>
[Collection("Postgres")]
public sealed class CapacityLeasePoolTests : IAsyncLifetime
{
	private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

	private readonly PostgresFixture _fixture;
	private CapacityLeasePoolRepository _pool = null!;

	public CapacityLeasePoolTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ExecuteAsync("TRUNCATE TABLE capacity_leases; DELETE FROM capacity_pool");
		_pool = new CapacityLeasePoolRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task TryClaim_NoPoolRowRegistered_DeniesEverything_FailSafe()
	{
		Guid jobId = await SeedJobAsync();

		bool claimed = await _pool.TryClaimAsync(jobId, "runner-a", "scan", 0.1, 1, Lease, CancellationToken.None);

		Assert.False(claimed);
	}

	[Fact]
	public async Task TryClaim_WithinCapacity_Succeeds_AndOverCapacity_Denies()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 4.0, 4096, operatorSet: false, CancellationToken.None);
		Guid first = await SeedJobAsync();
		Guid second = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(first, "runner-a", "scan", 3.0, 3000, Lease, CancellationToken.None));
		// Second claim would need 3.0 + 2.0 = 5.0 CPU > 4.0 pool.
		Assert.False(await _pool.TryClaimAsync(second, "runner-b", "scan", 2.0, 500, Lease, CancellationToken.None));
	}

	[Fact]
	public async Task TryClaim_BothAxesEnforced_MemoryAloneCanDeny()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 8.0, 1000, operatorSet: false, CancellationToken.None);
		Guid first = await SeedJobAsync();
		Guid second = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(first, "runner-a", "scan", 1.0, 800, Lease, CancellationToken.None));
		// Plenty of CPU left; memory (800 + 300 > 1000) is what must deny.
		Assert.False(await _pool.TryClaimAsync(second, "runner-a", "scan", 1.0, 300, Lease, CancellationToken.None));
	}

	/// <summary>
	/// The multi-runner contention acceptance criterion: many concurrent claimants
	/// (each simulating a different runner process) race for a pool that only fits
	/// two of them. Exactly two must win -- the FOR UPDATE serialization on the pool
	/// row is what makes a both-saw-it-free double-win impossible.
	/// </summary>
	[Fact]
	public async Task TryClaim_ConcurrentRunnersRacingForLastSlots_NeverOvercommits()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 2.0, 2048, operatorSet: false, CancellationToken.None);

		Guid[] jobs = new Guid[8];
		for (int i = 0; i < jobs.Length; i++)
		{
			jobs[i] = await SeedJobAsync();
		}

		bool[] results = await Task.WhenAll(jobs.Select((jobId, i) =>
			_pool.TryClaimAsync(jobId, $"runner-{i}", "scan", 1.0, 512, Lease, CancellationToken.None)));

		Assert.Equal(2, results.Count(claimed => claimed));

		(double cpu, long mem) = await SumActiveLeasesAsync();
		Assert.Equal(2.0, cpu);
		Assert.Equal(1024, mem);
	}

	[Fact]
	public async Task Release_FreesCapacityForTheNextClaim()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 2.0, 2048, operatorSet: false, CancellationToken.None);
		Guid first = await SeedJobAsync();
		Guid second = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(first, "runner-a", "scan", 2.0, 1024, Lease, CancellationToken.None));
		Assert.False(await _pool.TryClaimAsync(second, "runner-b", "scan", 2.0, 1024, Lease, CancellationToken.None));

		await _pool.ReleaseAsync(first, CancellationToken.None);

		Assert.True(await _pool.TryClaimAsync(second, "runner-b", "scan", 2.0, 1024, Lease, CancellationToken.None));
	}

	[Fact]
	public async Task Renew_ExtendsExpiry_AndFailsForWrongRunner()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 2.0, 2048, operatorSet: false, CancellationToken.None);
		Guid jobId = await SeedJobAsync();
		Assert.True(await _pool.TryClaimAsync(jobId, "runner-a", "scan", 1.0, 512, Lease, CancellationToken.None));

		DateTimeOffset before = await ReadExpiryAsync(jobId);
		Assert.True(await _pool.RenewAsync(jobId, "runner-a", TimeSpan.FromMinutes(30), CancellationToken.None));
		DateTimeOffset after = await ReadExpiryAsync(jobId);
		Assert.True(after > before, "renewal must push expires_at forward");

		// Another runner id must not renew a lease it does not own (lease-loss signal).
		Assert.False(await _pool.RenewAsync(jobId, "runner-b", Lease, CancellationToken.None));
	}

	/// <summary>
	/// Lease loss/worker death: an expired lease stops counting against the pool the
	/// moment it expires -- BEFORE any reaper runs -- because every claim filters on
	/// expires_at &gt; now(). The reaper then physically deletes it.
	/// </summary>
	[Fact]
	public async Task ExpiredLease_StopsCountingImmediately_AndReaperDeletesIt()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 2.0, 2048, operatorSet: false, CancellationToken.None);
		Guid dead = await SeedJobAsync();
		Guid next = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(dead, "runner-dead", "scan", 2.0, 1024, Lease, CancellationToken.None));

		// Simulate worker loss: backdate the lease past expiry (owner connection).
		await ExecuteAsync($"UPDATE capacity_leases SET expires_at = now() - interval '1 second' WHERE job_id = '{dead}'");

		// The stale row must not block the next claim even though it still exists.
		Assert.True(await _pool.TryClaimAsync(next, "runner-b", "scan", 2.0, 1024, Lease, CancellationToken.None));

		int reaped = await _pool.ReapExpiredAsync(CancellationToken.None);
		Assert.Equal(1, reaped);
		Assert.Equal(1, await CountLeasesAsync());
	}

	/// <summary>
	/// Worker loss then job re-claim: the capacity row is keyed on job id and follows
	/// the job (ADR-0020 §2) -- the new owner takes over the existing row instead of
	/// double-counting the job's weights.
	/// </summary>
	[Fact]
	public async Task TryClaim_SameJobAfterWorkerLoss_TakesOverTheExistingRowWithoutDoubleCounting()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 2.0, 2048, operatorSet: false, CancellationToken.None);
		Guid jobId = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(jobId, "runner-dead", "scan", 2.0, 1024, Lease, CancellationToken.None));

		// The same job, re-claimed by a surviving runner (its old row not yet expired):
		// must succeed -- its own row is excluded from the usage sums -- and must leave
		// exactly one row, now owned by the new runner.
		Assert.True(await _pool.TryClaimAsync(jobId, "runner-b", "scan", 2.0, 1024, Lease, CancellationToken.None));

		Assert.Equal(1, await CountLeasesAsync());
		Assert.True(await _pool.RenewAsync(jobId, "runner-b", Lease, CancellationToken.None));
	}

	/// <summary>
	/// The ADR-0020 fairness policy end to end: a large job denied capacity parks a
	/// reservation; from then on, small claims that WOULD fit the active tier are
	/// denied because the reservation counts against them -- so the capacity freed by
	/// the finishing job goes to the starved large job, which converts its reservation
	/// to an active lease.
	/// </summary>
	[Fact]
	public async Task Reservation_HoldsFreedCapacityForTheStarvedJob_AgainstSmallerClaims()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 4.0, 4096, operatorSet: false, CancellationToken.None);
		Guid running = await SeedJobAsync();
		Guid large = await SeedJobAsync();
		Guid small = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(running, "runner-a", "scan", 3.0, 3000, Lease, CancellationToken.None));

		// The large job does not fit (3 + 3 > 4) and parks a reservation.
		Assert.False(await _pool.TryClaimAsync(large, "runner-b", "scan", 3.0, 3000, Lease, CancellationToken.None));
		Assert.True(await _pool.TryReserveAsync(large, "runner-b", "scan", 3.0, 3000, Lease, CancellationToken.None));

		// The small job WOULD fit the active tier (3 + 1 <= 4) -- but the reservation
		// counts against it (3 + 3 + 1 > 4), so it is denied. Without this, a stream
		// of small claims starves the large job forever.
		Assert.False(await _pool.TryClaimAsync(small, "runner-a", "discover", 1.0, 100, Lease, CancellationToken.None));

		// The running job completes; the freed capacity goes to the reservation holder,
		// which converts to an active lease.
		await _pool.ReleaseAsync(running, CancellationToken.None);
		Assert.True(await _pool.TryClaimAsync(large, "runner-b", "scan", 3.0, 3000, Lease, CancellationToken.None));
		(double cpu, _) = await SumActiveLeasesAsync();
		Assert.Equal(3.0, cpu);

		// With the reservation converted (no longer counted twice), the small job's
		// claim is admitted on ordinary arithmetic again (3 + 1 <= 4): fairness holds
		// capacity back only while a starved job is actually waiting.
		Assert.True(await _pool.TryClaimAsync(small, "runner-a", "discover", 1.0, 100, Lease, CancellationToken.None));
	}

	/// <summary>Two mutually oversized reservations must not deadlock the pool: conversion checks only the active tier, so the first fit wins (ADR-0020 §5).</summary>
	[Fact]
	public async Task TwoLargeReservations_DoNotDeadlock_FirstFitConversionWins()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 4.0, 4096, operatorSet: false, CancellationToken.None);
		Guid first = await SeedJobAsync();
		Guid second = await SeedJobAsync();

		Assert.True(await _pool.TryReserveAsync(first, "runner-a", "scan", 3.0, 3000, Lease, CancellationToken.None));
		Assert.True(await _pool.TryReserveAsync(second, "runner-b", "scan", 3.0, 3000, Lease, CancellationToken.None));

		// Combined reservations (6.0) exceed the pool (4.0), but each alone fits the
		// active tier -- the first conversion attempt must win, not deadlock.
		Assert.True(await _pool.TryClaimAsync(first, "runner-a", "scan", 3.0, 3000, Lease, CancellationToken.None));
		Assert.False(await _pool.TryClaimAsync(second, "runner-b", "scan", 3.0, 3000, Lease, CancellationToken.None));
	}

	[Fact]
	public async Task TryReserve_NeverDowngradesAnActiveLease()
	{
		await _pool.RegisterPoolCapacityAsync("runner-a", 4.0, 4096, operatorSet: false, CancellationToken.None);
		Guid jobId = await SeedJobAsync();

		Assert.True(await _pool.TryClaimAsync(jobId, "runner-a", "scan", 1.0, 512, Lease, CancellationToken.None));
		Assert.False(await _pool.TryReserveAsync(jobId, "runner-a", "scan", 1.0, 512, Lease, CancellationToken.None));

		Assert.False(await ReadReservedAsync(jobId));
	}

	/// <summary>Explicit limits stay authoritative: an operator-set pool capacity is never overwritten by a derived report, in either order (ADR-0020 §1).</summary>
	[Fact]
	public async Task RegisterPoolCapacity_OperatorSetIsAuthoritativeOverDerived()
	{
		await _pool.RegisterPoolCapacityAsync("operator-config", 2.0, 2048, operatorSet: true, CancellationToken.None);

		// A larger derived report must NOT widen an operator-set pool.
		await _pool.RegisterPoolCapacityAsync("runner-a", 64.0, 1_000_000, operatorSet: false, CancellationToken.None);
		CapacityPoolStatus? status = await _pool.GetStatusAsync(CancellationToken.None);
		Assert.NotNull(status);
		Assert.Equal(2.0, status!.CpuCores);
		Assert.Equal(2048, status.MemoryBytes);
		Assert.Equal("operator", status.Source);

		// A new operator write always wins.
		await _pool.RegisterPoolCapacityAsync("operator-config", 8.0, 8192, operatorSet: true, CancellationToken.None);
		status = await _pool.GetStatusAsync(CancellationToken.None);
		Assert.Equal(8.0, status!.CpuCores);
	}

	[Fact]
	public async Task RegisterPoolCapacity_DerivedReportsConvergeViaGreatest()
	{
		// An uncapped replica measures the host; a container-capped sibling reports
		// smaller numbers. The pool must keep the larger measurement on each axis.
		await _pool.RegisterPoolCapacityAsync("runner-a", 8.0, 1024, operatorSet: false, CancellationToken.None);
		await _pool.RegisterPoolCapacityAsync("runner-b", 4.0, 4096, operatorSet: false, CancellationToken.None);

		CapacityPoolStatus? status = await _pool.GetStatusAsync(CancellationToken.None);
		Assert.NotNull(status);
		Assert.Equal(8.0, status!.CpuCores);
		Assert.Equal(4096, status.MemoryBytes);
		Assert.Equal("derived", status.Source);
	}

	[Fact]
	public async Task GetStatus_ReportsCapacityLeasesAndReservations()
	{
		Assert.Null(await _pool.GetStatusAsync(CancellationToken.None));

		await _pool.RegisterPoolCapacityAsync("runner-a", 4.0, 4096, operatorSet: false, CancellationToken.None);
		Guid active = await SeedJobAsync();
		Guid starved = await SeedJobAsync();
		Assert.True(await _pool.TryClaimAsync(active, "runner-a", "scan", 2.0, 1024, Lease, CancellationToken.None));
		Assert.True(await _pool.TryReserveAsync(starved, "runner-b", "scan", 3.0, 3000, Lease, CancellationToken.None));

		CapacityPoolStatus? status = await _pool.GetStatusAsync(CancellationToken.None);
		Assert.NotNull(status);
		Assert.Equal(4.0, status!.CpuCores);
		Assert.Equal(2.0, status.LeasedCpuCores);
		Assert.Equal(1024, status.LeasedMemoryBytes);
		Assert.Equal(1, status.ActiveLeaseCount);
		CapacityReservation reservation = Assert.Single(status.Reservations);
		Assert.Equal(starved, reservation.JobId);
		Assert.Equal("scan", reservation.JobType);
		Assert.Equal("runner-b", reservation.RunnerId);
		Assert.Equal(3.0, reservation.CpuCores);
	}

	private async Task<Guid> SeedJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('scan', 1, 'queued') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task ExecuteAsync(string sql)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(sql, connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<(double Cpu, long Memory)> SumActiveLeasesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT COALESCE(SUM(cpu_cores), 0)::double precision, COALESCE(SUM(memory_bytes), 0)::bigint FROM capacity_leases WHERE NOT reserved AND expires_at > now()", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		await reader.ReadAsync();
		return (reader.GetDouble(0), reader.GetInt64(1));
	}

	private async Task<int> CountLeasesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*)::int FROM capacity_leases", connection);
		return (int)(await command.ExecuteScalarAsync())!;
	}

	private async Task<bool> ReadReservedAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT reserved FROM capacity_leases WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	private async Task<DateTimeOffset> ReadExpiryAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT expires_at FROM capacity_leases WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		await reader.ReadAsync();
		return reader.GetFieldValue<DateTimeOffset>(0);
	}
}
