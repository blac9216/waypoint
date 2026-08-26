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
/// Fail-closed drift guard for the PLANNER-PARITY slice of issue #749, mirroring
/// <see cref="ParityMatrixCompletenessTests"/>'s mechanics exactly (same doc, same
/// row set, different question): every one of docs/compliance-parity.md's 13
/// capability-group rows must be either represented in
/// <see cref="PlannerDerivationMatrix.Rows"/> (this slice proves the planner expands it
/// correctly) or explicitly named in <see cref="PlannerDerivationMatrix.OwnerLiveOnlyRows"/>
/// with a rationale.
///
/// This is deliberately a SEPARATE allow-list from <see cref="CatalogDerivationMatrix"/>'s:
/// a row can be catalog-derivation-covered (PR #836) while still being planner-parity
/// owner-live-only today, so shrinking one allow-list is not required to shrink the
/// other. This PR's own contribution: eleven of the thirteen documented families now
/// have a <see cref="PlannerDerivationMatrix.Rows"/> entry proving planner expansion
/// (every family except the two VCF 9-x rows). The two VCF rows stay allow-listed
/// because their underlying catalog-derivation row is ITSELF owner-live-only in
/// <see cref="CatalogDerivationMatrix.OwnerLiveOnlyRows"/> (no 'vcf' vendor family in
/// <c>VendorHierarchyInterpreter</c> yet) -- a planner fixture cannot seed an execution
/// profile that cannot be imported in the first place.
/// </summary>
public sealed class PlannerMatrixCompletenessTests
{
	/// <summary>
	/// The complete set of docs/compliance-parity.md family identifiers this test
	/// tracks, expressed the same way <see cref="PlannerDerivationMatrix"/> keys its
	/// rows -- kept as an explicit ledger (not re-parsed from prose) because the
	/// planner-parity question ("did THIS slice seed a multi-instance planner fixture
	/// for this family") is inherently a test-authorship fact, not something derivable
	/// purely from the markdown table the way <see cref="ParityMatrixCompletenessTests"/>
	/// derives catalog-derivation coverage. <see cref="ParityMatrixCompletenessTests"/>
	/// remains the authority that the row SET itself (13 rows) has not drifted from the
	/// doc; this test only tracks this slice's own coverage of that fixed set.
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

	[Fact]
	public void EveryDocumentedFamily_IsPlannerCoveredOrExplicitlyOwnerLiveOnly()
	{
		HashSet<string> covered = PlannerDerivationMatrix.Rows.Select(r => r.MatrixRowId).ToHashSet();
		HashSet<string> ownerLiveOnly = PlannerDerivationMatrix.OwnerLiveOnlyRows.Keys.ToHashSet();

		List<string> uncovered = AllDocumentedFamilyIds
			.Where(id => !covered.Contains(id) && !ownerLiveOnly.Contains(id))
			.ToList();

		Assert.True(uncovered.Count == 0,
			"docs/compliance-parity.md families not covered by PlannerDerivationMatrix.Rows nor " +
			"explicitly allow-listed in PlannerDerivationMatrix.OwnerLiveOnlyRows: " + string.Join(", ", uncovered));
	}

	[Fact]
	public void NoFamily_IsBothCoveredAndOwnerLiveOnly()
	{
		HashSet<string> covered = PlannerDerivationMatrix.Rows.Select(r => r.MatrixRowId).ToHashSet();
		IEnumerable<string> overlap = PlannerDerivationMatrix.OwnerLiveOnlyRows.Keys.Where(covered.Contains);

		Assert.Empty(overlap);
	}

	[Fact]
	public void OwnerLiveOnlyRows_EachHaveNonEmptyRationale()
	{
		foreach ((string id, string rationale) in PlannerDerivationMatrix.OwnerLiveOnlyRows)
		{
			Assert.False(string.IsNullOrWhiteSpace(rationale), $"planner owner-live-only row '{id}' must document a rationale");
		}
	}

	[Fact]
	public void EveryMatrixRow_HasAtLeastOneInstanceInAtLeastOneSelectorGroup()
	{
		// Guards against a row being added with an empty/zero-instance Instances list,
		// which would make PlannerParityContractTests's theory silently assert an empty
		// plan for that row instead of exercising real fan-out.
		foreach (PlannerParityRow row in PlannerDerivationMatrix.Rows)
		{
			Assert.True(row.Instances.Count > 0, $"planner matrix row '{row.MatrixRowId}' has no selector-kind groups");
			Assert.True(row.TotalInstanceCount > 0, $"planner matrix row '{row.MatrixRowId}' has zero total component instances");
		}
	}

	/// <summary>
	/// This slice's own count-pinned regression guard (mirrors the pattern the dispatch
	/// instructions describe as "the count-pinned allow-list shrinks accordingly"):
	/// pins the planner allow-list to exactly the two rows this PR could not cover
	/// (both blocked on the same underlying catalog-derivation gap: no 'vcf' vendor
	/// family in <c>VendorHierarchyInterpreter</c> yet) and the covered set to exactly
	/// the eleven rows this PR added. A future slice that covers another family must
	/// update both counts here, in the same commit, so this test fails until it does --
	/// never silently drifting quiet.
	/// </summary>
	[Fact]
	public void AllowListCounts_ArePinnedToThisSlicesCoverage()
	{
		Assert.Equal(11, PlannerDerivationMatrix.Rows.Count);
		Assert.Equal(2, PlannerDerivationMatrix.OwnerLiveOnlyRows.Count);
		Assert.Equal(13, AllDocumentedFamilyIds.Count);
	}
}
