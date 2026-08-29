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

using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Tests.Core.ComplianceContent.ShapeInventory;
using Xunit;
using static Waypoint.Tests.Core.ComplianceContent.SemanticImport.VendorContentEntryBuilder;

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Issue #1099 (extending #1077 to the leaf-manifest dimension of
/// <see cref="VendorHierarchyInterpreter"/>): <c>LayoutTableParityTests</c> (issue
/// #959) already guards this interpreter's PATH/layout dimension against
/// docs/compliance-parity.md's provenance matrix; this class guards the orthogonal
/// "how a classified path's PARSED manifest becomes a
/// <see cref="SemanticCandidate"/>'s fields" dimension -- display-name fallback,
/// aggregate/leaf disposition, and pass-through/derived fields -- against the
/// "VendorHierarchyInterpreter leaf-manifest dimension" section of
/// docs/compliance-content-shape-inventory.md. No real vendor content, path, or
/// manifest appears anywhere in this file -- every fixture is invented.
/// </summary>
public sealed class VendorHierarchyInterpreterLeafManifestShapeInventoryTests
{
	/// <summary>
	/// The single source of truth this class asserts about each documented shape: every row here is
	/// accept-flavoured (this dimension has no rejection cases of its own -- malformed-manifest rejection is
	/// already covered by <see cref="VendorHierarchyInterpreterTests.MalformedManifest_IsQuarantinedWithActionableDiagnostic"/>
	/// and is not re-documented here), so <see cref="RejectedShapeIds"/> is always empty.
	/// </summary>
	private static readonly (string ShapeId, Func<VendorHierarchyInterpretation> Interpret, Action<VendorHierarchyInterpretation> AssertBehavior)[] ShapeExpectations =
	[
		(
			"title-present-leaf-uses-manifest-title-as-display-name",
			() => VendorHierarchyInterpreter.Interpret([Leaf(
				"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/vcenter",
				Manifest("vcenter", "Invented vCenter STIG Title"),
				"controls/vc-000001.rb")]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				Assert.Equal("Invented vCenter STIG Title", candidate.DisplayName);
			}
		),
		(
			"title-missing-split-leaf-falls-back-to-tail-segment-literal",
			() => VendorHierarchyInterpreter.Interpret([Leaf(
				"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/esxi",
				Manifest("esxi"),
				"controls/esxi-000001.rb")]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				Assert.Equal("esxi", candidate.DisplayName);
			}
		),
		(
			"title-missing-whole-appliance-falls-back-to-family-name",
			() => VendorHierarchyInterpreter.Interpret([Leaf(
				"photon/5-0/v3r3-srg/inspec/photon-os-5-0-srg-baseline",
				Manifest("photon-os-5-0-srg-baseline"),
				"controls/photon-000001.rb")]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				Assert.Equal("photon", candidate.DisplayName);
			}
		),
		(
			"empty-tail-with-controls-directory-is-an-executable-leaf",
			() => VendorHierarchyInterpreter.Interpret([Leaf(
				"photon/5-0/v3r3-srg/inspec/photon-os-5-0-srg-baseline",
				Manifest("photon-os-5-0-srg-baseline"),
				"controls/photon-000001.rb")]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				Assert.False(candidate.IsAggregate);
				Assert.True(candidate.IsExecutableLeaf);
			}
		),
		(
			"empty-tail-without-controls-directory-is-an-aggregate",
			() => VendorHierarchyInterpreter.Interpret([Aggregate(
				"photon/5-0/v3r3-srg/inspec/photon-os-5-0-srg-baseline",
				Manifest("photon-os-5-0-srg-baseline"))]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				Assert.True(candidate.IsAggregate);
				Assert.False(candidate.IsExecutableLeaf);
			}
		),
		(
			"non-empty-tail-forces-aggregate-even-with-controls-directory",
			() => VendorHierarchyInterpreter.Interpret([Leaf(
				"photon/5-0/v3r3-srg/inspec/photon-os-5-0-srg-baseline/extra-nested-segment",
				Manifest("photon-os-5-0-srg-baseline"),
				"controls/photon-000001.rb")]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				Assert.True(candidate.IsAggregate);
			}
		),
		(
			"inputs-supports-depends-carried-through-unchanged",
			() => VendorHierarchyInterpreter.Interpret([Leaf(
				"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/vcenter",
				"""
				name: vcenter
				title: Invented vCenter STIG Title
				inputs:
				  - name: vcenter_host
				    type: String
				    required: true
				supports:
				  - platform-name: invented-platform
				depends:
				  - name: invented-shared-profile
				""",
				"controls/vc-000001.rb")]),
			result =>
			{
				Assert.Empty(result.Rejections);
				SemanticCandidate candidate = Assert.Single(result.Candidates);
				InspecManifestInput input = Assert.Single(candidate.Inputs);
				Assert.Equal("vcenter_host", input.Name);
				Assert.True(input.Required);
				Assert.Contains("invented-platform", candidate.Supports);
				Assert.Contains("invented-shared-profile", candidate.Depends);
			}
		),
		(
			"content-digest-differs-when-release-key-differs-same-manifest-and-controls",
			() => VendorHierarchyInterpreter.Interpret([
				Leaf("vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/vcenter", Manifest("vcenter", "Invented vCenter STIG Title"), "controls/vc-000001.rb"),
				Leaf("vsphere/8-0/v2r4-stig/inspec/vsphere-8-0-stig-baseline/vcenter", Manifest("vcenter", "Invented vCenter STIG Title"), "controls/vc-000001.rb"),
			]),
			result =>
			{
				Assert.Empty(result.Rejections);
				Assert.Equal(2, result.Candidates.Count);
				Assert.NotEqual(result.Candidates[0].ContentDigest, result.Candidates[1].ContentDigest);
			}
		),
	];

	/// <summary>Shape IDs this class implements, in the order the inventory doc documents them.</summary>
	public static readonly string[] ImplementedShapeIds = ShapeExpectations.Select(shape => shape.ShapeId).ToArray();

	/// <summary>This dimension has no documented reject shapes -- see the class summary.</summary>
	private static readonly HashSet<string> RejectedShapeIds = new(StringComparer.Ordinal);

	public static TheoryData<string> Shapes
	{
		get
		{
			TheoryData<string> data = [];
			foreach (string shapeId in ImplementedShapeIds)
			{
				data.Add(shapeId);
			}

			return data;
		}
	}

	[Fact]
	public void InventoryIsComplete() =>
		ShapeInventoryDoc.AssertCompleteness(
			"`VendorHierarchyInterpreter` leaf-manifest dimension (`backend/Waypoint.Core/ComplianceContent/SemanticImport/VendorHierarchyInterpreter.cs`)",
			ImplementedShapeIds,
			RejectedShapeIds);

	[Theory]
	[MemberData(nameof(Shapes))]
	public void ShapeBehavesAsDocumented(string shapeId)
	{
		(_, Func<VendorHierarchyInterpretation> interpret, Action<VendorHierarchyInterpretation> assertBehavior) =
			ShapeExpectations.Single(shape => shape.ShapeId == shapeId);

		assertBehavior(interpret());
	}
}
