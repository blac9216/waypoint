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
/// The candidate query for <c>EvidenceRetentionSweepHostedService</c> (issue #1062).
/// Deliberately the ONLY read this sweep needs: a compliance run (<c>scan</c>/
/// <c>remediate</c>) that is terminal, older than the caller-supplied cutoff, not yet
/// purged, and NOT currently held -- the last condition enforced with a SQL anti-join
/// against <c>run_retention_holds</c> INSIDE this query, per PR #1083's round-1 review
/// verdict on the now-deleted <c>IRunRetentionHoldRepository.ListHeldRunIdsAsync</c>:
/// materializing every held run id in the API process to filter a large candidate set
/// in C# does not scale, while <c>WHERE NOT EXISTS (SELECT 1 FROM run_retention_holds
/// h WHERE h.run_id = r.id)</c> scales with the candidate set and needs no C# surface
/// at all. <see cref="Waypoint.Infrastructure.Runs.RunPurgeService.PurgeRunAsync"/>'s
/// own hold check remains the backstop that makes the exclusion correct even if this
/// query ever forgets the anti-join -- this repository does not replace that check,
/// it only decides which run ids are worth calling <c>PurgeRunAsync</c> for.
/// </summary>
public interface IEvidenceRetentionSweepRepository
{
	/// <summary>
	/// Compliance run ids eligible for a retention purge: terminal, older than
	/// <paramref name="olderThan"/> (by <c>completed_at</c>, falling back to
	/// <c>created_at</c> for a terminal row with no <c>completed_at</c> -- same idiom
	/// <see cref="Waypoint.Infrastructure.Runs.RunHistoryRolloffHostedService"/>'s own
	/// candidate query already uses), never purged (<c>runs.purged_at IS NULL</c>),
	/// and not currently held. Ordered oldest-first so a large backlog drains in
	/// completion order across sweep passes, capped at <paramref name="maxRuns"/>.
	/// </summary>
	Task<IReadOnlyList<Guid>> FindPurgeCandidatesAsync(DateTimeOffset olderThan, int maxRuns, CancellationToken cancellationToken);
}
