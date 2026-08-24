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
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Runner.Resources;

namespace Waypoint.Runner.Jobs;

/// <summary>
/// The ADR-0008 dispatcher: claims one job at a time up to
/// <see cref="JobEngineOptions.MaxConcurrency"/>, executes it through the
/// <see cref="JobHandlerRegistry"/>-resolved <see cref="IJobHandler"/>, heartbeats its
/// lease while it runs, and reports the outcome. Run queue flags and aborted state are
/// enforced after claim rather than by changing the proven global claim predicate:
/// paused/blocked claims are released; aborted claims are cancelled. In-flight work
/// observes abort through the same database read in its heartbeat loop.
///
/// <para>
/// Issue #437 (ADR-0014 §5): <see cref="JobEngineOptions.MaxConcurrency"/> remains a
/// coarse worker-slot ceiling (the <c>SemaphoreSlim</c> below), but every claim is now
/// also subject to <see cref="ResourceAdmissionController.TryAdmit"/> -- the finer
/// CPU/memory budget derived from cgroup discovery and operator caps. The resource
/// check runs immediately after a successful claim, before the job is handed to a
/// handler: <c>ClaimJobAsync</c>'s <c>job_type</c> is not knowable until the row is
/// actually claimed (the atomic <c>SKIP LOCKED</c> statement has no cheap peek-ahead),
/// so "decide admission before claiming" is honored as "decide admission before
/// executing" -- a claim the resource budget cannot admit is immediately released back
/// to <c>queued</c> via the same <see cref="IJobRunnerRepository.ReleaseClaimAsync"/>
/// path already used for a paused/blocked run's claim, rather than being executed
/// over-budget or lost. <see cref="_resourceAdmission"/> is optional: a host that does
/// not register one (see <c>ResourceAdmissionOptions.Enabled</c> at the composition
/// root) keeps exactly today's <see cref="JobEngineOptions.MaxConcurrency"/>-only
/// behavior.
/// </para>
/// </summary>
public sealed partial class JobDispatcherHostedService : BackgroundService
{
	// internal (not private): issue #428's test needs the real backoff value rather than
	// a duplicated magic number, so it can wait out a deterministic number of claim/
	// release cycles instead of racing the transient queued->running->queued window.
	internal static readonly TimeSpan PausedReleaseRetryDelay = TimeSpan.FromMilliseconds(200);
	private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(30);

	// Issue #637: bounds how many CONSECUTIVE heartbeat-tick faults are tolerated
	// before the loop gives up on this job rather than retrying forever. 3 mirrors the
	// same "a few misses are noise, more is a real problem" judgment call already made
	// for ConsecutiveAuthFailureThreshold's default -- a healthy DB blip resolves in
	// well under 3 heartbeat intervals, but a persistently unreachable database should
	// stop pretending renewal might still succeed and let lease-recovery reclaim the
	// job through its normal expiry path instead of heartbeating into the void forever.
	internal const int MaxConsecutiveHeartbeatTickFailures = 3;

	// Issue #654: dedicated backoff for the boot-only 42501 race, deliberately
	// separate from JobEngineOptions.PollInterval (the steady-state empty-queue
	// cadence) -- mirrors #634's capacity-pool registration retry shape (short initial
	// delay, capped doubling) since this is the same "schema not applied yet" class.
	internal static readonly TimeSpan BootClaimRetryInitialDelay = TimeSpan.FromMilliseconds(500);
	internal static readonly TimeSpan BootClaimRetryMaxDelay = TimeSpan.FromSeconds(10);

	// Issue #415: the dispatcher is today's stand-in for the future dedicated runner
	// process (ADR-0013/0014), so it is the one caller in this codebase that
	// legitimately needs both focused interfaces -- _repository for every
	// claim/lease/state/recovery operation ADR-0014 assigns to the runner, and
	// _controlRepository strictly for this type's own AbortRunAsync/PauseRunAsync/
	// ResumeRunAsync pass-throughs (retained for API parity until those move behind an
	// actual runner-control channel). Both parameters are satisfied by the same
	// concrete instance in every current caller (DI wires one JobQueueRepository
	// singleton under both interfaces; tests pass one fake/repository object twice).
	private readonly IJobRunnerRepository _repository;
	private readonly IJobControlRepository _controlRepository;
	private readonly IJobEventPublisher _events;
	private readonly JobHandlerRegistry _handlers;
	private readonly IOptions<JobEngineOptions> _options;
	private readonly ResourceAdmissionController? _resourceAdmission;
	private readonly Resources.CapacityLeaseCoordinator? _capacityPool;
	private readonly ILogger<JobDispatcherHostedService> _logger;
	private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

