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

namespace Waypoint.Core.Scheduling;

/// <summary>
/// Storage for <c>schedules</c> (migration 0030, issue #31). One implementation
/// (<c>Waypoint.Infrastructure.Scheduling.ScheduleRepository</c>, plain Npgsql -- same
/// "no ORM for this layer" convention as every other repository in this codebase).
/// Control-plane only: <see cref="Waypoint.Api.Controllers.SchedulesController"/> owns
/// the CRUD surface and <c>ScheduleDispatchHostedService</c> owns the due-schedule scan
/// -- no runner ever touches this table (ADR-0013/0014: scheduling is a control-plane
/// producer, not an execution-domain concern).
/// </summary>
public interface IScheduleRepository
{
	Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken);

	Task<Schedule?> GetAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>
	/// Creates a schedule with its first <see cref="Schedule.NextRunAt"/> already
	/// computed by the caller (<c>SchedulesController</c>, via <see cref="CronExpression.GetNextOccurrence"/>)
	/// -- the repository never evaluates cron itself, keeping that logic in one place.
	/// Returns <c>null</c> when <paramref name="name"/> is already taken.
	/// </summary>
	Task<Guid?> CreateAsync(
		string name, string jobType, string cronExpression, string scopeJson, Guid? credentialId,
		DateTimeOffset nextRunAt, string createdBy, CancellationToken cancellationToken);

	/// <summary>
	/// Full-replacement update for the fields <c>PUT /schedules/{id}</c> accepts. A null
	/// argument leaves the corresponding column unchanged, matching
	/// <c>TargetRepository.UpdateAsync</c>'s convention. Returns
	/// <see cref="ScheduleWriteOutcome.NotFound"/> or <see cref="ScheduleWriteOutcome.NameTaken"/>
	/// as needed; <paramref name="nextRunAt"/> is recomputed by the caller whenever
	/// <paramref name="cronExpression"/> changes and passed through here so the stored
	/// value never drifts from the stored expression.
	/// </summary>
	Task<ScheduleWriteOutcome> UpdateAsync(
		Guid id, string? name, string? cronExpression, string? scopeJson, Guid? credentialId, bool clearCredential,
		bool? enabled, DateTimeOffset? nextRunAt, CancellationToken cancellationToken);

	Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>
	/// Every enabled schedule whose <c>next_run_at</c> is due (&lt;= <paramref name="asOf"/>)
	/// and is not depot-auto-paused -- the row set <c>ScheduleDispatchHostedService</c>'s
	/// sweep enqueues. A plain read: no row lock spans this call and the later
	/// <see cref="MarkDispatchedAsync"/> that advances <c>next_run_at</c> (they run on
	/// separate connections/transactions, and dispatch in between does run-creation work
	/// that is too long to hold a row lock across). That is safe only because exactly one
	/// control-plane producer sweeps <c>schedules</c> today -- <c>ScheduleDispatchHostedService</c>
	/// is registered solely on the API host (<c>AddWaypointApiSurface</c>), never on either
	/// runner host, and its own sweep loop awaits each pass to completion before starting
	/// the next (ADR-0013). If a second concurrent sweeper is ever introduced, this method
	/// must gain real double-dispatch protection (e.g. an atomic claim via
	/// <c>UPDATE ... RETURNING</c>) before that invariant no longer holds. See issue #518.
	/// </summary>
	Task<IReadOnlyList<Schedule>> ListDueAsync(DateTimeOffset asOf, CancellationToken cancellationToken);

	/// <summary>
	/// Records a dispatch: advances <c>next_run_at</c> to <paramref name="nextRunAt"/> and
	/// stamps <c>last_run_at</c>/<c>last_run_id</c>. <c>last_result</c> is set once the run
	/// reaches a terminal state (out of this method's scope -- see <see cref="SetLastResultAsync"/>).
	/// </summary>
	Task MarkDispatchedAsync(Guid id, DateTimeOffset nextRunAt, Guid runId, CancellationToken cancellationToken);

	/// <summary>Sets or clears <c>paused_reason</c> -- the auto-pause path (depot-kind schedules in disconnected mode), independent of the operator's own <c>enabled</c> flag.</summary>
	Task SetPausedReasonAsync(Guid id, string? reason, CancellationToken cancellationToken);

	/// <summary>Records the terminal outcome ("success"/"failure") of a schedule's most recently dispatched run.</summary>
	Task SetLastResultAsync(Guid id, string result, CancellationToken cancellationToken);
}

/// <summary>Outcomes for <see cref="IScheduleRepository.UpdateAsync"/>, mirroring <c>TargetWriteOutcome</c>'s shape.</summary>
public enum ScheduleWriteOutcome
{
	Ok,
	NotFound,
	NameTaken,
}
