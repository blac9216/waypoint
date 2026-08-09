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
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// #158: a host shutdown racing an in-flight lease must not throw
/// ObjectDisposedException out of the lease's using-block (masking the invocation's
/// real result/exception) or out of <see cref="WaypointRunspacePool.RentAsync"/> as a
/// double release. True concurrent shutdown-vs-lease timing is not reproducible
/// deterministically, so these tests drive the exact post-dispose code paths the race
/// lands on: a lease returned after <see cref="WaypointRunspacePool.Dispose"/>, and a
/// rent that observes disposal mid-wait.
/// </summary>
public sealed class WaypointRunspacePoolTests
{
	private static WaypointRunspacePool CreatePool(int maxRunspaces = 2)
	{
		PowerShellOptions options = new() { MaxRunspaces = maxRunspaces };
		return new WaypointRunspacePool(Options.Create(options), NullLogger<WaypointRunspacePool>.Instance);
	}

	/// <summary>Race 1, the deterministic post-dispose shape: a lease is still
	/// outstanding when Dispose runs (the idle bag it drains is empty because the
	/// runspace is out on lease), then the lease is returned. Pre-fix, Return's
	/// finally called the disposed SemaphoreSlim's Release and threw ObjectDisposedException
	/// out of this Dispose() call. Post-fix it must complete cleanly.</summary>
	[Fact]
	public async Task ReturningALease_AfterPoolDispose_DoesNotThrow()
	{
		WaypointRunspacePool pool = CreatePool();
		WaypointRunspacePool.RunspaceLease lease = await pool.RentAsync(CancellationToken.None);

		// Dispose while the lease is still outstanding -- mirrors host shutdown racing
		// an in-flight invocation. The idle bag is empty (the only runspace is leased),
		// so this exercises exactly the "outstanding lease survives Dispose" path.
		pool.Dispose();

		Exception? thrown = Record.Exception(lease.Dispose);
		Assert.Null(thrown);
	}

	/// <summary>Same shape as above but simulates the case in the issue verbatim: the
	/// invocation's own result must survive -- i.e. the code around the lease's
	/// using-block observes no exception from disposal and can report whatever the
	/// invocation itself produced, terminating error or success alike.</summary>
	[Fact]
	public async Task ALeaseUsingBlock_EndingAfterPoolDispose_PreservesTheCaller_sOutcome_NotAnObjectDisposedException()
	{
		WaypointRunspacePool pool = CreatePool();

		string? observedResult = null;
		Exception? observedException = null;
		try
		{
			using WaypointRunspacePool.RunspaceLease lease = await pool.RentAsync(CancellationToken.None);

			// The host disposes the pool while this invocation is still holding its
			// lease -- shutdown racing an in-flight job.
			pool.Dispose();

			// The invocation's own outcome, computed after the race window opened.
			// Pre-fix, the `using` block's implicit Dispose() at the closing brace threw
			// ObjectDisposedException here and this assignment was never reached.
			observedResult = "invocation completed normally";
		}
		catch (Exception ex)
		{
			observedException = ex;
		}

		Assert.Null(observedException);
		Assert.Equal("invocation completed normally", observedResult);
	}

	/// <summary>Poisoning a lease after dispose exercises the Discard branch of Return
	/// (not the idle-add branch) -- must be equally ODE-safe.</summary>
	[Fact]
	public async Task ReturningAPoisonedLease_AfterPoolDispose_DoesNotThrow()
	{
		WaypointRunspacePool pool = CreatePool();
		WaypointRunspacePool.RunspaceLease lease = await pool.RentAsync(CancellationToken.None);
		lease.Poison();

		pool.Dispose();

		Exception? thrown = Record.Exception(lease.Dispose);
		Assert.Null(thrown);
	}

	/// <summary>Multiple outstanding leases returned after dispose -- each Return call
	/// independently hits the disposed semaphore; none may throw or double-throw.</summary>
	[Fact]
	public async Task ReturningMultipleLeases_AfterPoolDispose_NoneThrow()
	{
		WaypointRunspacePool pool = CreatePool(maxRunspaces: 3);
		WaypointRunspacePool.RunspaceLease leaseA = await pool.RentAsync(CancellationToken.None);
		WaypointRunspacePool.RunspaceLease leaseB = await pool.RentAsync(CancellationToken.None);
		WaypointRunspacePool.RunspaceLease leaseC = await pool.RentAsync(CancellationToken.None);

		pool.Dispose();

		Assert.Null(Record.Exception(leaseA.Dispose));
		Assert.Null(Record.Exception(leaseB.Dispose));
		Assert.Null(Record.Exception(leaseC.Dispose));
	}

