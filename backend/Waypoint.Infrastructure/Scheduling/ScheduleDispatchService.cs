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
using Waypoint.Core.Jobs;
using Waypoint.Core.Scheduling;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Runs;

namespace Waypoint.Infrastructure.Scheduling;

/// <summary>
/// Issue #31 (epic #14): the control-plane producer half of the scheduling engine
/// (ADR-0013 -- "the scheduler is a control-plane producer: it validates and enqueues
/// due jobs. Compliance/download runners claim and execute their allowlisted scheduled
/// job types"). One sweep pass validates every due schedule and enqueues its run via
/// <see cref="IJobControlRepository"/>/<see cref="RunCreationService"/> -- exactly the
/// same enqueue surface <c>SchedulesController</c>, <c>RunsController</c>,
/// <c>DiscoveryController</c> and <c>CatalogController</c> already use for their own
/// job creation, so a scheduled run is indistinguishable in the queue from an
/// operator-initiated one except for its recorded initiator ("scheduled",
/// docs/domain-model.md's Scheduling section) and the schedule's own
/// <see cref="Schedule.CreatedBy"/> attribution, which the API layer (<c>SchedulesController.MapSchedule</c>)
/// carries separately on the wire.
///
/// Never claims, leases, or executes a job -- that stays entirely with the runners per
/// their existing <see cref="JobCapabilities"/> allowlists (migration 0024's atomic
/// claim). This class only ever calls <see cref="IJobControlRepository.CreateRunAsync"/>
/// and <see cref="IJobControlRepository.FanOutJobsAsync"/>, the same two control-plane
/// primitives every other job-creating controller uses.
/// </summary>
public sealed partial class ScheduleDispatchService
{
	/// <summary>Initiator recorded on every scheduled run (docs/domain-model.md Scheduling: "record 'scheduled' as the initiator alongside the schedule's creator").</summary>
	public const string ScheduledInitiator = "scheduled";

	private const short DiscoverPriority = 4;
	private const short CredentialTestPriority = 4;
	private const short CatalogIndexPriority = 1;

	private readonly IScheduleRepository _schedules;
	private readonly IJobControlRepository _jobs;
	private readonly IApplianceStateRepository _applianceState;
	private readonly RunCreationService _runCreation;
	private readonly ILogger<ScheduleDispatchService> _logger;

	public ScheduleDispatchService(
		IScheduleRepository schedules,
		IJobControlRepository jobs,
		IApplianceStateRepository applianceState,
		RunCreationService runCreation,
		ILogger<ScheduleDispatchService> logger)
	{
		ArgumentNullException.ThrowIfNull(schedules);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(applianceState);
		ArgumentNullException.ThrowIfNull(runCreation);
		ArgumentNullException.ThrowIfNull(logger);
		_schedules = schedules;
		_jobs = jobs;
		_applianceState = applianceState;
		_runCreation = runCreation;
		_logger = logger;
	}

