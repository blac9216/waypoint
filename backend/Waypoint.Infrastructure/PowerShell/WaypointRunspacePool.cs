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

		// The check above and WaitAsync below are not atomic: Dispose can land in the
		// gap and dispose _slots before WaitAsync observes it, or while WaitAsync is
		// blocked. SemaphoreSlim throws ObjectDisposedException from WaitAsync in that
		// case -- that is the correct, single exception to surface (shutdown mid-rent
		// is genuinely "the pool is gone"), so it is let through rather than wrapped.
		// What must NOT happen is a second ODE: if WaitAsync itself threw ODE, the slot
		// was never acquired, so the catch below must not attempt to release it.
		bool acquired = false;
		try
		{
			await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
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
	/// host has already disposed the pool at shutdown -- the semaphore is gone, but a
	/// release with nothing left to release is meaningless, not an error. Swallowing
	/// here is what keeps that race from throwing ObjectDisposedException out of
	/// <see cref="RunspaceLease.Dispose"/> and masking the invocation's real
	/// result/exception in the caller's using-block (see #158). The check-then-release
	/// is still racy against a concurrent Dispose, so the catch is the backstop.
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
			// Dispose won the race between the check above and this call. The pool is
			// gone; there is nothing to release into.
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
		while (_idle.TryTake(out Runspace? runspace))
		{
			runspace.Dispose();
		}

		_slots.Dispose();
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
