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

namespace Waypoint.Core.Runs;

/// <summary>
/// The <c>retention_policy</c> singleton row (migration 0078; epic #726 section 6:
/// "retention defaults to six months, Admin-configurable"). <see cref="UpdatedBy"/> is
/// <c>null</c> when the policy still holds the seeded default and has never been
/// changed by an Admin.
/// </summary>
public sealed record RetentionPolicy(int EvidenceRetentionDays, string? UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>Outcome of a single "set the retention period" request.</summary>
public enum SetRetentionPolicyOutcome
{
	/// <summary><paramref name="EvidenceRetentionDays"/>-style value was not a positive integer.</summary>
	InvalidRetentionDays,

	/// <summary>
	/// Value was positive but below <see cref="Waypoint.Infrastructure.Runs.RetentionPolicyService.MinimumEvidenceRetentionDays"/>
	/// -- issue #1109: the floor a typo (a dropped trailing zero, "1" instead of
	/// "180") would otherwise sail through, given the sweep re-reads this value fresh
	/// every pass with no restart and no confirmation step.
	/// </summary>
	BelowMinimum,

	/// <summary>Policy updated.</summary>
	Updated,
}

/// <summary>Return shape for <see cref="Waypoint.Infrastructure.Runs.RetentionPolicyService.SetRetentionAsync"/>.</summary>
public sealed record SetRetentionPolicyResult(SetRetentionPolicyOutcome Outcome, RetentionPolicy? Policy = null);

/// <summary>
/// Storage for <c>retention_policy</c> (migration 0078). One implementation
/// (<c>Waypoint.Infrastructure.Runs.RetentionPolicyRepository</c>, plain Npgsql, same
/// "no ORM for this layer" convention as every other repository in this namespace).
/// </summary>
public interface IRetentionPolicyRepository
{
	/// <summary>
	/// Reads the singleton row. Migration 0078 seeds it unconditionally (<c>id = 1</c>
	/// with the 180-day default), so a null return means the row was deleted out of
	/// band rather than "not yet configured" -- callers should treat that as a
	/// server error, matching <see cref="Waypoint.Core.SystemState.IApplianceStateRepository.GetAsync"/>'s
	/// own contract for its sibling singleton.
	/// </summary>
	Task<RetentionPolicy?> GetAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Updates the singleton row's retention period, actor, and timestamp in one
	/// statement, and writes one <c>audit_log</c> row for the attempt in the same
	/// transaction -- issue #1109: every retention-period change (including a no-op
	/// PUT that resubmits the current value) leaves a durable, actor-attributed
	/// record, matching the bar <c>RunRetentionHoldRepository</c> (issue #784) already
	/// set for the hold's own transitions.
	/// </summary>
	Task<RetentionPolicy> SetAsync(int evidenceRetentionDays, string actor, CancellationToken cancellationToken);
}
