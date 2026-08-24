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

using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.PowerShell;

namespace Waypoint.Infrastructure.PowerShell;

/// <summary>
/// A hand-rolled runspace pool (deliberately not SMA's <see cref="RunspacePool"/>):
/// the one capability slice 2 needs that SMA's pool does not expose is evicting a
/// single poisoned member -- a pipeline that ignored Stop() past the grace period
/// sits on a runspace that can never be trusted again, and with SMA's pool the only
/// recovery is discarding the whole pool mid-flight. Here each runspace is an
/// independent member: rent blocks on a semaphore bounding concurrency, return
/// recycles healthy members, and <see cref="Discard"/> replaces a poisoned one
/// (the CLR cannot kill its thread; the runspace object is dropped for GC and its
/// slot freed -- counted, because a rising count is an operator signal).
///
/// Module preload happens once per runspace via <see cref="InitialSessionState"/>,
/// so a rented member already has the vcf-docker-download modules (or the test
/// stub) imported -- per ADR-0006, connection/module reuse across jobs is the point
/// of pooling.
/// </summary>
public sealed partial class WaypointRunspacePool : IDisposable
{
	private readonly IOptions<PowerShellOptions> _options;
	private readonly ILogger<WaypointRunspacePool> _logger;
	private readonly SemaphoreSlim _slots;
	private readonly ConcurrentBag<Runspace> _idle = new();
	private readonly CancellationTokenSource _disposalCts = new();
	private long _poisonedTotal;
	private long _createdTotal;
	private volatile bool _disposed;

