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

using Xunit;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Fail-closed drift guard for the EXECUTION-PARITY slice of issue #749, mirroring
/// <see cref="ParityMatrixCompletenessTests"/>/<see cref="PlannerMatrixCompletenessTests"/>'s
/// mechanics exactly (same 13 documented families, same allow-list-or-covered question,
/// a THIRD independent question layered on top): every one of
/// docs/compliance-parity.md's 13 capability-group rows must be either represented by an
/// <see cref="ExecutionDerivationMatrix.Rows"/> entry (this slice proves the family's
/// command-construction shape) or explicitly named in
/// <see cref="ExecutionDerivationMatrix.OwnerLiveOnlyRows"/> with a rationale.
///
/// This is deliberately a SEPARATE allow-list from <see cref="CatalogDerivationMatrix"/>'s
/// and <see cref="PlannerDerivationMatrix"/>'s: a row can be catalog- and planner-parity
/// covered while still being execution-parity owner-live-only (or vice versa is not
/// possible here, since execution needs a plannable row first, but the allow-lists are
/// still tracked independently so a future slice's coverage decisions are never implied
/// by a different slice's).
/// </summary>
public sealed class ExecutionMatrixCompletenessTests
{
	/// <summary>
	/// The complete set of docs/compliance-parity.md family identifiers this test
	/// tracks -- the SAME 13 families <see cref="PlannerMatrixCompletenessTests"/>
	/// tracks (execution rows are keyed one level finer, per selector-kind instance
	/// within a family, but every family must have at least one execution row or an
	/// owner-live-only entry).
	/// </summary>
	private static readonly IReadOnlyList<string> AllDocumentedFamilyIds =
	[
		"vsphere-8-0-stig-vmware",
		"vsphere-8-0-stig-vcsa-ssh",
		"vsphere-9-0-srg-vmware",
		"vsphere-9-0-srg-vcsa-ssh",
		"nsx-4-x-stig",
		"nsx-9-x-srg",
		"aria-operations-8-x-srg",
		"aria-automation-8-x-srg",
		"aria-suite-lifecycle-8-x-srg",
		"vidm-3-3-x-srg",
		"photon-5-0-srg",
		"vcf-9-x-ssh",
		"vcf-9-x-vcf-api",
	];

	/// <summary>
	/// Maps an <see cref="ExecutionDerivationMatrix.Rows"/> row id to the family id it
	/// covers -- execution rows are keyed per selector-kind instance (e.g.
	/// "vsphere-8-0-stig-vmware-esxi"), one level finer than the family-level ids this
	/// test (and <see cref="PlannerMatrixCompletenessTests"/>) track.
	/// </summary>
	private static string FamilyIdFor(ExecutionParityRow row) => row.MatrixRowId switch
	{
		"vsphere-8-0-stig-vmware-vcenter" or "vsphere-8-0-stig-vmware-esxi" or "vsphere-8-0-stig-vmware-vm" => "vsphere-8-0-stig-vmware",
		"vsphere-8-0-stig-vcsa-ssh-service" => "vsphere-8-0-stig-vcsa-ssh",
		"nsx-4-x-stig-service" => "nsx-4-x-stig",
		"vidm-3-3-x-srg-target" => "vidm-3-3-x-srg",
		_ => throw new InvalidOperationException($"ExecutionMatrixCompletenessTests.FamilyIdFor: unmapped execution row id '{row.MatrixRowId}' -- add a mapping when a new row is added to ExecutionDerivationMatrix.Rows."),
	};

	[Fact]
	public void EveryDocumentedFamily_IsExecutionCoveredOrExplicitlyOwnerLiveOnly()
	{
		HashSet<string> covered = ExecutionDerivationMatrix.Rows.Select(FamilyIdFor).ToHashSet();
		HashSet<string> ownerLiveOnly = ExecutionDerivationMatrix.OwnerLiveOnlyRows.Keys.ToHashSet();

		List<string> uncovered = AllDocumentedFamilyIds
			.Where(id => !covered.Contains(id) && !ownerLiveOnly.Contains(id))
			.ToList();

		Assert.True(uncovered.Count == 0,
			"docs/compliance-parity.md families not covered by ExecutionDerivationMatrix.Rows nor " +
			"explicitly allow-listed in ExecutionDerivationMatrix.OwnerLiveOnlyRows: " + string.Join(", ", uncovered));
	}

