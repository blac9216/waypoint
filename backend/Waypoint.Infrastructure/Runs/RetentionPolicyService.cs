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

using Waypoint.Core.Runs;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #1062 (epic #726 sections 6/7): Admin view/set of the evidence retention
/// period (AC1). Validation lives here (positive day count) rather than in the
/// controller, matching <see cref="RunRetentionHoldService"/>'s own
/// validate-then-delegate-to-repository split; the RBAC floor itself is a controller
/// attribute (<c>[RequireAdminRole]</c>), not this service's job.
/// </summary>
public sealed class RetentionPolicyService
{
	private readonly IRetentionPolicyRepository _policy;

	public RetentionPolicyService(IRetentionPolicyRepository policy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		_policy = policy;
	}

	/// <summary>
	/// Reads the current policy. Migration 0078 seeds the singleton unconditionally,
	/// so a null return is a server-side integrity problem, not a normal empty state
	/// -- same contract <see cref="Waypoint.Core.SystemState.IApplianceStateRepository.GetAsync"/>
	/// documents for its sibling singleton.
	/// </summary>
	public Task<RetentionPolicy?> GetAsync(CancellationToken cancellationToken) => _policy.GetAsync(cancellationToken);

	/// <summary>Sets the retention period. Rejects a non-positive day count before ever reaching the repository.</summary>
	public async Task<SetRetentionPolicyResult> SetRetentionAsync(int evidenceRetentionDays, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		if (evidenceRetentionDays <= 0)
		{
			return new SetRetentionPolicyResult(SetRetentionPolicyOutcome.InvalidRetentionDays);
		}

		RetentionPolicy updated = await _policy.SetAsync(evidenceRetentionDays, actor, cancellationToken).ConfigureAwait(false);
		return new SetRetentionPolicyResult(SetRetentionPolicyOutcome.Updated, updated);
	}
}
