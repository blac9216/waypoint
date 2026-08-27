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

using Waypoint.Core.Sites;

namespace Waypoint.Core.Jobs;

/// <summary>
/// Maps a <see cref="Target"/>'s <see cref="Target.Kind"/> to its scan fan-out
/// priority (ADR-0008 / migration 0001: "NSX=1, VCSA=2, vCenter=3, ESXi=4, VM=5,
/// SRG=6"). The catalog's six-valued scheme is finer than <see cref="TargetKinds"/>'
/// three-valued transport classification -- VCSA-components-vs-vCenter-API and
/// ESXi-vs-VM are two different *benchmarks against inventory under one target*, not
/// separate target rows (docs/domain-model.md: "Discovered ESXi hosts and VMs are
/// cached inventory under a `vsphere` target, not standalone targets"). This issue's
/// scope (#273: run creation + per-**target** fan-out, no InSpec) fans out one job per
/// target row, so each <c>vsphere</c> target is scored at the vCenter tier (3); a
/// future slice that fans out one job per selected inventory item (ESXi host / VM) --
/// or one per benchmark within a target -- assigns the finer VCSA(2)/ESXi(4)/VM(5)
/// tiers at that point, not here.
/// </summary>
public static class ScanTargetPriority
{
	public const short Nsx = 1;
	public const short VCenter = 3;
	public const short Srg = 6;

	/// <summary>
	/// The fan-out priority for one target row, per <see cref="Target.Kind"/>. Throws
	/// <see cref="ArgumentOutOfRangeException"/> for a kind outside <see cref="TargetKinds.All"/>
	/// -- every target in storage was validated against that closed set at creation
	/// (<c>TargetsController</c>), so this can only happen if that invariant is broken.
	/// </summary>
	public static short ForTargetKind(string kind)
	{
		return kind switch
		{
			TargetKinds.NsxApi => Nsx,
			TargetKinds.VSphere => VCenter,
			TargetKinds.Ssh => Srg,
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"unrecognized target kind '{kind}'."),
		};
	}

	/// <summary>
	/// Issue #737 (epic #726 Wave 2 capstone, ADR-0024): maps one accepted
	/// <see cref="Waypoint.Core.Scans.ScanPlanItem"/>'s catalog-declared
	/// <see cref="Waypoint.Core.Scans.ScanPlanItem.Priority"/> (sourced from
	/// <c>catalog_report_groups.priority</c>, itself <c>CHECK</c>-constrained to the
	/// same 1-6 range this method clamps into -- migration 0050) onto the queue's own
	/// closed <c>jobs_priority_check</c> bound (migration 0001: <c>priority BETWEEN 1
	/// AND 6</c>). The clamp is defensive, not load-bearing under today's schema: it
	/// exists so a future catalog change that widens the report-group priority range
	/// can never let an out-of-bounds value escape into a job row and fail the
	/// <c>INSERT</c> outright (AC "no unbounded values escape") -- the queue's bound is
	/// the closed contract; the catalog's bound is data that could, in principle,
	/// change independently of it.
	/// </summary>
	public static short ForPlanItem(Waypoint.Core.Scans.ScanPlanItem item)
	{
		ArgumentNullException.ThrowIfNull(item);

		const short minPriority = 1;
		const short maxPriority = 6;
		int priority = item.Priority;
		if (priority < minPriority)
		{
			return minPriority;
		}

		if (priority > maxPriority)
		{
			return maxPriority;
		}

		return (short)priority;
	}
}
