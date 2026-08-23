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
using Waypoint.Core.Capacity;
using Waypoint.Runner.Resources;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// Issue #569 (ADR-0020): the runner-side coordinator's policy layer -- deny on any
/// database failure (the DB-in-the-admission-path acceptance criterion: degrade to
/// "no new work", never silently overcommit), starvation escalation to a reservation
/// after the configured threshold, and lease-loss re-claim during heartbeat that never
/// cancels the running job.
/// </summary>
public sealed class CapacityLeaseCoordinatorTests
{
	private static readonly TimeSpan StarveAfter = TimeSpan.FromSeconds(30);

	private readonly FakeCapacityLeasePool _pool = new();
	private readonly ManualTimeProvider _clock = new();
	private readonly CapacityLeaseCoordinator _coordinator;

	public CapacityLeaseCoordinatorTests()
	{
		_coordinator = new CapacityLeaseCoordinator(
			_pool,
			Options.Create(new CapacityPoolOptions { StarvationReservationAfter = StarveAfter }),
			NullLogger<CapacityLeaseCoordinator>.Instance,
			_clock);
	}

	[Fact]
	public async Task TryAcquire_PoolClaims_ReturnsTrue()
	{
		_pool.ClaimResult = true;
		Assert.True(await _coordinator.TryAcquireAsync(Guid.NewGuid(), "scan", "runner-a", CancellationToken.None));
		Assert.Equal(1, _pool.ClaimCalls);
		Assert.Equal(0, _pool.ReserveCalls);
	}

	/// <summary>The DB-unavailable acceptance criterion: any pool exception is a denial, never an execution.</summary>
	[Fact]
	public async Task TryAcquire_DatabaseUnavailable_DeniesInsteadOfOvercommitting()
	{
		_pool.ThrowOnClaim = new InvalidOperationException("connection refused");
		Assert.False(await _coordinator.TryAcquireAsync(Guid.NewGuid(), "scan", "runner-a", CancellationToken.None));
	}

	[Fact]
	public async Task TryAcquire_DeniedPastStarvationThreshold_ParksReservation()
	{
		Guid jobId = Guid.NewGuid();
		_pool.ClaimResult = false;

		// First denial starts the starvation clock; no reservation yet.
		Assert.False(await _coordinator.TryAcquireAsync(jobId, "scan", "runner-a", CancellationToken.None));
		Assert.Equal(0, _pool.ReserveCalls);

		// Still within the threshold: denied, still no reservation.
		_clock.Advance(StarveAfter - TimeSpan.FromSeconds(1));
		Assert.False(await _coordinator.TryAcquireAsync(jobId, "scan", "runner-a", CancellationToken.None));
		Assert.Equal(0, _pool.ReserveCalls);

		// Threshold crossed: the reservation is parked (ADR-0020 fairness).
		_clock.Advance(TimeSpan.FromSeconds(2));
		Assert.False(await _coordinator.TryAcquireAsync(jobId, "scan", "runner-a", CancellationToken.None));
		Assert.Equal(1, _pool.ReserveCalls);
	}

	[Fact]
	public async Task TryAcquire_SuccessAfterDenials_ClearsTheStarvationClock()
	{
		Guid jobId = Guid.NewGuid();
		_pool.ClaimResult = false;
		Assert.False(await _coordinator.TryAcquireAsync(jobId, "scan", "runner-a", CancellationToken.None));

		_pool.ClaimResult = true;
		Assert.True(await _coordinator.TryAcquireAsync(jobId, "scan", "runner-a", CancellationToken.None));

		// A much later denial must restart the clock, not inherit the old first-denial
		// timestamp and immediately reserve.
		_clock.Advance(TimeSpan.FromMinutes(10));
		_pool.ClaimResult = false;
		Assert.False(await _coordinator.TryAcquireAsync(jobId, "scan", "runner-a", CancellationToken.None));
		Assert.Equal(0, _pool.ReserveCalls);
	}

	[Fact]
	public async Task Renew_LeaseHeld_JustRenews()
	{
		_pool.RenewResult = true;
		await _coordinator.RenewAsync(Guid.NewGuid(), "scan", "runner-a", CancellationToken.None);
		Assert.Equal(1, _pool.RenewCalls);
		Assert.Equal(0, _pool.ClaimCalls);
	}

	/// <summary>Lease loss while the job runs: re-claim, never cancel (ADR-0020 §4).</summary>
	[Fact]
	public async Task Renew_LeaseLost_ReclaimsWithoutThrowing()
	{
		_pool.RenewResult = false;
		_pool.ClaimResult = true;
		await _coordinator.RenewAsync(Guid.NewGuid(), "scan", "runner-a", CancellationToken.None);
		Assert.Equal(1, _pool.ClaimCalls);
	}

	[Fact]
	public async Task Renew_DatabaseUnavailable_SwallowsAndRetriesNextTick()
	{
		_pool.ThrowOnRenew = new InvalidOperationException("connection refused");
		await _coordinator.RenewAsync(Guid.NewGuid(), "scan", "runner-a", CancellationToken.None);
	}

	[Fact]
	public async Task Release_DatabaseUnavailable_SwallowsAndLeavesReclaimToTheReaper()
	{
		_pool.ThrowOnRelease = new InvalidOperationException("connection refused");
		await _coordinator.ReleaseAsync(Guid.NewGuid(), CancellationToken.None);
	}

	// Same pattern as ResourceAdmissionControllerTests' clock seam -- no external
	// fake-time package dependency.
	private sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset _now = DateTimeOffset.UtcNow;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}

	private sealed class FakeCapacityLeasePool : ICapacityLeasePool
	{
		public bool ClaimResult { get; set; }
		public bool RenewResult { get; set; }
		public Exception? ThrowOnClaim { get; set; }
		public Exception? ThrowOnRenew { get; set; }
		public Exception? ThrowOnRelease { get; set; }
		public int ClaimCalls { get; private set; }
		public int ReserveCalls { get; private set; }
		public int RenewCalls { get; private set; }

		public Task RegisterPoolCapacityAsync(string reportedBy, double cpuCores, long memoryBytes, bool operatorSet, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<bool> TryClaimAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			ClaimCalls++;
			return ThrowOnClaim is { } exception ? Task.FromException<bool>(exception) : Task.FromResult(ClaimResult);
		}

		public Task<bool> TryReserveAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			ReserveCalls++;
			return Task.FromResult(true);
		}

		public Task<bool> RenewAsync(Guid jobId, string runnerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
		{
			RenewCalls++;
			return ThrowOnRenew is { } exception ? Task.FromException<bool>(exception) : Task.FromResult(RenewResult);
		}

		public Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken)
			=> ThrowOnRelease is { } exception ? Task.FromException(exception) : Task.CompletedTask;

		public Task<int> ReapExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);
	}
}
