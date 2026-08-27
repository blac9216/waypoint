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

using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Jobs;

/// <summary>
/// Issue #737 (epic #726 Wave 2 capstone, ADR-0024): <see cref="ScanTargetPriority.ForPlanItem"/>
/// maps one accepted <see cref="ScanPlanItem"/>'s catalog-declared priority onto the
/// queue's own closed <c>jobs_priority_check</c> bound (migration 0001: 1-6). Every
/// value <c>catalog_report_groups.priority</c> can actually hold is already
/// CHECK-constrained to that same 1-6 range (migration 0050), so the in-bounds cases
/// below prove a faithful pass-through and the out-of-bounds cases prove the
/// defensive clamp never lets an escaped value reach a job row (AC-2 "no unbounded
/// values escape").
/// </summary>
public sealed class ScanTargetPriorityTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	public void ForPlanItem_InBoundsCatalogPriority_PassesThroughUnchanged(int catalogPriority)
	{
		ScanPlanItem item = BuildItem(catalogPriority);
		short priority = ScanTargetPriority.ForPlanItem(item);
		Assert.Equal((short)catalogPriority, priority);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(-5, 1)]
	[InlineData(int.MinValue, 1)]
	public void ForPlanItem_BelowClosedBound_ClampsToMinimum(int catalogPriority, short expected)
	{
		ScanPlanItem item = BuildItem(catalogPriority);
		Assert.Equal(expected, ScanTargetPriority.ForPlanItem(item));
	}

	[Theory]
	[InlineData(7, 6)]
	[InlineData(100, 6)]
	[InlineData(int.MaxValue, 6)]
	public void ForPlanItem_AboveClosedBound_ClampsToMaximum(int catalogPriority, short expected)
	{
		ScanPlanItem item = BuildItem(catalogPriority);
		Assert.Equal(expected, ScanTargetPriority.ForPlanItem(item));
	}

	[Fact]
	public void ForPlanItem_NullItem_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => ScanTargetPriority.ForPlanItem(null!));
	}

	private static ScanPlanItem BuildItem(int priority) => new(
		ComponentId: Guid.NewGuid(),
		CatalogExecutionProfileId: Guid.NewGuid(),
		BaselineId: null,
		BenchmarkRevisionId: null,
		Transport: "vmware",
		SelectorKind: "esxi",
		SelectorName: null,
		ReportGroupKey: "test-group",
		Priority: priority,
		OutputKind: "hdf_ckl",
		RequiredPurposes: ["vsphere-api"],
		DeclaredInputNames: []);
}
