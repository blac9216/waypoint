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

using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Issue #734 AC-4 ("Preview and create use the same planner and produce the same
/// plan digest"): pure domain logic, no database -- proves the digest function itself
/// is deterministic and order-independent, and that it is sensitive to every field a
/// later reader depends on (a bit-for-bit reproducibility guarantee, ADR-0023).
/// </summary>
public sealed class ScanPlanDigestTests
{
	private static ScanPlanItem MakeItem(
		Guid componentId,
		Guid? baselineId = null,
		Guid? benchmarkRevisionId = null,
		string transport = "vmware-api",
		string reportGroupKey = "group-a",
		int priority = 2,
		string[]? purposes = null,
		string[]? inputs = null) => new(
		componentId,
		Guid.Parse("11111111-1111-1111-1111-111111111111"),
		baselineId,
		benchmarkRevisionId,
		transport,
		"esxi",
		null,
		reportGroupKey,
		priority,
		"hdf_and_ckl",
		purposes ?? ["vsphere-api"],
		inputs ?? ["target_ip"]);

	[Fact]
	public void Compute_SameInputs_ProducesTheSameDigest()
	{
		Guid componentId = Guid.NewGuid();
		ScanPlanItem item = MakeItem(componentId);

		string first = ScanPlanDigest.Compute(1, [componentId], [item]);
		string second = ScanPlanDigest.Compute(1, [componentId], [item]);

		Assert.Equal(first, second);
	}

	[Fact]
	public void Compute_ItemOrderIndependent_ProducesTheSameDigest()
	{
		Guid componentA = Guid.NewGuid();
		Guid componentB = Guid.NewGuid();
		ScanPlanItem itemA = MakeItem(componentA);
		ScanPlanItem itemB = MakeItem(componentB);

		string forward = ScanPlanDigest.Compute(1, [componentA, componentB], [itemA, itemB]);
		string reversed = ScanPlanDigest.Compute(1, [componentB, componentA], [itemB, itemA]);

		Assert.Equal(forward, reversed);
	}

	[Fact]
	public void Compute_PurposeAndInputOrderIndependent_ProducesTheSameDigest()
	{
		Guid componentId = Guid.NewGuid();
		ScanPlanItem forward = MakeItem(componentId, purposes: ["a", "b"], inputs: ["x", "y"]);
		ScanPlanItem reversed = MakeItem(componentId, purposes: ["b", "a"], inputs: ["y", "x"]);

		Assert.Equal(
			ScanPlanDigest.Compute(1, [componentId], [forward]),
			ScanPlanDigest.Compute(1, [componentId], [reversed]));
	}

	[Theory]
	[InlineData(2)] // different schema version
	public void Compute_DifferentSchemaVersion_ProducesADifferentDigest(int otherVersion)
	{
		Guid componentId = Guid.NewGuid();
		ScanPlanItem item = MakeItem(componentId);

		string original = ScanPlanDigest.Compute(1, [componentId], [item]);
		string changed = ScanPlanDigest.Compute(otherVersion, [componentId], [item]);

		Assert.NotEqual(original, changed);
	}

	[Fact]
	public void Compute_DifferentBaselineId_ProducesADifferentDigest()
	{
		Guid componentId = Guid.NewGuid();
		ScanPlanItem withoutBaseline = MakeItem(componentId);
		ScanPlanItem withBaseline = MakeItem(componentId, baselineId: Guid.NewGuid());

		Assert.NotEqual(
			ScanPlanDigest.Compute(1, [componentId], [withoutBaseline]),
			ScanPlanDigest.Compute(1, [componentId], [withBaseline]));
	}

	[Fact]
	public void Compute_DifferentResolvedScope_ProducesADifferentDigestEvenWithIdenticalItems()
	{
		// ADR-0023 "one plan freezes requested and resolved scope" -- the same accepted
		// item set under a different overall resolved scope (e.g. a sibling component
		// dropped due to an unrelated later change) must not collide.
		Guid componentId = Guid.NewGuid();
		Guid otherComponentId = Guid.NewGuid();
		ScanPlanItem item = MakeItem(componentId);

		string scopeOfOne = ScanPlanDigest.Compute(1, [componentId], [item]);
		string scopeOfTwo = ScanPlanDigest.Compute(1, [componentId, otherComponentId], [item]);

		Assert.NotEqual(scopeOfOne, scopeOfTwo);
	}

	[Fact]
	public void Compute_EmptyItemsAndScope_ProducesAStableNonEmptyDigest()
	{
		string digest = ScanPlanDigest.Compute(1, [], []);

		Assert.False(string.IsNullOrWhiteSpace(digest));
		Assert.Equal(digest, ScanPlanDigest.Compute(1, [], []));
	}
}
