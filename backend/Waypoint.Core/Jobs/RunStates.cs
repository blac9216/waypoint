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
/// The exact string values of <c>runs.state</c>, matching <c>runs_state_check</c> in
/// <c>0001_initial_schema.sql</c> verbatim -- this is the closed set. Issue #708's
/// <c>GET /runs/history</c> validates its <c>state</c> filter against this set (400 on
/// an unknown value).
/// </summary>
public static class RunStates
{
	public const string Pending = "pending";
	public const string Running = "running";
	public const string Completed = "completed";
	public const string CompletedWithFailures = "completed_with_failures";
	public const string Aborted = "aborted";

	public static readonly IReadOnlyList<string> All = [Pending, Running, Completed, CompletedWithFailures, Aborted];

	public static bool IsValid(string state) => All.Contains(state, StringComparer.Ordinal);
}

/// <summary>
/// The three <c>runs.state</c> values with no further outgoing transition -- the same
/// set <see cref="Waypoint.Infrastructure.Runs.RunHistoryDeletionService"/> and
/// <c>RunPurgeService</c> already hardcode locally; centralized here so the roll-off
/// sweep (issue #708) and any future caller share one definition instead of a third
/// private copy of the same three strings.
/// </summary>
public static class RunTerminalStates
{
	private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
	{
		RunStates.Completed, RunStates.CompletedWithFailures, RunStates.Aborted,
	};

	public static bool Contains(string state) => Values.Contains(state);

	public static readonly IReadOnlyList<string> All = [RunStates.Completed, RunStates.CompletedWithFailures, RunStates.Aborted];
}