	public JobDispatcherHostedService(
		IJobRunnerRepository repository,
		IJobControlRepository controlRepository,
		IJobEventPublisher events,
		JobHandlerRegistry handlers,
		IOptions<JobEngineOptions> options,
		ILogger<JobDispatcherHostedService> logger)
		: this(repository, controlRepository, events, handlers, options, resourceAdmission: null, capacityPool: null, logger)
	{
	}

	/// <summary>Issue #437: the resource-admission-aware overload. <paramref name="resourceAdmission"/> is null for a host that opts out of resource-aware admission (see this type's remarks).</summary>
	public JobDispatcherHostedService(
		IJobRunnerRepository repository,
		IJobControlRepository controlRepository,
		IJobEventPublisher events,
		JobHandlerRegistry handlers,
		IOptions<JobEngineOptions> options,
		ResourceAdmissionController? resourceAdmission,
		ILogger<JobDispatcherHostedService> logger)
		: this(repository, controlRepository, events, handlers, options, resourceAdmission, capacityPool: null, logger)
	{
	}

	/// <summary>
	/// Issue #569 (ADR-0020): the shared-capacity-pool-aware overload.
	/// <paramref name="capacityPool"/> is null when the pool is disabled
	/// (<c>CapacityPool:Enabled=false</c>), preserving ADR-0014 §5's per-runner-only
	/// admission exactly as before #569.
	/// </summary>
	public JobDispatcherHostedService(
		IJobRunnerRepository repository,
		IJobControlRepository controlRepository,
		IJobEventPublisher events,
		JobHandlerRegistry handlers,
		IOptions<JobEngineOptions> options,
		ResourceAdmissionController? resourceAdmission,
		Resources.CapacityLeaseCoordinator? capacityPool,
		ILogger<JobDispatcherHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(controlRepository);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(handlers);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_repository = repository;
		_controlRepository = controlRepository;
		_events = events;
		_handlers = handlers;
		_options = options;
		_resourceAdmission = resourceAdmission;
		_capacityPool = capacityPool;
		_logger = logger;

		// jobs.claimed_by is unconstrained TEXT (see 0001_initial_schema.sql), so there
		// is no length budget to cap this at -- earlier revisions truncated with a fixed
		// [..48], which threw ArgumentOutOfRangeException whenever MachineName + the
		// process id happened to be short enough for the whole interpolated string to
		// come in under 48 characters (observed with a short container hostname).
		WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
	}

	/// <summary>This process's claimant identity, stamped into <c>jobs.claimed_by</c>.</summary>
	public string WorkerId { get; }


	/// <summary>Aborts a run and immediately cancels work owned by this dispatcher; other workers observe the database state through heartbeat.</summary>
	public async Task AbortRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		AbortRunResult result = await _controlRepository.AbortRunAsync(runId, cancellationToken).ConfigureAwait(false);
		foreach (Guid jobId in result.InFlightJobIds)
		{
			if (_inFlight.TryGetValue(jobId, out CancellationTokenSource? cts))
			{
				await cts.CancelAsync().ConfigureAwait(false);
			}
		}

		if (result.CancelledJobIds.Count > 0 || result.InFlightJobIds.Count > 0)
		{
			string payload = JsonSerializer.Serialize(new
			{
				aborted = true,
				cancelled_job_count = result.CancelledJobIds.Count,
				in_flight_job_count = result.InFlightJobIds.Count
			});
			await _events.EmitAsync(JobEventTypes.RunProgress, null, runId, payload, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Stops new dispatch for a run while allowing in-flight jobs to finish.</summary>
	public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken) => _controlRepository.PauseRunAsync(runId, cancellationToken);

	/// <summary>Restores dispatch for a paused non-terminal run.</summary>
	public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken) => _controlRepository.ResumeRunAsync(runId, cancellationToken);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		JobEngineOptions options = _options.Value;
		if (!options.Enabled)
		{
			LogDispatcherDisabled();
			return;
		}

