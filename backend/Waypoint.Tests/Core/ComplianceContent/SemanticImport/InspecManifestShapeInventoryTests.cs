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

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Issue #1077 class-killing guard for <see cref="InspecManifestParser"/>: every row
/// of the "InspecManifestParser" section of
/// <c>docs/compliance-content-shape-inventory.md</c> gets an invented fixture here
/// (<see cref="BuildYaml"/>) and an asserted expected result, and
/// <see cref="InventoryIsComplete"/> ties this class's implemented shape IDs to that
/// doc so the two cannot silently drift apart (the same failure mode issue #959 fixed
/// for <c>VendorHierarchyInterpreter</c>, generalized). <see cref="BuildYaml"/> and
/// <see cref="Resolves"/> are also reused by <c>ShapeVerdictDump</c> so the
/// differential harness (<c>scripts/parser-shape-diff.sh</c>) exercises the exact same
/// corpus as this class.
///
/// Every fixture below is fabricated for this test only -- no real vendor/DISA
/// <c>inspec.yml</c> content is reproduced here (AGENTS.md sanitization policy).
/// </summary>
public sealed class InspecManifestShapeInventoryTests
{
	/// <summary>Shape IDs this class implements, in the order the inventory doc documents them.</summary>
	public static readonly string[] ImplementedShapeIds =
	[
		"indented-dash-sequence",
		"column0-dash-sequence",
		"name-not-first-key",
		"attributes-legacy-alias",
		"column0-comment-between-entries",
		"trailing-inline-comment",
		"block-scalar-folded-description",
		"block-scalar-literal-description",
		"nested-extra-keys-ignored",
		"empty-inputs-sequence",
		"missing-inputs-key",
		"crlf-line-endings",
	];

	/// <summary>
	/// Shape IDs whose expectation is "resolves zero inputs, no error" rather than "resolves the named input".
	/// The single source of truth for which of the two theories below runs a given shape: both
	/// <see cref="DeclaredInputShapes"/> and <see cref="ZeroInputShapes"/> are derived from this set together
	/// with <see cref="ImplementedShapeIds"/>, so the theory rows cannot drift from it (issue #1121 round-1
	/// review). Both theories are accept-flavoured -- this parser has no reject fixtures -- so this class
	/// passes no reject set to <see cref="ShapeInventoryDoc.AssertCompleteness"/>, which then asserts that
	/// every row of its doc section reads <c>Accepted</c> and fails closed the moment a reject row is added.
	/// </summary>
	private static readonly HashSet<string> NoInputShapeIds = new(["empty-inputs-sequence", "missing-inputs-key"], StringComparer.Ordinal);

	/// <summary>Theory rows for <see cref="ShapeResolvesTheDeclaredInput"/>: every implemented shape outside <see cref="NoInputShapeIds"/>.</summary>
	public static TheoryData<string> DeclaredInputShapes => ShapesWhere(id => !NoInputShapeIds.Contains(id));

	/// <summary>Theory rows for <see cref="ShapeResolvesToZeroInputs_NotAnError"/>: every implemented shape in <see cref="NoInputShapeIds"/>.</summary>
	public static TheoryData<string> ZeroInputShapes => ShapesWhere(NoInputShapeIds.Contains);

	private static TheoryData<string> ShapesWhere(Func<string, bool> predicate)
	{
		TheoryData<string> data = [];
		foreach (string shapeId in ImplementedShapeIds)
		{
			if (predicate(shapeId))
			{
				data.Add(shapeId);
			}
		}

		return data;
	}

	[Fact]
	public void InventoryIsComplete() =>
		ShapeInventoryDoc.AssertCompleteness("`InspecManifestParser` (`backend/Waypoint.Core/ComplianceContent/SemanticImport/InspecManifest.cs`)", ImplementedShapeIds);

	[Theory]
	[MemberData(nameof(DeclaredInputShapes))]
	public void ShapeResolvesTheDeclaredInput(string shapeId)
	{
		string yaml = BuildYaml(shapeId);

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.NotNull(manifest);
		Assert.Null(error);
		Assert.Contains(manifest!.Inputs, i => i.Name == "nsx_manager_address");
	}

	[Theory]
	[MemberData(nameof(ZeroInputShapes))]
	public void ShapeResolvesToZeroInputs_NotAnError(string shapeId)
	{
		string yaml = BuildYaml(shapeId);

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.NotNull(manifest);
		Assert.Null(error);
		Assert.Empty(manifest!.Inputs);
	}

	/// <summary>
	/// Whether <see cref="InspecManifestParser.TryParse"/> resolves <paramref name="shapeId"/>'s
	/// documented expectation -- the boolean resolved/not-resolved signal the
	/// differential harness diffs old-vs-new on.
	/// </summary>
	public static bool Resolves(string shapeId)
	{
		InspecManifest? manifest = InspecManifestParser.TryParse(BuildYaml(shapeId), out string? error);
		if (manifest is null || error is not null)
		{
			return false;
		}

		return NoInputShapeIds.Contains(shapeId) ? manifest.Inputs.Count == 0 : manifest.Inputs.Any(i => i.Name == "nsx_manager_address");
	}

	/// <summary>Builds the invented fixture for one documented shape ID.</summary>
	public static string BuildYaml(string shapeId) => shapeId switch
	{
		"indented-dash-sequence" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			    required: true
			""",
		"column0-dash-sequence" => "name: invented-profile\ninputs:\n- name: nsx_manager_address\n  type: String\n  required: true\n",
		"name-not-first-key" => """
			name: invented-profile
			inputs:
			  - description: NSX manager address
			    name: nsx_manager_address
			    type: String
			""",
		"attributes-legacy-alias" => """
			name: invented-profile
			attributes:
			  - name: nsx_manager_address
			    type: String
			""",
		"column0-comment-between-entries" => "name: invented-profile\ninputs:\n  - name: unrelated_input\n# a column-0 comment between entries\n  - name: nsx_manager_address\n    type: String\n",
		"trailing-inline-comment" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address # legacy 3.x key retained for compatibility
			    type: String
			""",
		"block-scalar-folded-description" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    description: >
			      The NSX manager address used to authenticate the scan.
			      Spans multiple folded lines.
			    type: String
			""",
		"block-scalar-literal-description" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    description: |
			      The NSX manager address used to authenticate the scan.
			      Spans multiple literal lines.
			    type: String
			""",
		"nested-extra-keys-ignored" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			    required: true
			    sensitive: true
			    value:
			      default: invented.example.internal
			      source: environment
			""",
		"empty-inputs-sequence" => "name: invented-profile\ninputs: []\n",
		"missing-inputs-key" => "name: invented-profile\ntitle: Invented Profile\n",
		"crlf-line-endings" => "name: invented-profile\r\ninputs:\r\n  - name: nsx_manager_address\r\n    type: String\r\n    required: true\r\n",
		_ => throw new ArgumentOutOfRangeException(nameof(shapeId), shapeId, "no fixture builder for this shape ID"),
	};
}