	[Fact]
	public void NoFamily_IsBothCoveredAndOwnerLiveOnly()
	{
		HashSet<string> covered = ExecutionDerivationMatrix.Rows.Select(FamilyIdFor).ToHashSet();
		IEnumerable<string> overlap = ExecutionDerivationMatrix.OwnerLiveOnlyRows.Keys.Where(covered.Contains);

		Assert.Empty(overlap);
	}

	[Fact]
	public void OwnerLiveOnlyRows_EachHaveNonEmptyRationale()
	{
		foreach ((string id, string rationale) in ExecutionDerivationMatrix.OwnerLiveOnlyRows)
		{
			Assert.False(string.IsNullOrWhiteSpace(rationale), $"execution owner-live-only row '{id}' must document a rationale");
		}
	}

	[Fact]
	public void EveryMatrixRow_HasAnExpectedCommandAndAtLeastOneParameterKey()
	{
		// Guards against a row being added with an empty ExpectedCommand/
		// ExpectedParameterKeys, which would make ExecutionParityContractTests's theory
		// silently assert nothing meaningful for that row.
		foreach (ExecutionParityRow row in ExecutionDerivationMatrix.Rows)
		{
			Assert.False(string.IsNullOrWhiteSpace(row.ExpectedCommand), $"execution matrix row '{row.MatrixRowId}' has no ExpectedCommand");
			Assert.True(row.ExpectedParameterKeys.Count > 0, $"execution matrix row '{row.MatrixRowId}' has no ExpectedParameterKeys");
		}
	}

	/// <summary>
	/// This slice's own count-pinned regression guard (issue #749 deliverable 3): pins
	/// the execution allow-list to exactly the rows this PR could not cover, and the
	/// covered row/family counts to exactly what this PR added. A future slice that
	/// covers another family must update these counts in the same commit, so this test
	/// fails until it does -- never silently drifting quiet.
	///
	/// Owner-live-only rationale summary (see <see cref="ExecutionDerivationMatrix.OwnerLiveOnlyRows"/>
	/// for the full text) falls into two distinct classes, deliberately not collapsed
	/// into one bucket:
	/// <list type="bullet">
	/// <item><description><b>Cannot be executed at all</b> (the two VCF 9-x rows):
	/// blocked on the same underlying gap as the catalog/planner slices -- no 'vcf'
	/// vendor family in <c>VendorHierarchyInterpreter</c> yet, so no executable profile
	/// can be seeded in the first place.</description></item>
	/// <item><description><b>Already proven by an equivalent row</b> (the remaining
	/// seven): <c>ScanJobHandler</c>'s invocation branches are transport/selector/sudo-
	/// driven, never vendor- or output-kind-driven for the SCAN command itself --
	/// output_kind only changes post-scan pipeline routing, which the covered theory
	/// already reads per-row. Four ssh/target SRG families (Aria Operations/Automation/
	/// Suite Lifecycle, Photon) duplicate 'vidm-3-3-x-srg'; three SRG siblings of an
	/// already-covered STIG family (vsphere-9-0-srg-vmware/vcsa-ssh, nsx-9-x-srg)
	/// duplicate their STIG counterpart's invocation shape under a different
	/// OutputKind.</description></item>
	/// </list>
	/// This is the #917-class judgment call the dispatch instructions asked to be made
	/// explicit: distinguishing "cannot be executed yet" from "already proven by an
	/// equivalent row" rather than treating every uncovered family identically.
	/// </summary>
	[Fact]
	public void AllowListCounts_ArePinnedToThisSlicesCoverage()
	{
		Assert.Equal(6, ExecutionDerivationMatrix.Rows.Count);
		Assert.Equal(9, ExecutionDerivationMatrix.OwnerLiveOnlyRows.Count);
		Assert.Equal(13, AllDocumentedFamilyIds.Count);

		// 6 rows collapse to 4 distinct families (vsphere-8-0-stig-vmware contributes 3
		// rows -- vcenter/esxi/vm -- the others contribute 1 each); 4 covered + 9
		// owner-live-only = the full 13-family set with no gaps and no double-counting
		// (NoFamily_IsBothCoveredAndOwnerLiveOnly proves the latter independently).
		HashSet<string> coveredFamilies = ExecutionDerivationMatrix.Rows.Select(FamilyIdFor).ToHashSet();
		Assert.Equal(4, coveredFamilies.Count);
		Assert.Equal(13, coveredFamilies.Count + ExecutionDerivationMatrix.OwnerLiveOnlyRows.Count);
	}
}
