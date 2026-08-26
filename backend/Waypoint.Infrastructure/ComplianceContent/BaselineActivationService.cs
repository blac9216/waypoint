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

using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <summary>
/// Admin-only activation/rollback orchestration for a staged <see cref="Baseline"/>
/// (issue #731). Deliberately thin -- <see cref="IBaselineRepository.ActivateAsync"/>
/// already performs the atomic pointer swap under a Postgres transaction with row
/// locking (the actual correctness guarantee); this service adds the
/// application-level checks that belong above the storage boundary: computing the
/// impact diff a caller should see BEFORE confirming activation, and translating the
/// repository's <see cref="BaselineActivationOutcome"/> into the caller's own
/// vocabulary. No HTTP endpoint calls this yet in this slice (issue #731's own AC
/// allows "API exposure may be a stated remainder if size demands" -- see this PR's
/// body); a future <c>POST /candidate-content/{id}/activate</c>/<c>POST
/// /baselines/{id}/rollback</c> (docs/api-contract.md's planned shape) would call this
/// service directly rather than the repository.
///
/// ADR-0022 "the activation boundary is exclusive": this service, like the repository
/// it wraps, is never invoked from a runner-executed job -- only from the Admin-only
/// API surface (a future controller) using the owner connection.
/// </summary>
public sealed class BaselineActivationService
{
	private readonly IBaselineRepository _baselines;

	public BaselineActivationService(IBaselineRepository baselines)
	{
		ArgumentNullException.ThrowIfNull(baselines);
		_baselines = baselines;
	}

	/// <summary>
	/// Activates <paramref name="baselineId"/>, superseding any current active baseline
	/// for the same execution profile. Returns the repository's raw outcome -- a
	/// caller-facing translation (e.g. HTTP 409 body) is the future controller's job,
	/// not this service's.
	/// </summary>
	public Task<BaselineActivationOutcome> ActivateAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken) =>
		_baselines.ActivateAsync(baselineId, activatedBy, cancellationToken);

	/// <summary>
	/// Rolls back to <paramref name="baselineId"/> -- a previously-activated (now
	/// superseded) baseline for the SAME execution profile it already belongs to.
	/// Unlike a fresh activation, rollback additionally validates that the target
	/// baseline actually belongs to the execution profile whose active baseline is
	/// being replaced -- ADR-0022 "the prior baseline must still satisfy integrity/
	/// capability checks for the current appliance before reactivation" (this slice's
	/// integrity check is "the row and its content revision still exist and the
	/// revision was not rejected", which <see cref="IBaselineRepository.ActivateAsync"/>
	/// already enforces via <see cref="BaselineActivationOutcome.RevisionNotEligible"/>;
	/// a fuller capability-compatibility check is deferred to when the catalog
	/// version-compatibility model exists).
	/// </summary>
	public Task<BaselineActivationOutcome> RollbackAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken) =>
		_baselines.RollbackAsync(baselineId, activatedBy, cancellationToken);

	/// <summary>
	/// Computes a minimal impact diff for activating <paramref name="candidateBaselineId"/>
	/// relative to whatever baseline is currently active for the SAME execution
	/// profile (issue #731 AC "operators see a deterministic impact diff before
	/// activation"). This slice's diff is profile-identity-level (added/changed/
	/// removed relative to "was there an active baseline at all, and is it the same
	/// content revision") -- a full per-control semantic diff already exists one layer
	/// up for CANDIDATE review (issue #730's SemanticImportReport/benchmark mapping
	/// pipeline); this method does not duplicate that, it answers the narrower
	/// "what does activating THIS baseline change about which baseline is active"
	/// question a confirmation dialog needs. Unsupported-capability counting is always
	/// 0 in this slice (no catalog-capability-version model exists yet to compare
	/// against) -- a stated remainder, not silently wrong: the field exists in
	/// <see cref="BaselineImpactDiff"/> so a later slice only has to populate it.
	/// </summary>
	public async Task<BaselineImpactDiff> ComputeImpactDiffAsync(Guid candidateBaselineId, CancellationToken cancellationToken)
	{
		Baseline? candidate = await _baselines.GetBaselineAsync(candidateBaselineId, cancellationToken).ConfigureAwait(false);
		if (candidate is null)
		{
			throw new InvalidOperationException($"Baseline '{candidateBaselineId}' does not exist.");
		}

		Baseline? currentActive = await _baselines.GetActiveBaselineAsync(candidate.CatalogExecutionProfileId, cancellationToken).ConfigureAwait(false);

		if (currentActive is null)
		{
			// No baseline is active for this execution profile today -- activating the
			// candidate is a pure addition.
			return new BaselineImpactDiff(AddedProfiles: 1, ChangedProfiles: 0, RemovedProfiles: 0, UnsupportedCapabilities: 0);
		}

		if (currentActive.Id == candidate.Id)
		{
			return new BaselineImpactDiff(AddedProfiles: 0, ChangedProfiles: 0, RemovedProfiles: 0, UnsupportedCapabilities: 0);
		}

		bool sameContentRevision = currentActive.ContentRevisionId == candidate.ContentRevisionId;
		return sameContentRevision
			? new BaselineImpactDiff(AddedProfiles: 0, ChangedProfiles: 0, RemovedProfiles: 0, UnsupportedCapabilities: 0)
			: new BaselineImpactDiff(AddedProfiles: 0, ChangedProfiles: 1, RemovedProfiles: 0, UnsupportedCapabilities: 0);
	}
}
