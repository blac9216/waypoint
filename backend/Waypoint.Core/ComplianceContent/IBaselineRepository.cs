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

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// Storage for immutable staged content revisions and their activation state
/// (migration 0055, issue #731). ADR-0022 "the activation boundary is exclusive":
/// <see cref="RecordStagedRevisionAsync"/> and <see cref="CreateStagedBaselineAsync"/>
/// are the only writes <c>ContentPullJobHandler</c> (a compliance-runner process) may
/// perform -- they create immutable rows in <c>staged</c> status and never touch an
/// existing active baseline. <see cref="ActivateAsync"/>/<see cref="RollbackAsync"/> are
/// the ONLY methods that ever flip a baseline to/from <c>active</c>, and are called
/// exclusively from the Admin-only API surface (never from a runner-executed job) --
/// see <c>BaselineActivationService</c>.
/// </summary>
public interface IBaselineRepository
{
	/// <summary>
	/// Records one immutable staged content revision. Idempotent by
	/// (source_commit, content_digest): re-staging byte-identical content for the same
	/// source returns the SAME existing row rather than creating a duplicate.
	/// </summary>
	Task<ContentRevision> RecordStagedRevisionAsync(
		string sourceCommit, string contentDigest, string stagedRelativePath, CancellationToken cancellationToken);

	Task<ContentRevision?> GetRevisionAsync(Guid id, CancellationToken cancellationToken);

	Task<IReadOnlyList<ContentRevision>> ListRevisionsAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Creates one staged (not yet active) baseline binding a content revision to an
	/// execution profile (and optional benchmark revision for STIG). Multiple staged
	/// baselines may coexist for the same execution profile -- ADR-0022 "independent
	/// candidate lifecycles" -- only <see cref="ActivateAsync"/> enforces the
	/// one-active-per-execution-profile invariant.
	/// </summary>
	Task<Baseline> CreateStagedBaselineAsync(
		Guid contentRevisionId, Guid catalogExecutionProfileId, Guid? benchmarkRevisionId, CancellationToken cancellationToken);

	Task<Baseline?> GetBaselineAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>Every baseline for one execution profile, newest first.</summary>
	Task<IReadOnlyList<Baseline>> ListBaselinesForExecutionProfileAsync(Guid catalogExecutionProfileId, CancellationToken cancellationToken);

	/// <summary>Every baseline, newest first -- the backing read for a future <c>GET /baselines</c> (docs/api-contract.md).</summary>
	Task<IReadOnlyList<Baseline>> ListAllBaselinesAsync(CancellationToken cancellationToken);

	/// <summary>The current active baseline for one execution profile, or null if none is active.</summary>
	Task<Baseline?> GetActiveBaselineAsync(Guid catalogExecutionProfileId, CancellationToken cancellationToken);

	/// <summary>
	/// Atomically activates <paramref name="baselineId"/>: supersedes any existing
	/// active baseline for the SAME execution profile (status -&gt; 'superseded',
	/// superseded_at stamped) and marks the target row 'active' with
	/// activated_at/activated_by, all within one transaction -- a reader can never
	/// observe zero or two active rows for the same execution profile mid-flight
	/// (issue #731 AC "activation is atomic"). Also marks the target's
	/// <see cref="ContentRevision"/> status 'activated' in the same transaction.
	/// </summary>
	Task<BaselineActivationOutcome> ActivateAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken);

	/// <summary>
	/// Rolls back to a previously-activated (now superseded) baseline -- semantically
	/// identical atomic operation to <see cref="ActivateAsync"/> (ADR-0022 "rollback ...
	/// creates a new activation event pointing at the old artifact set", never
	/// resurrecting the old row's original activated_at). The target baseline must
	/// already exist (any prior status except still-active) and its content revision
	/// must not be 'rejected'.
	/// </summary>
	Task<BaselineActivationOutcome> RollbackAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken);
}
