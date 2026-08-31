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
/// The grace-window auto-prune driver for <c>download_retained_content_state</c>
/// (migration 0107, issue #1406) -- issue #1436, epic #1182. One implementation
/// (<c>Waypoint.Infrastructure.Downloads.RetentionSweepService</c>), consumed by
/// <c>Waypoint.Infrastructure.Execution.Downloads.RetentionSweepJobHandler</c> (the
/// <c>retention-sweep</c> <c>waypoint_download_runner</c>-claimed job).
///
/// Candidate discovery is deliberately NOT this service's job: "identify
/// superseded/out-of-window content within a subscription's scope" (this issue's own
/// Proposed Changes) needs a real Subscription entity that does not exist yet (#1421
/// is still open) -- so <see cref="RunSweepAsync"/> takes its candidate depot-artifact
/// ids as an explicit input from the caller, rather than querying for them itself.
/// Wiring a real subscription-driven candidate query is deferred to #1421 landing
/// (see #1436's deferred items). This keeps the safety contracts this issue actually
/// owes -- grace transition, alert-on-entry, timed auto-prune, the partial-listing
/// refusal, and immediate purge -- reviewable independent of that still-missing
/// discovery mechanism, and gives the AC "orphans / out-of-subscription-scope content
/// is never touched" a structural proof for free: this service never reads
/// <c>unknown_catalog_files</c> or any table but <c>download_retained_content_state</c>
/// / <c>download_retention_policies</c> / <c>depot_artifacts</c>, so an orphan --
/// which has no <c>download_retained_content_state</c> row at all -- cannot be named
/// as a candidate that resolves to anything, and is never visited by the auto-prune
/// pass either (it only ever walks existing <c>grace</c>-state rows).
///
/// Row creation (<see cref="IRetainedContentStateRepository.EnsureTrackedAsync"/>,
/// an INSERT) is also deliberately never called from here: migration 0127 grants
/// <c>waypoint_download_runner</c> only <c>SELECT, UPDATE</c> on
/// <c>download_retained_content_state</c> (this issue's grants note, following the
/// 0100/#1484 precedent of granting exactly what the landing consumer needs) -- the
/// runner process this service executes in cannot INSERT. A candidate naming a
/// depot artifact with no existing tracked row is skipped, not inserted; the future
/// API-process caller that first names an artifact as retention-worthy (#1453, which
/// runs with the API's own, broader-privileged connection) owns that initial
/// <c>EnsureTrackedAsync</c> call.
/// </summary>
public interface IRetentionSweepService
{
	/// <summary>
	/// Runs one sweep pass over <paramref name="request"/>'s candidates (transitions
	/// each already-<see cref="RetainedContentStates.Tracked"/> candidate into
	/// <see cref="RetainedContentStates.Grace"/> and raises an alert), then walks
	/// every row currently in <see cref="RetainedContentStates.Grace"/> and
	/// auto-prunes (via the same purge path as <see cref="PurgeImmediatelyAsync"/>)
	/// any whose grace window (its resolved <see cref="RetentionPolicy.GracePeriodDays"/>)
	/// has elapsed. <see cref="RetentionSweepRequest.ListingVerified"/> <c>false</c>
	/// short-circuits BOTH passes -- the hard safety contract this issue's Risk note
	/// calls out: "never prune on unverified state, never prune on a partial/incomplete
	/// listing" -- and is reported back via <see cref="RetentionSweepReport.Skipped"/>,
	/// not thrown, since a caller reporting an incomplete listing is expected input,
	/// not a defect.
	/// </summary>
	Task<RetentionSweepReport> RunSweepAsync(RetentionSweepRequest request, CancellationToken cancellationToken);

	/// <summary>
	/// Purges <paramref name="retainedContentStateId"/> immediately -- walking it
	/// through whatever legal transitions reach
	/// <see cref="RetainedContentStates.Purged"/> (<c>tracked -&gt; grace -&gt;
	/// pending-purge -&gt; purged</c>, or a shorter prefix if the row is already
	/// further along) in one call, without waiting for its grace window to elapse --
	/// and deletes the underlying depot file, logging the outcome per file. Content
	/// already <see cref="RetainedContentStates.Pinned"/> is refused (an explicit
	/// domain guard beyond the raw transition table -- pinning exists specifically to
	/// protect content from removal): unpin it first. Content already
	/// <see cref="RetainedContentStates.Purged"/> is reported as a no-op, not an
	/// error. Called both by the scheduled sweep's own auto-prune pass and by the
	/// future retention API (#1453)'s purge-now endpoint.
	/// </summary>
	Task<RetentionPurgeOutcome> PurgeImmediatelyAsync(
		Guid retainedContentStateId, string actor, string? reason, CancellationToken cancellationToken);
}

/// <summary>
/// One <see cref="IRetentionSweepService.RunSweepAsync"/> invocation's candidates and
/// safety gate. <see cref="ScopeKey"/> resolves which <see cref="RetentionPolicy"/>
/// newly-entering-grace candidates are evaluated against (falls back to
/// <see cref="RetentionPolicyScopes.Default"/> when null/blank or unresolvable);
/// already-tracked rows already carry their own resolved <c>policy_id</c> and ignore
/// this field entirely during the auto-prune pass.
/// </summary>
public sealed record RetentionSweepRequest(
	IReadOnlyList<Guid> SupersededOrOutOfWindowDepotArtifactIds,
	bool ListingVerified,
	string? ScopeKey = null);

/// <summary>
/// The outcome of one <see cref="IRetentionSweepService.RunSweepAsync"/> pass.
/// <see cref="Skipped"/> true means the partial/unverified-listing safety gate fired
/// and nothing else in this record is meaningful (every count is zero).
/// </summary>
public sealed record RetentionSweepReport(
	bool Skipped,
	string? SkippedReason,
	int EnteredGrace,
	int AutoPruned,
	int UntrackedCandidatesSkipped,
	IReadOnlyList<string> Errors);

/// <summary>One <see cref="IRetentionSweepService.PurgeImmediatelyAsync"/> outcome.</summary>
public sealed record RetentionPurgeOutcome(Guid RetainedContentStateId, bool Purged, string? Error);
