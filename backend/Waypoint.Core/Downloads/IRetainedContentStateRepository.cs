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
/// Storage for the <c>download_retained_content_state</c> table (migration 0107,
/// issue #1406). One implementation
/// (<c>Waypoint.Infrastructure.Downloads.RetainedContentStateRepository</c>, plain
/// Npgsql). Every mutating method validates the transition against
/// <see cref="RetainedContentStateTransitions"/> before writing and throws
/// <see cref="InvalidOperationException"/> on an illegal one -- callers (the future
/// #1436 sweep, #1453 API) never need to re-implement the state graph themselves.
/// </summary>
public interface IRetainedContentStateRepository
{
	/// <summary>
	/// Creates the initial <see cref="RetainedContentStates.Tracked"/> row for an
	/// artifact that has never been evaluated before (presence-based: see
	/// <see cref="RetainedContentState"/>'s doc comment). Idempotent on
	/// <paramref name="depotArtifactId"/> -- returns the existing row's id if one
	/// already exists rather than throwing, and repeat calls with a null or
	/// already-matching <paramref name="policyId"/> touch nothing (no write, no
	/// <c>updated_at</c> bump -- genuinely a no-op, not merely non-throwing). Passing
	/// a non-null <paramref name="policyId"/> that differs from the existing row's is
	/// an explicit adopt-the-new-value update, not silently discarded -- the row's
	/// <c>policy_id</c> is overwritten and <c>updated_at</c> moves. This is the path
	/// the future #1436 sweep uses when it re-evaluates an already-tracked artifact
	/// against a freshly resolved policy.
	/// </summary>
	Task<Guid> EnsureTrackedAsync(Guid depotArtifactId, Guid? policyId, CancellationToken cancellationToken);

	Task<RetainedContentState?> GetAsync(Guid id, CancellationToken cancellationToken);

	Task<RetainedContentState?> GetByDepotArtifactIdAsync(Guid depotArtifactId, CancellationToken cancellationToken);

	/// <summary>
	/// Moves <paramref name="id"/> to <paramref name="toState"/>. Throws
	/// <see cref="InvalidOperationException"/> when
	/// <see cref="RetainedContentStateTransitions.CanTransition"/> rejects the current
	/// row's state -&gt; <paramref name="toState"/> pair. Setting <paramref name="toState"/>
	/// to <see cref="RetainedContentStates.Grace"/> stamps <c>grace_started_at</c>;
	/// setting it to <see cref="RetainedContentStates.Purged"/> stamps
	/// <c>purged_at</c>.
	/// </summary>
	Task TransitionAsync(Guid id, string toState, CancellationToken cancellationToken);

	/// <summary>
	/// Same as <see cref="TransitionAsync(Guid, string, CancellationToken)"/>, but
	/// stamps <c>grace_started_at</c>/<c>purged_at</c> with the caller-supplied
	/// <paramref name="occurredAt"/> instead of the database's own <c>now()</c> --
	/// added for #1436's retention sweep, which must evaluate a grace window's
	/// elapsed time against the same clock it used to start that window (an
	/// app-clock-stamped start compared against a DB-clock-stamped start drifts under
	/// DB/app clock skew, in a destructive direction). Callers that do not need
	/// single-source-of-truth clock consistency (nothing outside the sweep does today)
	/// keep using the three-argument overload, which is unaffected by this one.
	/// </summary>
	Task TransitionAsync(Guid id, string toState, DateTimeOffset occurredAt, CancellationToken cancellationToken);

	/// <summary>
	/// Sets <c>policy_id</c> on the row identified by <paramref name="id"/> to
	/// <paramref name="policyId"/>, independent of any state transition -- added for
	/// #1436's retention sweep, which resolves a <see cref="RetentionSweepRequest.ScopeKey"/>-keyed
	/// policy for a candidate entering grace and must record that resolution on the
	/// row so a later auto-prune pass evaluates it against the same policy. Does not
	/// validate <paramref name="policyId"/> against <c>download_retention_policies</c>
	/// itself -- the caller resolves it first (<c>policy_id</c>'s own FK constraint is
	/// the backstop).
	/// </summary>
	Task SetPolicyAsync(Guid id, Guid policyId, CancellationToken cancellationToken);

	/// <summary>
	/// Pins the content identified by <paramref name="id"/> (moves it to
	/// <see cref="RetainedContentStates.Pinned"/>, recording who and an optional
	/// note). Throws <see cref="InvalidOperationException"/> when
	/// <see cref="RetainedContentStateTransitions.CanPin"/> rejects the current row's
	/// state -- in particular, pinning already-<c>purged</c> or
	/// <c>pending-purge</c> content.
	/// </summary>
	Task PinAsync(Guid id, string pinnedBy, string? note, CancellationToken cancellationToken);

	Task<IReadOnlyList<RetainedContentState>> ListByStateAsync(string state, CancellationToken cancellationToken);
}