	/// <summary>
	/// One sweep: reconciles depot-kind auto-pause against the current appliance mode
	/// (docs/api-contract.md: "auto-paused states in air-gapped mode for depot kinds"),
	/// then dispatches every due, non-paused, enabled schedule. Errors from a single
	/// schedule's dispatch are caught and logged, never allowed to abort the sweep --
	/// the same "one target's failure does not halt the run" discipline CLAUDE.md
	/// requires of scans applies here to one schedule among many.
	/// </summary>
	public async Task SweepAsync(CancellationToken cancellationToken)
	{
		await ReconcileDepotAutoPauseAsync(cancellationToken).ConfigureAwait(false);

		IReadOnlyList<Schedule> due = await _schedules.ListDueAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
		foreach (Schedule schedule in due)
		{
			try
			{
				await DispatchAsync(schedule, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				LogDispatchFailed(schedule.Id, schedule.Name, exception);
			}
		}
	}

	/// <summary>
	/// Auto-pauses every depot-kind (<see cref="ScheduleJobTypes.DepotKinds"/>) schedule
	/// while the appliance is disconnected, and clears that auto-pause the moment it
	/// reconnects -- re-evaluated every sweep so a mode flip taking effect between
	/// sweeps is picked up within one <see cref="ScheduleDispatchOptions.PollInterval"/>,
	/// with no separate event hook needed. Only ever touches the auto-pause reason this
	/// service itself set (<see cref="AutoPauseReason"/>); an operator-set <c>enabled =
	/// false</c> is a different, independent flag and is left untouched either way.
	/// </summary>
	private async Task ReconcileDepotAutoPauseAsync(CancellationToken cancellationToken)
	{
		ApplianceState? state = await _applianceState.GetAsync(cancellationToken).ConfigureAwait(false);
		bool disconnected = string.Equals(state?.Mode, "disconnected", StringComparison.Ordinal);

		IReadOnlyList<Schedule> all = await _schedules.ListAsync(cancellationToken).ConfigureAwait(false);
		foreach (Schedule schedule in all)
		{
			if (!ScheduleJobTypes.DepotKinds.Contains(schedule.JobType))
			{
				continue;
			}

			bool isAutoPaused = string.Equals(schedule.PausedReason, AutoPauseReason, StringComparison.Ordinal);
			if (disconnected && !isAutoPaused && schedule.PausedReason is null)
			{
				await _schedules.SetPausedReasonAsync(schedule.Id, AutoPauseReason, cancellationToken).ConfigureAwait(false);
				LogAutoPaused(schedule.Id, schedule.Name);
			}
			else if (!disconnected && isAutoPaused)
			{
				await _schedules.SetPausedReasonAsync(schedule.Id, null, cancellationToken).ConfigureAwait(false);
				LogAutoResumed(schedule.Id, schedule.Name);
			}
		}
	}

	/// <summary>The <c>paused_reason</c> value this service itself sets -- distinguishes an auto-pause from any future operator-settable reason.</summary>
	public const string AutoPauseReason = "disconnected_mode";

	private async Task DispatchAsync(Schedule schedule, CancellationToken cancellationToken)
	{
		Guid runId;
		if (string.Equals(schedule.JobType, ScheduleJobTypes.Scan, StringComparison.Ordinal))
		{
			runId = await _runCreation.CreateScanRunAsync(
				schedule.ScopeJson, schedule.CredentialId, credential: null, ScheduledInitiator, cancellationToken, schedule.Id)
				.ConfigureAwait(false);
		}
		else
		{
			(short priority, string? targetName) = schedule.JobType switch
			{
				ScheduleJobTypes.Discover => (DiscoverPriority, (string?)null),
				ScheduleJobTypes.CredentialTest => (CredentialTestPriority, null),
				ScheduleJobTypes.CatalogIndex => (CatalogIndexPriority, "depot"),
				_ => throw new InvalidOperationException($"Schedule '{schedule.Id}' has unsupported job_type '{schedule.JobType}'."),
			};

			runId = await _jobs.CreateRunAsync(schedule.JobType, schedule.ScopeJson, schedule.CredentialId, ScheduledInitiator, cancellationToken, schedule.Id)
				.ConfigureAwait(false);
			await _jobs.FanOutJobsAsync(
				runId,
				[new JobSpec(schedule.JobType, priority, TargetName: targetName, CredentialId: schedule.CredentialId)],
				ScheduledInitiator,
				cancellationToken).ConfigureAwait(false);
		}

		CronExpression cron = CronExpression.Parse(schedule.CronExpression);
		DateTimeOffset nextRunAt = cron.GetNextOccurrence(DateTimeOffset.UtcNow);
		await _schedules.MarkDispatchedAsync(schedule.Id, nextRunAt, runId, cancellationToken).ConfigureAwait(false);

		LogDispatched(schedule.Id, schedule.Name, runId, nextRunAt);
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} ({Name}) dispatched run {RunId}; next run at {NextRunAt}")]
	private partial void LogDispatched(Guid scheduleId, string name, Guid runId, DateTimeOffset nextRunAt);

	[LoggerMessage(Level = LogLevel.Error, Message = "Schedule {ScheduleId} ({Name}) failed to dispatch")]
	private partial void LogDispatchFailed(Guid scheduleId, string name, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {ScheduleId} ({Name}) auto-paused: appliance is in disconnected mode")]
	private partial void LogAutoPaused(Guid scheduleId, string name);

	[LoggerMessage(Level = LogLevel.Information, Message = "Schedule {ScheduleId} ({Name}) auto-pause cleared: appliance reconnected")]
	private partial void LogAutoResumed(Guid scheduleId, string name);
}
