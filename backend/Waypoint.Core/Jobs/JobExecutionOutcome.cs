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
	AuthFailed
}

/// <summary>The result a job handler reports back to the dispatcher.</summary>
public sealed record JobExecutionOutcome(JobOutcomeKind Kind, string? Note = null)
{
	public static JobExecutionOutcome Succeeded(string? note = null) => new(JobOutcomeKind.Succeeded, note);

	public static JobExecutionOutcome Failed(string? note = null) => new(JobOutcomeKind.Failed, note);

	public static JobExecutionOutcome AuthFailed(string? note = null) => new(JobOutcomeKind.AuthFailed, note);
}
