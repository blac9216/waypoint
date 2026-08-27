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

namespace Waypoint.Core.Scans;

/// <summary>
/// Storage for an accepted, immutable <see cref="ScanPlan"/> (migration 0057's
/// <c>scan_plans</c>/<c>scan_plan_items</c>). Written exactly once, at run creation, by
/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/> -- there is no update
/// method, matching ADR-0023 "Later inventory, content activation, target edits,
/// retirement, or purge cannot rewrite them." A preview call
/// (<see cref="Waypoint.Infrastructure.Runs.ScanPlannerService.CompileAsync"/> with a
/// null run id) never reaches this repository at all.
/// </summary>
public interface IScanPlanRepository
{
	/// <summary>
	/// Persists <paramref name="plan"/> against <paramref name="runId"/> and
	/// <paramref name="runScopeSnapshotId"/> (migration 0056's row for the same run, or
	/// null when the run used the legacy target-granular scope shape with no scope
	/// snapshot at all). Only ever called with a plan whose <see cref="ScanPlan.IsRunnable"/>
	/// is true -- an unrunnable plan is rejected by the caller before any run row
	/// exists, so this method never has to represent "a plan with zero items" for a
	/// real run.
	///
	/// Returns the persisted <c>scan_plan_items.id</c> for every accepted item, keyed
	/// by <see cref="ScanPlanItem.ComponentId"/> (issue #737, epic #726 Wave 2
	/// capstone: <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>'s
	/// component-granular job fan-out needs the real row id to populate
	/// <c>jobs.scan_plan_item_id</c>, which does not exist on the pre-persistence
	/// <see cref="ScanPlanItem"/> DTO itself). <c>scan_plan_items_unique_component_per_plan</c>
	/// (migration 0057) guarantees at most one row per component within this plan, so
	/// the mapping is unambiguous.
	/// </summary>
	Task<IReadOnlyDictionary<Guid, Guid>> RecordAsync(Guid runId, Guid? runScopeSnapshotId, ScanPlan plan, CancellationToken cancellationToken);

	/// <summary>The recorded plan for a run, or null when this run predates #734 or was not planned (e.g. a legacy request shape with no target_scope).</summary>
	Task<ScanPlan?> GetForRunAsync(Guid runId, CancellationToken cancellationToken);
}