		LogDispatcherStarting(WorkerId, options.MaxConcurrency);

		using SemaphoreSlim gate = new(options.MaxConcurrency, options.MaxConcurrency);
		object tasksLock = new();
		List<Task> inFlightTasks = [];

		// Issue #654: whether any claim attempt has ever succeeded (found a job or
		// legitimately found none -- anything that is not the boot-race exception
		// below). Only gates which log level/backoff the FIRST 42501 gets; every other
		// exception, and every 42501 after the schema is known ready, is unaffected.
		bool schemaObservedReady = false;
		TimeSpan bootRaceRetryDelay = BootClaimRetryInitialDelay;

		while (!stoppingToken.IsCancellationRequested)
		{
			await gate.WaitAsync(stoppingToken).ConfigureAwait(false);

			ClaimedJob? job;
			try
			{
				// Issue #436: the allowlist comes from the mandatory capability
				// registration on JobHandlerRegistry (fail-closed at construction --
				// see its doc comment), never a value this loop chooses itself. Today's
				// still-unsplit host passes JobCapabilities.All; a split
				// compliance-runner/download-runner passes its own narrower set the
				// same way, through the same registry.
				job = await _repository.ClaimJobAsync(WorkerId, options.LeaseDuration, _handlers.AllowedJobTypes, stoppingToken).ConfigureAwait(false);
				schemaObservedReady = true;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				gate.Release();
				break;
			}
			catch (PostgresException exception) when (!schemaObservedReady && exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
			{
				// Issue #654: on a fresh stack, this runner's very first ClaimJobAsync can
				// race the backend's migration/grant application -- the jobs table (or its
				// runner-role grants) is not there yet, so Postgres answers 42501. Same
				// benign startup-race class as #633 (capacity-pool registration losing the
				// race against a not-yet-created table). The claim loop already retries
				// unconditionally on any exception (see the catch below), so this is
				// already self-healing with zero operator action -- what was missing is
				// that it logged at Error every time, which reads as a real problem on a
				// clean boot. Gated to ONLY the pre-first-success window and ONLY this
				// specific SQLSTATE: log quietly here and back off on a short dedicated
				// cadence (capped, doubling) instead of the general-purpose Error log +
				// PollInterval used for every other claim failure.
				gate.Release();
				LogBootClaimRace(exception, bootRaceRetryDelay);
				await DelayAsync(bootRaceRetryDelay, stoppingToken).ConfigureAwait(false);
				TimeSpan doubled = bootRaceRetryDelay * 2;
				bootRaceRetryDelay = doubled > BootClaimRetryMaxDelay ? BootClaimRetryMaxDelay : doubled;
				continue;
			}
			catch (Exception exception)
			{
				schemaObservedReady = true;
				gate.Release();
				LogClaimFailed(exception);
				await DelayAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
				continue;
			}

			if (job is null)
			{
				gate.Release();
				await DelayAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
				continue;
			}

			// Issue #437: resource-aware admission runs immediately after claim, before
			// this job is handed to a handler or even checked against run pause/abort
			// state -- see this type's doc comment for why "before claiming" becomes
			// "before executing" given ClaimJobAsync's atomic SKIP LOCKED statement has
			// no job-type peek-ahead. A denied claim is released back to queued exactly
			// like the paused/blocked-run release path below, so the job is neither
			// executed over-budget nor lost; another worker (or this one, once running
			// jobs free up budget) picks it up on a later poll.
			if (_resourceAdmission is not null && !_resourceAdmission.TryAdmit(job.Id, job.JobType))
			{
				await _repository.ReleaseClaimAsync(job.Id, WorkerId, stoppingToken).ConfigureAwait(false);
				gate.Release();
				await DelayAsync(PausedReleaseRetryDelay, stoppingToken).ConfigureAwait(false);
				continue;
			}

			// Issue #569 (ADR-0018 §5 / ADR-0020): the shared pool claim runs strictly
			// AFTER local admission, so this runner's own discovered/capped budget
			// stays the authoritative upper bound on what it may claim from the pool.
			// A pool denial -- including any database failure, which the coordinator
			// answers with false rather than overcommitting -- releases the claim back
			// to queued exactly like a local denial.
			if (_capacityPool is not null && !await _capacityPool.TryAcquireAsync(job.Id, job.JobType, WorkerId, stoppingToken).ConfigureAwait(false))
			{
				_resourceAdmission?.Release(job.Id);
				await _repository.ReleaseClaimAsync(job.Id, WorkerId, stoppingToken).ConfigureAwait(false);
				gate.Release();
				await DelayAsync(PausedReleaseRetryDelay, stoppingToken).ConfigureAwait(false);
				continue;
			}

			if (job.RunId is Guid runId)
			{
				RunQueueState? run = await _repository.GetRunQueueStateAsync(runId, stoppingToken).ConfigureAwait(false);
				if (run is { } state && (state.Paused || state.Blocked || string.Equals(state.State, "aborted", StringComparison.Ordinal)))
				{
					if (string.Equals(state.State, "aborted", StringComparison.Ordinal))
					{
						if (!JobStateMachine.CanEngineTransition(JobShapes.ForJob(job.JobType, job.Payload), JobStates.Running, JobStates.Cancelled))
						{
							throw new InvalidOperationException("The engine transition gate rejects abort cancellation.");
						}

						bool cancelled = await _repository.AdvanceStateAsync(job.Id, WorkerId, JobStates.Running, JobStates.Cancelled, "Cancelled: run aborted", clearLease: true, stoppingToken).ConfigureAwait(false);
						if (cancelled)
						{
							await _events.EmitAsync(JobEventTypes.JobState, job.Id, job.RunId,
								JsonSerializer.Serialize(new { from = JobStates.Running, to = JobStates.Cancelled, note = "Cancelled: run aborted" }), stoppingToken).ConfigureAwait(false);
						}
					}
					else
					{
						await _repository.ReleaseClaimAsync(job.Id, WorkerId, stoppingToken).ConfigureAwait(false);
					}
					_resourceAdmission?.Release(job.Id);
					if (_capacityPool is not null)
					{
						await _capacityPool.ReleaseAsync(job.Id, stoppingToken).ConfigureAwait(false);
					}
					gate.Release();
					await DelayAsync(PausedReleaseRetryDelay, stoppingToken).ConfigureAwait(false);
					continue;
				}
			}

			Task runTask = RunJobAsync(job, gate, stoppingToken);
			lock (tasksLock)
			{
				inFlightTasks.RemoveAll(task => task.IsCompleted);
				inFlightTasks.Add(runTask);
			}
		}

		Task[] remaining;
		lock (tasksLock)
		{
			remaining = [.. inFlightTasks.Where(task => !task.IsCompleted)];
		}

		if (remaining.Length > 0)
		{
			LogAwaitingInFlight(remaining.Length);

			// Deliberately CancellationToken.None: stoppingToken is already cancelled by
			// the time we reach here (that's what ended the dispatch loop above), so a
			// token-bound Delay would return instantly and skip the shutdown grace
			// period entirely.
			await Task.WhenAny(Task.WhenAll(remaining), Task.Delay(ShutdownGracePeriod, CancellationToken.None)).ConfigureAwait(false);
		}
	}


