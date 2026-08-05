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
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>
/// The ADR-0008 dispatcher: claims one job at a time up to
/// <see cref="JobEngineOptions.MaxConcurrency"/>, executes it through the
/// <see cref="JobHandlerRegistry"/>-resolved <see cref="IJobHandler"/>, heartbeats its
/// lease while it runs, and reports the outcome. Run queue flags and aborted state are
/// enforced after claim rather than by changing the proven global claim predicate:
/// paused/blocked claims are released; aborted claims are cancelled. In-flight work
/// observes abort through the same database read in its heartbeat loop.
/// </summary>
public sealed partial class JobDispatcherHostedService : BackgroundService
{
	private static readonly TimeSpan PausedReleaseRetryDelay = TimeSpan.FromMilliseconds(200);
	private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(30);

	private readonly IJobQueueRepository _repository;
	private readonly IJobEventPublisher _events;
	private readonly JobHandlerRegistry _handlers;
	private readonly IOptions<JobEngineOptions> _options;
	private readonly ILogger<JobDispatcherHostedService> _logger;
	private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

	public JobDispatcherHostedService(
		IJobQueueRepository repository,
		IJobEventPublisher events,
		JobHandlerRegistry handlers,
		IOptions<JobEngineOptions> options,
		ILogger<JobDispatcherHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(handlers);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_repository = repository;
		_events = events;
		_handlers = handlers;
		_options = options;
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
		AbortRunResult result = await _repository.AbortRunAsync(runId, cancellationToken).ConfigureAwait(false);
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
	public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken) => _repository.PauseRunAsync(runId, cancellationToken);

	/// <summary>Restores dispatch for a paused non-terminal run.</summary>
	public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken) => _repository.ResumeRunAsync(runId, cancellationToken);

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

		while (!stoppingToken.IsCancellationRequested)
		{
			await gate.WaitAsync(stoppingToken).ConfigureAwait(false);

			ClaimedJob? job;
			try
			{
				job = await _repository.ClaimJobAsync(WorkerId, options.LeaseDuration, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				gate.Release();
				break;
			}
			catch (Exception exception)
			{
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

			if (job.RunId is Guid runId)
			{
				RunQueueState? run = await _repository.GetRunQueueStateAsync(runId, stoppingToken).ConfigureAwait(false);
				if (run is { } state && (state.Paused || state.Blocked || string.Equals(state.State, "aborted", StringComparison.Ordinal)))
				{
					if (string.Equals(state.State, "aborted", StringComparison.Ordinal))
					{
						if (!JobStateMachine.CanEngineTransition(JobShapes.ForJobType(job.JobType), JobStates.Running, JobStates.Cancelled))
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
			JobShape shape = JobShapes.ForJobType(job.JobType);
			JobExecutionContext context = new(job, WorkerId, _events, _repository, shape);

			await _events.EmitAsync(
				JobEventTypes.JobState, job.Id, job.RunId,
				JsonSerializer.Serialize(new { from = JobStates.Queued, to = JobStates.Running }),
				hostToken).ConfigureAwait(false);

			using CancellationTokenSource heartbeatStopCts = new();
			Task heartbeatTask = RunHeartbeatLoopAsync(job, jobCts, heartbeatStopCts.Token);

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
					finalState = outcome.Kind switch
					{
						JobOutcomeKind.Succeeded => shape == JobShape.Standard ? JobStates.Uploaded : JobStates.Done,
						JobOutcomeKind.AuthFailed => JobStates.AuthFailed,
						_ => JobStates.Failed
					};
					finalNote = outcome.Note;
				}
			}
			catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
			{
				finalState = JobStates.Cancelled;
				finalNote = "Cancelled: run aborted";
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
				await heartbeatTask.ConfigureAwait(false);
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
			gate.Release();
		}
	}

	private async Task RunHeartbeatLoopAsync(ClaimedJob job, CancellationTokenSource jobCts, CancellationToken stopToken)
	{
		JobEngineOptions options = _options.Value;
		using PeriodicTimer timer = new(options.HeartbeatIntervalOrDefault);

		try
		{
			while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false))
			{
				bool renewed = await _repository.RenewLeaseAsync(job.Id, WorkerId, options.LeaseDuration, stopToken).ConfigureAwait(false);
				if (!renewed)
				{
					LogHeartbeatLostOwnership(job.Id);
					return;
				}

				if (job.RunId is Guid runId)
				{
					RunQueueState? runState = await _repository.GetRunQueueStateAsync(runId, stopToken).ConfigureAwait(false);
					if (string.Equals(runState?.State, "aborted", StringComparison.Ordinal))
					{
						LogHeartbeatObservedAbort(job.Id, runId);
						await jobCts.CancelAsync().ConfigureAwait(false);
						return;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Expected: either the job finished (heartbeatStopCts) or the host is stopping.
		}
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
		if (halt.BlockedRunIds.Count == 0 && halt.BlockedJobIds.Count == 0)
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

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: heartbeat observed run {RunId} aborted; cancelling")]
	private partial void LogHeartbeatObservedAbort(Guid jobId, Guid runId);

	[LoggerMessage(Level = LogLevel.Error, Message = "Credential {CredentialId} queue halted: {Threshold} consecutive auth failures, {BlockedRunCount} run(s), {BlockedJobCount} job(s) blocked")]
	private partial void LogQueueHalted(Guid credentialId, int threshold, int blockedRunCount, int blockedJobCount);

}
