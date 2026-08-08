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

/// <summary>How a job handler's run of <see cref="IJobHandler.ExecuteAsync"/> ended.</summary>
public enum JobOutcomeKind
{
	/// <summary>Reached its shape's terminal success state (<c>uploaded</c> or <c>done</c>).</summary>
	Succeeded,

	/// <summary>Ordinary failure -&gt; <c>failed</c>. Never halts the run (Continue policy, ADR-0008).</summary>
	Failed,

	/// <summary>
	/// Credential rejected by the target -&gt; <c>auth-failed</c>. Counts toward the
	/// consecutive-auth-failure queue halt (ADR-0008, default 3).
	/// </summary>
	AuthFailed,

	/// <summary>
	/// The handler finished one pipeline stage of a multi-stage (<see cref="JobShape.Standard"/>
	/// or <see cref="JobShape.Srg"/>) job and the job should rest, not terminate --
	/// issue #293's stage-per-execution dispatcher. The handler has already called
	/// <see cref="JobExecutionContext.AdvanceAsync"/> to move the row to the
	/// intermediate state it just reached (e.g. <c>running -&gt; attesting</c>); this
	/// outcome tells the dispatcher to requeue the job at <see cref="JobExecutionOutcome.NextStage"/>
	/// (lease cleared, <c>state</c> back to <c>queued</c>) instead of forcing the
	/// shape's terminal success state. A single-stage handler (every handler that
	/// exists today) never returns this -- it always returns <see cref="Succeeded"/> --
	/// so this is zero behavior change for them. Reaching the pipeline's actual last
	/// stage is still reported as <see cref="Succeeded"/>, which still forces the
	/// terminal state exactly as before.
	/// </summary>
	StageComplete
}

/// <summary>
/// The result a job handler reports back to the dispatcher.
/// <see cref="NextStage"/> is only meaningful for <see cref="JobOutcomeKind.StageComplete"/>
/// -- the stage marker (<c>jobs.stage</c>) the dispatcher persists when it requeues the
/// job, and the value <see cref="JobExecutionContext.Stage"/> hands back to whichever
/// handler claims the job next (same handler, another worker, or this one after a
/// resume) so it knows where in the pipeline to resume rather than starting over.
/// </summary>
public sealed record JobExecutionOutcome(JobOutcomeKind Kind, string? Note = null, string? NextStage = null)
{
	public static JobExecutionOutcome Succeeded(string? note = null) => new(JobOutcomeKind.Succeeded, note);

	public static JobExecutionOutcome Failed(string? note = null) => new(JobOutcomeKind.Failed, note);

	public static JobExecutionOutcome AuthFailed(string? note = null) => new(JobOutcomeKind.AuthFailed, note);

	/// <summary><paramref name="nextStage"/> must be a non-terminal stage marker for the job's shape (e.g. <c>attesting</c>, <c>converting</c>) -- see <see cref="JobOutcomeKind.StageComplete"/>.</summary>
	public static JobExecutionOutcome StageComplete(string nextStage, string? note = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nextStage);
		return new(JobOutcomeKind.StageComplete, note, nextStage);
	}
}
