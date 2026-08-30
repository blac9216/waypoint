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
	/// already exists rather than throwing.
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
