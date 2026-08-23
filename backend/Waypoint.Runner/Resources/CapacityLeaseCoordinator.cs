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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Capacity;
using Waypoint.Core.Jobs;

namespace Waypoint.Runner.Resources;

/// <summary>
/// The runner-side face of the shared capacity lease pool (issue #569, ADR-0020):
/// wraps <see cref="ICapacityLeasePool"/> with the policy the dispatcher needs --
/// deny-on-error (the database sitting in the admission path must never turn into
/// silent overcommit), starvation escalation (park a reservation once a job has been
/// denied longer than <see cref="CapacityPoolOptions.StarvationReservationAfter"/>),
/// and re-claim-on-lost-lease during heartbeat.
///
/// <para>
/// Ordering contract (ADR-0018 §5): this coordinator runs strictly AFTER the local
/// <see cref="ResourceAdmissionController"/> has admitted the job, so a runner's own
/// discovered/capped budget stays the authoritative upper bound on what it may ever
/// claim from the pool -- the pool coordinates sharing, it never grants a runner more
/// than ADR-0018 discovery established that runner has.
/// </para>
/// </summary>
public sealed partial class CapacityLeaseCoordinator
{
	private readonly ICapacityLeasePool _pool;
	private readonly IOptions<CapacityPoolOptions> _options;
	private readonly ILogger<CapacityLeaseCoordinator> _logger;
	private readonly TimeProvider _timeProvider;

	/// <summary>First-denial timestamp per job id, driving starvation escalation. Cleared on successful claim or release.</summary>
	private readonly ConcurrentDictionary<Guid, DateTimeOffset> _firstDeniedAt = new();

	public CapacityLeaseCoordinator(ICapacityLeasePool pool, IOptions<CapacityPoolOptions> options, ILogger<CapacityLeaseCoordinator> logger)
		: this(pool, options, logger, TimeProvider.System)
	{
	}

	/// <summary>Test seam for the starvation-escalation clock, mirroring <see cref="ResourceAdmissionController"/>'s.</summary>
	internal CapacityLeaseCoordinator(ICapacityLeasePool pool, IOptions<CapacityPoolOptions> options, ILogger<CapacityLeaseCoordinator> logger, TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(pool);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(timeProvider);
		_pool = pool;
		_options = options;
		_logger = logger;
		_timeProvider = timeProvider;
	}

	/// <summary>
	/// Attempts to acquire pool capacity for a claimed job. Returns <c>false</c> on
	/// denial (pool full, no pool row yet) AND on any database failure -- the job claim
	/// is then released back to the queue exactly like a local admission denial, so an
	/// unreachable database degrades to "no new work starts" rather than overcommit.
	/// A job denied longer than <see cref="CapacityPoolOptions.StarvationReservationAfter"/>
	/// escalates to an anti-starvation reservation (ADR-0020) before returning.
	/// </summary>
	public async Task<bool> TryAcquireAsync(Guid jobId, string jobType, string runnerId, CancellationToken cancellationToken)
	{
		JobResourceProfile profile = JobResourceProfiles.ForJobType(jobType);
		CapacityPoolOptions options = _options.Value;

		bool claimed;
		try
		{
			claimed = await _pool.TryClaimAsync(jobId, runnerId, jobType, profile.CpuCores, profile.MemoryBytes, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			LogPoolUnavailable(jobId, jobType, exception);
			return false;
		}

		if (claimed)
		{
			_firstDeniedAt.TryRemove(jobId, out _);
			return true;
		}

		DateTimeOffset now = _timeProvider.GetUtcNow();
		DateTimeOffset firstDenied = _firstDeniedAt.GetOrAdd(jobId, now);
		LogPoolDenied(jobId, jobType, profile.CpuCores, profile.MemoryBytes);

		if (now - firstDenied >= options.StarvationReservationAfter)
		{
			try
			{
				bool reserved = await _pool.TryReserveAsync(jobId, runnerId, jobType, profile.CpuCores, profile.MemoryBytes, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
				if (reserved)
				{
					LogStarvationReservationParked(jobId, jobType, now - firstDenied, profile.CpuCores, profile.MemoryBytes);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				LogPoolUnavailable(jobId, jobType, exception);
			}
		}

		return false;
	}

	/// <summary>
	/// Heartbeat-driven renewal. A lost capacity lease (row reaped/deleted while the
	/// job still holds its queue lease) is re-claimed rather than killing the running
	/// job: the job's own queue lease is the ownership authority (ADR-0014), and
	/// abandoning real in-flight work over lost bookkeeping would be worse than the
	/// transient window where the pool under-counts it. Failure to re-claim is logged
	/// loudly and retried on the next heartbeat tick.
	/// </summary>
	public async Task RenewAsync(Guid jobId, string jobType, string runnerId, CancellationToken cancellationToken)
	{
		CapacityPoolOptions options = _options.Value;
		try
		{
			if (await _pool.RenewAsync(jobId, runnerId, options.LeaseDuration, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			JobResourceProfile profile = JobResourceProfiles.ForJobType(jobType);
			bool reclaimed = await _pool.TryClaimAsync(jobId, runnerId, jobType, profile.CpuCores, profile.MemoryBytes, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
			LogLeaseLost(jobId, jobType, reclaimed);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			LogRenewFailed(jobId, exception);
		}
	}

	/// <summary>
	/// Releases the job's capacity lease (or pending reservation) on terminal state,
	/// stage requeue, or a released claim. A database failure here is logged and
	/// swallowed: the lease's own expiry (and the reaper) reclaims the capacity, the
	/// same way an expired jobs-queue lease is recovered after worker loss.
	/// </summary>
	public async Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_firstDeniedAt.TryRemove(jobId, out _);
		try
		{
			await _pool.ReleaseAsync(jobId, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			LogReleaseFailed(jobId, exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "Capacity pool unavailable for job {JobId} ({JobType}); denying admission (fail safe -- the pool never silently overcommits)")]
	private partial void LogPoolUnavailable(Guid jobId, string jobType, Exception exception);

	[LoggerMessage(Level = LogLevel.Information, Message = "Capacity pool denied job {JobId} ({JobType}): profile {ProfileCpu} cores / {ProfileMemory} bytes does not currently fit the shared pool")]
	private partial void LogPoolDenied(Guid jobId, string jobType, double profileCpu, long profileMemory);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) starved of pool capacity for {WaitedSoFar}; parked an anti-starvation reservation for {ProfileCpu} cores / {ProfileMemory} bytes -- freed capacity now accumulates for this job (ADR-0020)")]
	private partial void LogStarvationReservationParked(Guid jobId, string jobType, TimeSpan waitedSoFar, double profileCpu, long profileMemory);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) lost its capacity lease while running; re-claim {Reclaimed}. The job continues -- its queue lease is the ownership authority (ADR-0014)")]
	private partial void LogLeaseLost(Guid jobId, string jobType, bool reclaimed);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Capacity lease renewal failed for job {JobId}; will retry on the next heartbeat tick")]
	private partial void LogRenewFailed(Guid jobId, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Capacity lease release failed for job {JobId}; the lease's expiry/reaper will reclaim it")]
	private partial void LogReleaseFailed(Guid jobId, Exception exception);
}
