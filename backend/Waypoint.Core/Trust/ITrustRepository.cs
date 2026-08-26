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

namespace Waypoint.Core.Trust;

/// <summary>
/// Storage for managed CA trust bundles and scoped trust-policy bindings (migration
/// 0059, issue #753, ADR-0025). One implementation
/// (<c>Waypoint.Infrastructure.Trust.TrustRepository</c>, plain Npgsql).
/// </summary>
public interface ITrustRepository
{
	/// <summary>Returns the existing ACTIVE bundle sharing <paramref name="fingerprintSha256"/>, if any -- the duplicate-detection read <c>TrustBundleService</c> composes with <see cref="Trust.TrustBundleValidator"/>'s per-upload parse.</summary>
	Task<TrustBundle?> FindActiveByFingerprintAsync(string fingerprintSha256, CancellationToken cancellationToken);

	/// <summary>
	/// Inserts one new bundle row. When <paramref name="supersedesId"/> is supplied, the
	/// old row is flipped to <see cref="TrustBundleStatuses.Superseded"/> in the SAME
	/// transaction (issue #753 AC "replacement -- supersede, don't mutate"); the old
	/// row's PEM/metadata columns are never rewritten.
	/// </summary>
	Task<TrustBundle> CreateAsync(
		string label, string pemChain, string subject, string issuer, string fingerprintSha256,
		DateTimeOffset notBefore, DateTimeOffset notAfter, string uploadedBy, Guid? supersedesId,
		CancellationToken cancellationToken);

	Task<TrustBundle?> GetAsync(Guid id, CancellationToken cancellationToken);

	Task<IReadOnlyList<TrustBundle>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Deletes a bundle if and only if no <c>trust_policies</c> row (current or
	/// superseded) references it and it exists -- see migration 0059's own
	/// <c>ON DELETE RESTRICT</c> plus this pre-check for a clean 409 instead of a raw
	/// constraint-violation surfacing to the API caller.
	/// </summary>
	Task<TrustBundleDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken);

	Task<TrustPolicy?> GetCurrentPolicyAsync(string scopeType, string scopeId, CancellationToken cancellationToken);

	Task<TrustPolicy?> GetPolicyAsync(Guid id, CancellationToken cancellationToken);

	Task<IReadOnlyList<TrustPolicy>> ListPoliciesAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Atomically supersedes any existing CURRENT policy for the same
	/// (<paramref name="scopeType"/>, <paramref name="scopeId"/>) and inserts a new
	/// current row -- the same "supersede then insert, one transaction" idiom
	/// <c>BaselineRepository.ActivateAsync</c> uses for the analogous one-current-row
	/// invariant. For <see cref="TrustPolicyModes.Bundle"/>, the referenced bundle must
	/// exist and be <see cref="TrustBundleStatuses.Active"/> (a superseded bundle
	/// remains addressable for HISTORICAL policy references, but a NEW policy may never
	/// be created pointing at an already-superseded bundle).
	/// </summary>
	Task<(TrustPolicyWriteOutcome Outcome, TrustPolicy? Policy)> SetPolicyAsync(
		string scopeType, string scopeId, string mode, Guid? trustBundleId, string? bypassReason, string actor,
		CancellationToken cancellationToken);
}