	private async Task RunJobAsync(ClaimedJob job, SemaphoreSlim gate, CancellationToken hostToken)
	{
		using CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
		_inFlight[job.Id] = jobCts;

		try
		{
			JobShape shape = JobShapes.ForJob(job.JobType, job.Payload);
			JobExecutionContext context = new(job, WorkerId, _events, _repository, shape);

			await _events.EmitAsync(
				JobEventTypes.JobState, job.Id, job.RunId,
				JsonSerializer.Serialize(new { from = JobStates.Queued, to = JobStates.Running }),
				hostToken).ConfigureAwait(false);

			using CancellationTokenSource heartbeatStopCts = new();
			CancelReasonBox cancelReason = new();
			Task heartbeatTask = RunHeartbeatLoopAsync(job, jobCts, cancelReason, heartbeatStopCts.Token);

			// StageComplete (issue #293) is not a terminal outcome, so it is handled
			// entirely separately from the finalState/AdvanceStateAsync path below: the
			// handler has already left the row at a legal non-terminal state via
			// AdvanceAsync, and this outcome means "requeue it there" rather than "force
			// the shape's terminal success state". stageOutcome is null for every other
			// path (no handler, exception, cancellation, or a terminal outcome kind).
			JobExecutionOutcome? stageOutcome = null;
			string finalState;
			string? finalNote;

			try
			{
				if (!_handlers.TryResolve(job.JobType, out IJobHandler? handler) || handler is null)
				{
					finalState = JobStates.Failed;
					finalNote = $"No handler registered for job type '{job.JobType}'.";
					LogNoHandler(job.Id, job.JobType);
				}
				else
				{
					JobExecutionOutcome outcome = await handler.ExecuteAsync(context, jobCts.Token).ConfigureAwait(false);
					if (outcome.Kind == JobOutcomeKind.StageComplete)
					{
						stageOutcome = outcome;
						finalState = context.CurrentState;
						finalNote = outcome.Note;
					}
					else
					{
						finalState = outcome.Kind switch
						{
							JobOutcomeKind.Succeeded => shape == JobShape.Standard ? JobStates.Uploaded : JobStates.Done,
							JobOutcomeKind.AuthFailed => JobStates.AuthFailed,
							_ => JobStates.Failed
						};
						finalNote = outcome.Note;
					}
				}
			}
			catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
			{
				// Two independent cooperative-cancel signals feed the same jobCts: the
				// run-scoped abort check (pre-existing) and the per-job cancel_requested
				// flag (issue #234). cancelReason records which one actually fired this
				// tick so the note -- and therefore the audit trail -- says which.
				finalState = JobStates.Cancelled;
				finalNote = cancelReason.Value ?? "Cancelled: run aborted";
			}
			catch (Exception exception)
			{
				finalState = JobStates.Failed;
				finalNote = $"Unhandled exception: {exception.Message}";
				LogHandlerThrew(job.Id, job.JobType, exception);
			}
			finally
			{
				await heartbeatStopCts.CancelAsync().ConfigureAwait(false);

				// Issue #631: the heartbeat is best-effort lease renewal plus abort/
				// cancel observation -- NEVER the record of job/run state. A transient DB
				// fault on any of its per-tick calls (RenewLeaseAsync,
				// GetRunQueueStateAsync, IsCancelRequestedAsync -- the capacity
				// coordinator already swallows its own) faults this Task, and awaiting a
				// faulted Task rethrows. If that throw escaped this finally it would
				// propagate PAST the AdvanceStateAsync completion write below, discarding
				// the handler's real terminal outcome: the job would sit at 'running'
				// until its (now un-renewed) lease expired and lease-recovery reclaimed
				// and retried it -- the exact spurious-retry hang this issue reports, and
				// dangerous for non-idempotent work like tool-install. So a faulted
				// heartbeat is logged and swallowed here; the completion path proceeds.
				try
				{
					await heartbeatTask.ConfigureAwait(false);
				}
				catch (Exception exception)
				{
					LogHeartbeatFaulted(job.Id, exception);
				}
			}

			if (stageOutcome is not null)
			{
				await RequeueAtStageAsync(job, context, stageOutcome, hostToken).ConfigureAwait(false);
				return;
			}

			if (!JobStateMachine.CanTransition(shape, context.CurrentState, finalState))
			{
				LogIllegalFinalTransition(job.Id, context.CurrentState, finalState);
				finalNote = $"{finalNote} (rejected illegal transition {context.CurrentState}->{finalState})";
				finalState = JobStates.Failed;
			}

			bool advanced = await _repository
				.AdvanceStateAsync(job.Id, WorkerId, context.CurrentState, finalState, finalNote, clearLease: true, hostToken)
				.ConfigureAwait(false);

			if (!advanced)
			{
				LogCompletionLost(job.Id, context.CurrentState, finalState);
				return;
			}

			await _events.EmitAsync(
				JobEventTypes.JobState, job.Id, job.RunId,
				JsonSerializer.Serialize(new { from = context.CurrentState, to = finalState, note = finalNote }),
				hostToken).ConfigureAwait(false);

			if (string.Equals(finalState, JobStates.AuthFailed, StringComparison.Ordinal) && job.CredentialId is Guid credentialId)
			{
				await HandleAuthFailureAsync(credentialId, hostToken).ConfigureAwait(false);
			}

		}
		finally
		{
			_inFlight.TryRemove(job.Id, out _);
			_resourceAdmission?.Release(job.Id);
			if (_capacityPool is not null)
			{
				// CancellationToken.None: this release must run even during host
				// shutdown (hostToken already cancelled) -- otherwise the pool holds
				// this job's slice until the lease expires. The coordinator swallows
				// database failures (the reaper reclaims via expiry).
				await _capacityPool.ReleaseAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
			}
			gate.Release();
		}
	}

