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

namespace Waypoint.Core.Capacity;

/// <summary>
/// The shared, database-coordinated capacity lease pool (issue #569, ADR-0018 Option B,
/// protocol in ADR-0020): runners atomically claim weighted CPU/memory slots from the
/// singleton <c>capacity_pool</c> before executing a job, heartbeat the resulting
/// <c>capacity_leases</c> row while the job runs, and release it on terminal state.
/// Every mutation is a single SQL statement serialized on the pool row
/// (<c>FOR UPDATE</c>), so two runners racing for the last slot cannot both win --
/// the same claim-safety property the jobs queue gets from <c>FOR UPDATE SKIP
/// LOCKED</c> (ADR-0014).
///
/// <para>
/// <b>Failure posture is deny, never overcommit.</b> A missing pool row, an expired
/// schema, or an unreachable database all surface as a denied/failed claim -- callers
/// (see <c>CapacityLeaseCoordinator</c>) treat any exception from these methods as a
/// denial and release the job claim back to the queue. Silent overcommit is the one
/// outcome this contract forbids.
/// </para>
/// </summary>
public interface ICapacityLeasePool
{
	/// <summary>
	/// Upserts the singleton pool-capacity row. An <paramref name="operatorSet"/> write
	/// (explicit <c>CapacityPool:PoolCpuCores</c>/<c>PoolMemoryBytes</c> configuration)
	/// always overwrites and marks the row <c>source='operator'</c>. A derived write
	/// (host-derived discovery, ADR-0018) never overwrites an operator row, and
	/// converges with concurrent derived reports via <c>GREATEST</c> per axis -- a
	/// container-capped replica must not shrink the pool below what an uncapped
	/// sibling on the same host measured.
	/// </summary>
	Task RegisterPoolCapacityAsync(string reportedBy, double cpuCores, long memoryBytes, bool operatorSet, CancellationToken cancellationToken);

	/// <summary>
	/// Atomically claims <paramref name="cpuCores"/>/<paramref name="memoryBytes"/> for
	/// <paramref name="jobId"/>. Succeeds only when the weights fit the pool after
	/// summing all unexpired active leases (and, for a job without its own reservation,
	/// all other jobs' unexpired reservations -- the ADR-0020 fairness rule). Converts
	/// this job's own reservation to an active lease when one exists, and takes over a
	/// stale active row for the same job (the job queue lease already guarantees single
	/// job ownership). Returns <c>false</c> when the claim does not fit, including when
	/// no pool row exists at all (fail safe: no pool, no admission).
	/// </summary>
	Task<bool> TryClaimAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>
	/// Parks (or refreshes) an anti-starvation reservation for <paramref name="jobId"/>
	/// (ADR-0020): the reserved row counts against the pool for other jobs' claims, so
	/// capacity freed by completing jobs accumulates for this one instead of being
	/// consumed by an endless stream of smaller claims. Idempotent per job; never
	/// downgrades an existing active lease.
	/// </summary>
	Task<bool> TryReserveAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>Extends the lease for <paramref name="jobId"/> held by <paramref name="runnerId"/>; <c>false</c> when the row is gone or owned elsewhere (lease lost).</summary>
	Task<bool> RenewAsync(Guid jobId, string runnerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

	/// <summary>Releases the lease or reservation for <paramref name="jobId"/> (terminal state, stage requeue, or denied-after-claim cleanup). Missing row is a no-op.</summary>
	Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken);

	/// <summary>
	/// Deletes leases and reservations whose <c>expires_at</c> has passed (worker
	/// loss). Deletion is bookkeeping only: expired rows already stop counting against
	/// the pool the moment they expire, because every claim filters on
	/// <c>expires_at &gt; now()</c> -- the same recover-by-predicate posture as
	/// <c>RecoverExpiredLeasesAsync</c> on the jobs queue. Returns rows deleted.
	/// </summary>
	Task<int> ReapExpiredAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read side of the pool for the control plane's <c>GET /system</c> (issue #569:
/// "surface pool capacity, active reservations, and starvation reasons").
/// </summary>
public interface ICapacityPoolStatusReader
{
	/// <summary>Current pool capacity, usage, and waiting reservations; <c>null</c> when no pool row has ever been registered.</summary>
	Task<CapacityPoolStatus?> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Snapshot of the shared capacity pool for <c>GET /system</c>.</summary>
/// <param name="CpuCores">Total shareable CPU capacity (<c>capacity_pool.cpu_cores</c>).</param>
/// <param name="MemoryBytes">Total shareable memory capacity (<c>capacity_pool.memory_bytes</c>).</param>
/// <param name="Source"><c>operator</c> or <c>derived</c> -- who established the capacity numbers.</param>
/// <param name="UpdatedAt">When the capacity row was last written.</param>
/// <param name="LeasedCpuCores">CPU currently held by unexpired active leases.</param>
/// <param name="LeasedMemoryBytes">Memory currently held by unexpired active leases.</param>
/// <param name="ActiveLeaseCount">Unexpired active (non-reserved) lease rows.</param>
/// <param name="Reservations">Unexpired anti-starvation reservations -- each one is a job currently being starved and the reason capacity is being held back for it.</param>
public sealed record CapacityPoolStatus(
	double CpuCores,
	long MemoryBytes,
	string Source,
	DateTimeOffset UpdatedAt,
	double LeasedCpuCores,
	long LeasedMemoryBytes,
	int ActiveLeaseCount,
	IReadOnlyList<CapacityReservation> Reservations);

/// <summary>One waiting anti-starvation reservation (ADR-0020) surfaced through <c>GET /system</c>.</summary>
public sealed record CapacityReservation(
	Guid JobId,
	string JobType,
	string RunnerId,
	double CpuCores,
	long MemoryBytes,
	DateTimeOffset WaitingSince);