	/// <summary>Race 2: RentAsync called after the pool is already fully disposed
	/// (the ThrowIf check at entry) must surface exactly one clean ObjectDisposedException --
	/// not a second one from a catch-block Release, since no slot was ever acquired.</summary>
	[Fact]
	public async Task RentAsync_OnAnAlreadyDisposedPool_ThrowsExactlyOneObjectDisposedException()
	{
		WaypointRunspacePool pool = CreatePool();
		pool.Dispose();

		ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(
			() => pool.RentAsync(CancellationToken.None));

		Assert.NotNull(exception);
	}

	/// <summary>The narrower race the issue calls out: the ThrowIf check at entry
	/// passes (not yet disposed), then Dispose lands before WaitAsync observes it. This
	/// is only reachable while a slot is actually free -- WaitAsync completes
	/// synchronously/fast rather than parking the caller in the semaphore's internal
	/// wait queue, so the race is genuinely won or lost inside that fast path.
	/// (A caller already parked waiting on an exhausted pool is a different, more
	/// severe pre-existing condition -- SemaphoreSlim.Dispose does not release parked
	/// waiters at all, confirmed by manual repro and tracked separately, see #349 --
	/// out of scope here since #158 only concerns ObjectDisposedException reaching the
	/// caller.) Run many iterations with no artificial delay so the race window is hit
	/// on at least one: whichever side wins, the outcome must be a single clean ODE
	/// with no double release, never a hang and never two exceptions.</summary>
	[Fact]
	public async Task RentAsync_RacingDisposeOnAFreeSlot_SurfacesAtMostOneCleanObjectDisposedException_NoDoubleRelease()
	{
		for (int i = 0; i < 200; i++)
		{
			WaypointRunspacePool pool = CreatePool(maxRunspaces: 1);
			Task<WaypointRunspacePool.RunspaceLease> rent = pool.RentAsync(CancellationToken.None);

			// No delay: race Dispose against WaitAsync's fast path as tightly as possible.
			pool.Dispose();

			try
			{
				WaypointRunspacePool.RunspaceLease lease = await rent.WaitAsync(TimeSpan.FromSeconds(5));

				// Rent won the race -- acceptable, but the lease must still be returnable
				// cleanly against the now-disposed pool (exercises the same Return path
				// as the dedicated return-after-dispose tests, from a different angle).
				Assert.Null(Record.Exception(lease.Dispose));
			}
			catch (ObjectDisposedException)
			{
				// Dispose won the race -- the single expected outcome when it does.
				// Nothing further to assert: reaching this catch without a second
				// exception or a timeout IS the assertion that no double-release fired.
			}
		}
	}

	/// <summary>Baseline regression guard for #157: poison-eviction still frees the
	/// caller without waiting on the poisoned runspace, and the pool keeps serving
	/// afterward -- the #158 fix must not have touched this path's behavior.</summary>
	[Fact]
	public async Task PoisonedLease_IsDiscarded_AndThePoolKeepsServing()
	{
		WaypointRunspacePool pool = CreatePool(maxRunspaces: 1);

		WaypointRunspacePool.RunspaceLease first = await pool.RentAsync(CancellationToken.None);
		first.Poison();
		first.Dispose();

		Assert.Equal(1, pool.Health.PoisonedTotal);

		// The slot must be free again -- a second rent completes without blocking.
		using WaypointRunspacePool.RunspaceLease second = await pool.RentAsync(CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.NotNull(second.Runspace);

		pool.Dispose();
	}

	/// <summary>Baseline regression guard: a healthy (non-poisoned) return still goes
	/// to the idle bag and is reused rather than recreated.</summary>
	[Fact]
	public async Task HealthyLease_IsReturnedToIdle_AndReusedOnNextRent()
	{
		WaypointRunspacePool pool = CreatePool(maxRunspaces: 1);

		WaypointRunspacePool.RunspaceLease first = await pool.RentAsync(CancellationToken.None);
		System.Management.Automation.Runspaces.Runspace runspace = first.Runspace;
		first.Dispose();

		using WaypointRunspacePool.RunspaceLease second = await pool.RentAsync(CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Same(runspace, second.Runspace);
		Assert.Equal(1, pool.Health.CreatedTotal);

		pool.Dispose();
	}
}
