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

using System.Text.Json;

namespace Waypoint.Core.Jobs;

/// <summary>
/// Everything an <see cref="IJobHandler"/> gets handed for one execution, plus
/// <see cref="AdvanceAsync"/> for a <see cref="JobShape.Standard"/> or <see cref="JobShape.Srg"/>
/// handler to report intermediate pipeline stages (<c>running -&gt; attesting -&gt;
/// converting</c>) as it reaches them, each validated against <see cref="JobStateMachine"/>
/// and each emitting its own <c>job.state</c> event. A <see cref="JobShape.Simple"/>
/// handler never needs to call this -- the dispatcher moves it straight from
/// <c>running</c> to its terminal state when <see cref="IJobHandler.ExecuteAsync"/>
/// returns.
///
/// <see cref="Job"/>'s <see cref="ClaimedJob.Stage"/> is the routing input for a
/// multi-stage handler (issue #293): <c>null</c> means "start the pipeline from the
/// first stage"; any other value is the marker a prior <see cref="JobOutcomeKind.StageComplete"/>
/// requeue (or the lease-recovery sweep) left, and the handler must resume its own
/// stage-dispatch switch there rather than re-running earlier stages. This class does
/// not interpret the marker itself -- it is handler-defined, mirroring how
/// <c>job_type</c> and <c>payload</c> are already handler-defined.
/// </summary>
public sealed class JobExecutionContext
{
	private readonly IJobQueueRepository _repository;
	private readonly JobShape _shape;

	public JobExecutionContext(ClaimedJob job, string workerId, IJobEventPublisher events, IJobQueueRepository repository, JobShape shape)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(repository);

		Job = job;
		WorkerId = workerId;
		Events = events;
		_repository = repository;
		_shape = shape;
		CurrentState = JobStates.Running;
	}

	public ClaimedJob Job { get; }

	public string WorkerId { get; }

	public IJobEventPublisher Events { get; }

	/// <summary>The job's state as of the last successful <see cref="AdvanceAsync"/> call (starts at <c>running</c>).</summary>
	public string CurrentState { get; private set; }

	/// <summary>
	/// Moves the job from <see cref="CurrentState"/> to <paramref name="toState"/>,
	/// keeping its lease intact (this is a same-tier move within the active states, not
	/// a completion). Throws if the transition is illegal for the job's shape, or if the
	/// row was concurrently modified (e.g. the run was aborted out from under this
	/// handler) -- callers should let that propagate up to
	/// <see cref="IJobHandler.ExecuteAsync"/>'s caller, which treats an unhandled
	/// exception as a failure.
	/// </summary>
	public async Task AdvanceAsync(string toState, string? note, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(toState);

		if (!JobStateMachine.CanTransition(_shape, CurrentState, toState))
		{
			throw new InvalidOperationException(
				$"Illegal transition '{CurrentState}' -> '{toState}' for job {Job.Id} (shape {_shape}).");
		}

		bool advanced = await _repository
			.AdvanceStateAsync(Job.Id, WorkerId, CurrentState, toState, note, clearLease: false, cancellationToken)
			.ConfigureAwait(false);
		if (!advanced)
		{
			throw new InvalidOperationException(
				$"Job {Job.Id} could not advance to '{toState}': it is no longer '{CurrentState}' owned by this worker (concurrently aborted or lease-recovered).");
		}

		string fromState = CurrentState;
		CurrentState = toState;

		string payload = JsonSerializer.Serialize(new { from = fromState, to = toState, note });
		await Events.EmitAsync(JobEventTypes.JobState, Job.Id, Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}
}