	/// <summary>
	/// Issue #293: requeues a job that reported <see cref="JobOutcomeKind.StageComplete"/>
	/// -- the handler already advanced <paramref name="context"/> to a legal non-terminal
	/// state via <see cref="JobExecutionContext.AdvanceAsync"/> (e.g. <c>running -&gt;
	/// attesting</c>); this moves the row the rest of the way back to <c>queued</c> with
	/// the lease cleared and <c>jobs.stage</c> set to <see cref="JobExecutionOutcome.NextStage"/>,
	/// so the next claim of this job (this worker or another) hands that marker back via
	/// <see cref="ClaimedJob.Stage"/>. A lost race (row concurrently aborted/recovered) is
	/// logged and swallowed exactly like the terminal completion path's own lost-race
	/// case -- there is nothing left for this worker to do about a row it no longer owns.
	/// </summary>
	private async Task RequeueAtStageAsync(ClaimedJob job, JobExecutionContext context, JobExecutionOutcome stageOutcome, CancellationToken hostToken)
	{
		string nextStage = stageOutcome.NextStage
			?? throw new InvalidOperationException($"Job {job.Id}: StageComplete outcome carried no NextStage.");

		bool requeued = await _repository
			.RequeueAtStageAsync(job.Id, WorkerId, context.CurrentState, nextStage, stageOutcome.Note, hostToken)
			.ConfigureAwait(false);

		if (!requeued)
		{
			LogCompletionLost(job.Id, context.CurrentState, JobStates.Queued);
			return;
		}

		await _events.EmitAsync(
			JobEventTypes.JobState, job.Id, job.RunId,
			JsonSerializer.Serialize(new { from = context.CurrentState, to = JobStates.Queued, stage = nextStage, note = stageOutcome.Note }),
			hostToken).ConfigureAwait(false);
	}

