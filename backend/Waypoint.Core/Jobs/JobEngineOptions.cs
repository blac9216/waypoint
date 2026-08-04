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

namespace Waypoint.Core.Jobs;

/// <summary>
/// Dispatcher tuning, bound from the <c>JobEngine</c> configuration section. Every
/// budget here is a deliberate choice per this issue's "decide deliberately what your
/// timeout budget is and say so" instruction -- see each member's remarks for the
/// reasoning, not just the default.
/// </summary>
public sealed class JobEngineOptions
{
	public const string SectionName = "JobEngine";

	/// <summary>
	/// Whether the dispatcher and lease-recovery hosted services run at all. Defaults
	/// to <c>true</c>, mirroring <c>Database:RunMigrationsOnStartup</c>'s pattern; the
	/// "Testing" environment configuration turns it off because the in-process
	/// <c>WebApplicationFactory</c> test host has no Postgres to poll.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>How many jobs this process executes concurrently. Default 4: modest for a single-node appliance (ADR-0001), leaves headroom for the API's own request handling.</summary>
	public int MaxConcurrency { get; set; } = 4;

	/// <summary>How long an idle dispatcher waits between claim attempts when the queue was empty.</summary>
	public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// How long a claim's lease is valid before the recovery sweep considers the worker
	/// dead. 5 minutes: long enough that a normal heartbeat cadence (a third of this,
	/// see <see cref="HeartbeatInterval"/>) renews it two heartbeats before expiry even
	/// under a missed beat, short enough that a genuinely crashed worker's job is back
	/// in the queue well within an operator's patience for "is this run stuck".
	/// </summary>
	public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>How often an in-flight job's lease is renewed. Default: a third of <see cref="LeaseDuration"/>, computed in <see cref="HeartbeatIntervalOrDefault"/> unless set explicitly.</summary>
	public TimeSpan? HeartbeatInterval { get; set; }

	/// <summary>How often the recovery sweep runs.</summary>
	public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Maximum rows one recovery sweep pass touches, so a large backlog of expired leases is drained across several passes rather than one long transaction.</summary>
	public int RecoveryBatchSize { get; set; } = 50;

	/// <summary>
	/// N consecutive <c>auth-failed</c> jobs against the same credential that halt its
	/// queue (ADR-0008: "N consecutive auth failures (default 3)").
	/// </summary>
	public int ConsecutiveAuthFailureThreshold { get; set; } = 3;

	/// <summary>
	/// <see cref="Npgsql.NpgsqlCommand.CommandTimeout"/> for a <c>job_events</c> INSERT,
	/// in seconds. Deliberately shorter than Npgsql's 30s default (see #108/#117): the
	/// ordering lock's measured worst case (issue #117: 16 concurrent writers, p99
	/// 38.30 ms) is roughly two orders of magnitude below this, so 5s is a wide margin
	/// for ordinary contention while still failing an actually-stuck writer (something
	/// holding the global lock across a long-running transaction, the exact anti-pattern
	/// the schema's doc comments warn against) more than 6x faster than the inherited
	/// default, with a log line that names lock contention as the likely cause rather
	/// than a bare Npgsql timeout exception.
	/// </summary>
	public int EventCommandTimeoutSeconds { get; set; } = 5;

	/// <summary>
	/// Maximum <c>job_events</c> rows per batched INSERT on the <see cref="IJobLogBuffer"/>
	/// path -- the #117 budget's batch half. 100 amortizes the ordering lock two orders
	/// of magnitude versus row-at-a-time while keeping each statement far inside
	/// <see cref="EventCommandTimeoutSeconds"/> (a 100-row INSERT is low-single-digit
	/// milliseconds at #117's measured per-row cost).
	/// </summary>
	public int EventBatchMaxSize { get; set; } = 100;

	/// <summary>
	/// How often the buffered event writer flushes -- the #117 budget's latency half.
	/// 250 ms is imperceptible next to SSE delivery and long enough that a chatty
	/// scriptblock accumulates real batches instead of degenerating to row-at-a-time.
	/// </summary>
	public TimeSpan EventFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

	/// <summary>
	/// Bounded in-memory capacity of the event buffer. At the #117 per-batch ceiling
	/// this is roughly 25 s of maximum-rate backlog before drops begin; beyond it the
	/// writer drops new events (counted, logged) rather than grow without bound while
	/// Postgres is unreachable.
	/// </summary>
	public int EventBufferCapacity { get; set; } = 10_000;

	public TimeSpan HeartbeatIntervalOrDefault =>
		HeartbeatInterval ?? TimeSpan.FromTicks(LeaseDuration.Ticks / 3);
}