	public WaypointRunspacePool(IOptions<PowerShellOptions> options, ILogger<WaypointRunspacePool> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		if (options.Value.MaxRunspaces <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options), options.Value.MaxRunspaces, "MaxRunspaces must be positive.");
		}

		_options = options;
		_logger = logger;
		_slots = new SemaphoreSlim(options.Value.MaxRunspaces, options.Value.MaxRunspaces);
	}

	/// <summary>Pool state for a future health surface: slots in use, idle members, and how many members have ever been poisoned.</summary>
	public PoolHealth Health => new(
		_options.Value.MaxRunspaces,
		BusyCount: _options.Value.MaxRunspaces - _slots.CurrentCount,
		IdleCount: _idle.Count,
		PoisonedTotal: Interlocked.Read(ref _poisonedTotal),
		CreatedTotal: Interlocked.Read(ref _createdTotal));

	/// <summary>Rents an opened, module-preloaded runspace; blocks when all slots are busy. Dispose the lease to return it healthy, or call <see cref="RunspaceLease.Poison"/> first.</summary>
	public async Task<RunspaceLease> RentAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		// The check above and WaitAsync below are not atomic: Dispose can set _disposed
		// and cancel _disposalCts in the gap between this check and WaitAsync, or while
		// WaitAsync is blocked/parked. That race is exactly what the linked token below
		// exists to handle (#343): _slots itself is never disposed (see Dispose for why),
		// so WaitAsync no longer has a path to ObjectDisposedException on its own --
		// disposal is instead observed as cancellation of the linked token, translated
		// below into the same ObjectDisposedException this entry check throws, so "rent
		// during/after dispose" is one consistent outcome whichever path is taken.
		//
		// #343: a caller already PARKED in WaitAsync (pool fully exhausted) was not
		// released by SemaphoreSlim.Dispose() at all -- it just hung forever, no
		// exception, no completion. _disposalCts is cancelled by Dispose(), and this
		// linked token is what actually unblocks a parked waiter. The linked CTS is
		// per-rent and must not leak past this call (#351/#324) -- disposed in the
		// `using` above regardless of outcome.
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposalCts.Token);
		bool acquired = false;
		try
		{
			try
			{
				await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (_disposalCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				// The wait was released because the pool is disposing, not because the
				// caller's own token fired. Surface the same shutdown outcome RentAsync's
				// entry check and the fast-path race in #158 already produce, so "rent
				// during/after dispose" is one consistent exception regardless of whether
				// the caller was parked or arriving fresh.
				throw new ObjectDisposedException(GetType().FullName);
			}

			acquired = true;

			if (!_idle.TryTake(out Runspace? runspace))
			{
				runspace = CreateRunspace();
			}

			return new RunspaceLease(this, runspace);
		}
		catch
		{
			if (acquired)
			{
				ReleaseSlot();
			}

			throw;
		}
	}

	private Runspace CreateRunspace()
	{
		InitialSessionState sessionState = InitialSessionState.CreateDefault2();
		foreach (string modulePath in _options.Value.ModulePreloadPaths)
		{
			sessionState.ImportPSModule(modulePath);
		}

		// Issue #629/#618: by-name imports (e.g. VMware.PowerCLI) resolved via
		// PSModulePath, imported into the same initial session state as the disk-path
		// modules above. The full PowerCLI meta-module MUST be imported here rather than
		// left to first-cmdlet autoload: in the runner's in-process host a partially
		// autoloaded PowerCLI hydrates discovery pscustomobjects whose NoteProperties do
		// not survive the executor's output capture, silently storing zero inventory.
		if (_options.Value.ModulePreloadNames.Count > 0)
		{
			sessionState.ImportPSModule([.. _options.Value.ModulePreloadNames]);
		}

		Runspace runspace = RunspaceFactory.CreateRunspace(sessionState);
		runspace.Open();
		long createdTotal = Interlocked.Increment(ref _createdTotal);
		LogRunspaceCreated(createdTotal);
		return runspace;
	}

	private void Return(Runspace runspace, bool poisoned)
	{
		try
		{
			if (poisoned || _disposed || runspace.RunspaceStateInfo.State != RunspaceState.Opened)
			{
				if (poisoned)
				{
					long total = Interlocked.Increment(ref _poisonedTotal);
					LogRunspacePoisoned(total);
				}

				Discard(runspace);
				return;
			}

			_idle.Add(runspace);
		}
		finally
		{
			ReleaseSlot();
		}
	}

	/// <summary>
	/// Releases one semaphore slot, tolerating a release that lands after
	/// <see cref="Dispose"/>. A lease can be returned (via its using-block) after the
	/// host has already disposed the pool at shutdown; releasing into a semaphore
	/// nobody is waiting on anymore is meaningless, not an error, so the `_disposed`
	/// check short-circuits it (see #158). `_slots` itself is never disposed (#343 --
	/// see <see cref="Dispose"/>), so `Release()` cannot actually throw
	/// ObjectDisposedException here in current behavior; the catch remains as a
	/// backstop in case that invariant ever changes, so this method's contract --
	/// "never throws" -- does not silently depend on it.
	/// </summary>
	private void ReleaseSlot()
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			_slots.Release();
		}
		catch (ObjectDisposedException)
		{
			// Backstop only -- see summary above. Not currently reachable.
		}
	}

	private static void Discard(Runspace runspace)
	{
		// Dispose on a background thread: disposing a runspace whose pipeline ignored
		// Stop() can itself block indefinitely, and the caller is on the job worker's
		// path. If this thread wedges, the runspace object is simply abandoned -- the
		// semaphore slot was already freed by Return's finally.
		_ = Task.Run(() =>
		{
			try
			{
				runspace.Dispose();
			}
			catch (Exception)
			{
				// A poisoned runspace may throw from Dispose; there is nothing left to do
				// with it -- the slot is free and the object is garbage either way.
			}
		});
	}

	public void Dispose()
	{
		_disposed = true;

		// #343: cancel the disposal token so a caller parked in
		// _slots.WaitAsync(linked.Token) is released with a catchable cancellation
		// (translated to ObjectDisposedException in RentAsync) instead of hanging
		// forever -- SemaphoreSlim.Dispose() does not fault or release existing
		// waiters at all, which is the entire bug this fixes.
		_disposalCts.Cancel();

		// Deliberately NOT calling _slots.Dispose() here. This looks like a resource
		// leak but is not: confirmed by repeated empirical repro (not just reasoning)
		// that disposing the SemaphoreSlim synchronously, even strictly after
		// Cancel(), races the parked waiter's own cancellation-continuation teardown
		// -- Dispose() can tear down the semaphore's internal wait-queue state before
		// that continuation has run, and the parked Task then never completes at all
		// (no exception, no fault, just silently abandoned -- the exact hang this
		// issue exists to fix, just moved one line later). There is no reliable
		// synchronous signal available to wait for "every parked continuation has
		// finished" short of internal SemaphoreSlim state this type does not have
		// access to; a deferred/background dispose only shrinks the race window
		// rather than closing it. SemaphoreSlim holds no unmanaged resource unless
		// its AvailableWaitHandle is ever accessed, which this pool never does, so
		// leaving it for the GC to collect is a correct, deterministic trade against
		// an indeterministic disposal race with no first-class fix in the BCL.
		_disposalCts.Dispose();

		while (_idle.TryTake(out Runspace? runspace))
		{
			runspace.Dispose();
		}
	}

	/// <summary>A rented runspace. Dispose returns it to the pool; <see cref="Poison"/> marks it for replacement instead.</summary>
	public sealed class RunspaceLease : IDisposable
	{
		private WaypointRunspacePool? _owner;
		private bool _poisoned;

		internal RunspaceLease(WaypointRunspacePool owner, Runspace runspace)
		{
			_owner = owner;
			Runspace = runspace;
		}

		public Runspace Runspace { get; }

		/// <summary>The pipeline on this runspace could not be stopped cleanly; never reuse it.</summary>
		public void Poison()
		{
			_poisoned = true;
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _owner, null)?.Return(Runspace, _poisoned);
		}
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Runspace created (lifetime total {CreatedTotal})")]
	private partial void LogRunspaceCreated(long createdTotal);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Runspace poisoned and replaced (lifetime total {PoisonedTotal}) -- a pipeline ignored Stop() past the grace period")]
	private partial void LogRunspacePoisoned(long poisonedTotal);
}

/// <summary>Point-in-time pool state.</summary>
public sealed record PoolHealth(int MaxRunspaces, int BusyCount, int IdleCount, long PoisonedTotal, long CreatedTotal);