	private async Task RunHeartbeatLoopAsync(ClaimedJob job, CancellationTokenSource jobCts, CancelReasonBox cancelReason, CancellationToken stopToken)
	{
		using PeriodicTimer timer = new(_options.Value.HeartbeatIntervalOrDefault);
		int consecutiveTickFailures = 0;

		try
		{
			while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false))
			{
				bool tickSucceeded;
				try
				{
					tickSucceeded = await RunHeartbeatTickAsync(job, jobCts, cancelReason, stopToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Expected: either the job finished (heartbeatStopCts) or the host is
					// stopping. Rethrow so the outer catch below ends the loop the same
					// way it always has.
					throw;
				}
				catch (Exception exception)
				{
					// Issue #637: a single transient per-tick fault (DB blip, timeout on
					// RenewLeaseAsync/GetRunQueueStateAsync/IsCancelRequestedAsync -- the
					// capacity coordinator already swallows its own) must not end the loop
					// for the rest of the job. Before this fix, any such exception escaped
					// the loop entirely: lease renewal stopped for good, the job's lease
					// eventually expired mid-run, lease-recovery requeued the still-
					// executing job, and a second runner could start a concurrent
					// execution of the same (possibly non-idempotent) work. Logging and
					// retrying on the next tick keeps renewal and abort/cancel observation
					// alive across a blip. consecutiveTickFailures distinguishes that from
					// a persistently unreachable database: once it reaches the bound, this
					// is no longer "ownership might still be fine", so the loop gives up
					// exactly like the pre-existing !renewed / lost-ownership path already
					// did on a clean signal.
					consecutiveTickFailures++;
					LogHeartbeatTickFaulted(job.Id, consecutiveTickFailures, MaxConsecutiveHeartbeatTickFailures, exception);

					if (consecutiveTickFailures >= MaxConsecutiveHeartbeatTickFailures)
					{
						LogHeartbeatGivingUpAfterRepeatedFaults(job.Id, consecutiveTickFailures);
						return;
					}

					continue;
				}

				if (!tickSucceeded)
				{
					// A clean (non-exceptional) signal: lease ownership genuinely lost, or
					// abort/cancel observed and already handed to jobCts. Either way the
					// loop's job here is done.
					return;
				}

				consecutiveTickFailures = 0;
			}
		}
		catch (OperationCanceledException)
		{
			// Expected: either the job finished (heartbeatStopCts) or the host is stopping.
		}
	}

	/// <summary>
	/// One heartbeat tick's body: renew the job lease (and capacity lease), then check
	/// run-abort and per-job cancel_requested. Returns <c>false</c> when the loop should
	/// stop cleanly (ownership lost, or a cancel signal was just raised on
	/// <paramref name="jobCts"/>); returns <c>true</c> to continue to the next tick. Any
	/// exception from a per-tick call propagates to the caller, which treats it as a
	/// transient fault (issue #637) rather than a reason to stop.
	/// </summary>
	private async Task<bool> RunHeartbeatTickAsync(ClaimedJob job, CancellationTokenSource jobCts, CancelReasonBox cancelReason, CancellationToken stopToken)
	{
		bool renewed = await _repository.RenewLeaseAsync(job.Id, WorkerId, _options.Value.LeaseDuration, stopToken).ConfigureAwait(false);
		if (!renewed)
		{
			LogHeartbeatLostOwnership(job.Id);
			return false;
		}

		// Issue #569 (ADR-0020): the capacity lease renews on the same clock as
		// the job lease, so a worker that stops heartbeating loses both
		// together and the reaper's expiry semantics stay consistent with
		// job-lease recovery. The coordinator handles lost/failed renewal
		// itself (re-claim or log-and-retry) -- never by cancelling the job.
		if (_capacityPool is not null)
		{
			await _capacityPool.RenewAsync(job.Id, job.JobType, WorkerId, stopToken).ConfigureAwait(false);
		}

		if (job.RunId is Guid runId)
		{
			RunQueueState? runState = await _repository.GetRunQueueStateAsync(runId, stopToken).ConfigureAwait(false);
			if (string.Equals(runState?.State, "aborted", StringComparison.Ordinal))
			{
				LogHeartbeatObservedAbort(job.Id, runId);
				cancelReason.Value = "Cancelled: run aborted";
				await jobCts.CancelAsync().ConfigureAwait(false);
				return false;
			}
		}

		// Per-job cooperative cancel (issue #234): a running job's own
		// cancel_requested flag, set by CancelJobAsync (e.g. DELETE
		// /downloads/{id}) independently of any run-scoped abort.
		if (await _repository.IsCancelRequestedAsync(job.Id, stopToken).ConfigureAwait(false))
		{
			LogHeartbeatObservedCancelRequest(job.Id);
			cancelReason.Value = "Cancelled by request";
			await jobCts.CancelAsync().ConfigureAwait(false);
			return false;
		}

		return true;
	}

	/// <summary>
	/// Carries which cooperative-cancel signal (run-scoped abort vs. per-job
	/// cancel_requested) actually fired, from <see cref="RunHeartbeatLoopAsync"/> back to
	/// <see cref="RunJobAsync"/>'s catch block, so the terminal note names the real cause.
	/// A plain mutable holder rather than a return value because the heartbeat task is
	/// fire-and-forget until <see cref="RunJobAsync"/> awaits it in its <c>finally</c>.
	/// </summary>
	private sealed class CancelReasonBox
	{
		public string? Value { get; set; }
	}


	private async Task HandleAuthFailureAsync(Guid credentialId, CancellationToken cancellationToken)
	{
		JobEngineOptions options = _options.Value;
		AuthFailureHaltResult halt = await _repository
			.CheckConsecutiveAuthFailuresAsync(credentialId, options.ConsecutiveAuthFailureThreshold, cancellationToken)
			.ConfigureAwait(false);

		// #147: the halt is a state change worth announcing even when it blocked
		// nothing (no rows were queued at that instant) -- HaltTripped, not the
		// blocked counts, is the emission condition.
		if (!halt.HaltTripped)
		{
			return;
		}

		int blockedRunCount = halt.BlockedRunIds.Count;
		int blockedJobCount = halt.BlockedJobIds.Count;
		LogQueueHalted(credentialId, options.ConsecutiveAuthFailureThreshold, blockedRunCount, blockedJobCount);

		string payload = JsonSerializer.Serialize(new
		{
			blocked = true,
			credential_id = credentialId,
			threshold = options.ConsecutiveAuthFailureThreshold,
			blocked_job_count = blockedJobCount
		});

		foreach (Guid runId in halt.BlockedRunIds)
		{
			await _events.EmitAsync(JobEventTypes.QueueState, null, runId, payload, cancellationToken).ConfigureAwait(false);
		}

		await _events.EmitAsync(JobEventTypes.SystemNotice, null, null, payload, cancellationToken).ConfigureAwait(false);
	}

	private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Host stopping; the outer loop's while-condition ends the loop next.
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Job dispatcher disabled (JobEngine:Enabled=false)")]
	private partial void LogDispatcherDisabled();

	[LoggerMessage(Level = LogLevel.Information, Message = "Job dispatcher {WorkerId} starting, max concurrency {MaxConcurrency}")]
	private partial void LogDispatcherStarting(string workerId, int maxConcurrency);

	[LoggerMessage(Level = LogLevel.Error, Message = "Claim attempt failed; backing off")]
	private partial void LogClaimFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Information, Message = "First claim attempt hit 42501 (permission denied), likely racing migration/grant application at boot; retrying in {RetryDelay}")]
	private partial void LogBootClaimRace(Exception exception, TimeSpan retryDelay);

	[LoggerMessage(Level = LogLevel.Information, Message = "Awaiting {Count} in-flight job(s) before shutdown")]
	private partial void LogAwaitingInFlight(int count);

	[LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId}: no handler registered for job type '{JobType}'")]
	private partial void LogNoHandler(Guid jobId, string jobType);

	[LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId} ({JobType}) handler threw")]
	private partial void LogHandlerThrew(Guid jobId, string jobType, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: handler reported illegal transition {FromState}->{ToState}; forced to failed")]
	private partial void LogIllegalFinalTransition(Guid jobId, string fromState, string toState);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: completion from {FromState} to {ToState} lost the race (concurrently modified)")]
	private partial void LogCompletionLost(Guid jobId, string fromState, string toState);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: heartbeat could not renew lease; ownership lost (likely lease-recovered)")]
	private partial void LogHeartbeatLostOwnership(Guid jobId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: heartbeat tick failed ({ConsecutiveFailures}/{MaxConsecutiveFailures} consecutive); retrying next tick")]
	private partial void LogHeartbeatTickFaulted(Guid jobId, int consecutiveFailures, int maxConsecutiveFailures, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId}: heartbeat giving up after {ConsecutiveFailures} consecutive tick faults; lease renewal and abort/cancel observation stopped for this job")]
	private partial void LogHeartbeatGivingUpAfterRepeatedFaults(Guid jobId, int consecutiveFailures);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: heartbeat loop faulted; swallowed so the terminal completion write is not skipped (issue #631)")]
	private partial void LogHeartbeatFaulted(Guid jobId, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: heartbeat observed run {RunId} aborted; cancelling")]
	private partial void LogHeartbeatObservedAbort(Guid jobId, Guid runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: heartbeat observed cancel_requested; cancelling")]
	private partial void LogHeartbeatObservedCancelRequest(Guid jobId);

	[LoggerMessage(Level = LogLevel.Error, Message = "Credential {CredentialId} queue halted: {Threshold} consecutive auth failures, {BlockedRunCount} run(s), {BlockedJobCount} job(s) blocked")]
	private partial void LogQueueHalted(Guid credentialId, int threshold, int blockedRunCount, int blockedJobCount);

}
