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

namespace Waypoint.Core.Downloads;

/// <summary>
/// One row of the <c>download_retained_content_state</c> table (migration 0107,
/// issue #1406) -- the per-<see cref="DepotArtifactId"/> retention lifecycle state
/// and pin metadata. Presence-based: a row exists once the (not-yet-built, #1436)
/// sweep first evaluates the artifact; absence means "never evaluated", not "not
/// retained". <see cref="PolicyId"/> null means "resolves to the
/// <see cref="RetentionPolicyScopes.Default"/> scope's policy at evaluation time"
/// rather than a frozen copy.
/// </summary>
public sealed record RetainedContentState(
	Guid Id,
	Guid DepotArtifactId,
	Guid? PolicyId,
	string State,
	DateTimeOffset? GraceStartedAt,
	string? PinnedBy,
	DateTimeOffset? PinnedAt,
	string? PinNote,
	DateTimeOffset? PurgedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>The exact string values of <c>download_retained_content_state.state</c>, matching <c>download_retained_content_state_state_check</c> in migration 0107.</summary>
public static class RetainedContentStates
{
	public const string Tracked = "tracked";
	public const string Grace = "grace";
	public const string Pinned = "pinned";
	public const string PendingPurge = "pending-purge";
	public const string Purged = "purged";
}

/// <summary>
/// The <c>download_retained_content_state.state</c> transition graph (issue #1406
/// Acceptance Criteria: "invalid state transitions... are rejected at the domain
/// layer"), an explicit testable table in the same spirit as
/// <see cref="Waypoint.Core.Jobs.JobStateMachine"/> rather than scattered <c>if</c>
/// checks in the repositories/services that will consume this shape (#1436, #1453).
/// <c>purged</c> is terminal -- no transition out, including back to <c>tracked</c>
/// -- and pinning is only legal from <c>tracked</c> or <c>grace</c> (never on
/// already-<c>purged</c> or already-<c>pending-purge</c> content).
/// </summary>
public static class RetainedContentStateTransitions
{
	private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
	{
		[RetainedContentStates.Tracked] = [RetainedContentStates.Grace, RetainedContentStates.Pinned],
		[RetainedContentStates.Grace] = [RetainedContentStates.Tracked, RetainedContentStates.Pinned, RetainedContentStates.PendingPurge],
		[RetainedContentStates.Pinned] = [RetainedContentStates.Tracked, RetainedContentStates.Grace],
		[RetainedContentStates.PendingPurge] = [RetainedContentStates.Purged, RetainedContentStates.Pinned],
		[RetainedContentStates.Purged] = []
	};

	/// <summary>True if <paramref name="fromState"/> -&gt; <paramref name="toState"/> is a legal transition.</summary>
	public static bool CanTransition(string fromState, string toState)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromState);
		ArgumentException.ThrowIfNullOrWhiteSpace(toState);

		return Transitions.TryGetValue(fromState, out string[]? allowed) && Array.IndexOf(allowed, toState) >= 0;
	}

	/// <summary>The states reachable from <paramref name="fromState"/> in one transition.</summary>
	public static IReadOnlyList<string> AllowedNextStates(string fromState)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromState);

		return Transitions.TryGetValue(fromState, out string[]? allowed) ? allowed : [];
	}

	/// <summary>
	/// True when pinning is legal from <paramref name="fromState"/> -- i.e. the state
	/// is <c>tracked</c> or <c>grace</c>. Content already <c>pending-purge</c> or
	/// <c>purged</c> may never be pinned (issue #1406 AC: "pin on already-purged
	/// content is rejected"); <c>pinned</c> itself is excluded because pinning
	/// already-pinned content is a no-op the caller should treat as idempotent, not a
	/// transition.
	/// </summary>
	public static bool CanPin(string fromState)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromState);

		return string.Equals(fromState, RetainedContentStates.Tracked, StringComparison.Ordinal)
			|| string.Equals(fromState, RetainedContentStates.Grace, StringComparison.Ordinal);
	}
}
