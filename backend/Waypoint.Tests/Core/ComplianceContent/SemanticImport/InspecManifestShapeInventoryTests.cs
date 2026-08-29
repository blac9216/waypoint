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
	/// <summary>
	/// The single source of truth for what this class asserts about each documented shape, in the order the
	/// inventory doc documents them -- mirrors <c>StigZipReaderShapeInventoryTests.ShapeExpectations</c>
	/// (issue #1121 round-1 review: one table, everything else derived from it). A row with a non-null
	/// <c>ExpectedErrorSubstring</c> IS a <see cref="ShapeIsRejected"/> case. A null one is an accept case;
	/// <c>ZeroInputs</c> then selects which of the two accept-flavoured theories
	/// (<see cref="ShapeResolvesTheDeclaredInput"/> / <see cref="ShapeResolvesToZeroInputs_NotAnError"/>) runs
	/// it. Issue #1103 adds the rows from #1077's own follow-up: document markers, a multi-document stream, a
	/// raw tab used for block indentation (genuinely invalid YAML -- this parser's first reject-flavoured
	/// shape) versus one used only inside a trailing comment, a nested <c>name:</c> under an entry's
	/// <c>value:</c> in both mapping and sequence form, <c>inputs:</c> immediately adjacent to <c>depends:</c>,
	/// and a quoted <c>name:</c> scalar in both double- and single-quote form -- the exact shape PR #1098's
	/// round-1 review used to defeat this guard (see the doc's "What this guard does and does not cover").
	/// </summary>
	private static readonly (string ShapeId, bool ZeroInputs, string? ExpectedErrorSubstring)[] ShapeExpectations =
	[
		("indented-dash-sequence", false, null),
		("column0-dash-sequence", false, null),
		("name-not-first-key", false, null),
		("attributes-legacy-alias", false, null),
		("column0-comment-between-entries", false, null),
		("trailing-inline-comment", false, null),
		("block-scalar-folded-description", false, null),
		("block-scalar-literal-description", false, null),
		("nested-extra-keys-ignored", false, null),
		("empty-inputs-sequence", true, null),
		("missing-inputs-key", true, null),
		("crlf-line-endings", false, null),
		("document-start-end-markers", false, null),
		("multi-document-stream", false, null),
		("tab-block-indentation", false, "not valid YAML"),
		("tab-in-trailing-comment", false, null),
		("nested-name-under-value-mapping", false, null),
		("nested-name-under-value-sequence", false, null),
		("inputs-depends-adjacency", false, null),
		("quoted-scalar-name-double", false, null),
		("quoted-scalar-name-single", false, null),
	];

	/// <summary>Shape IDs this class implements, in the order the inventory doc documents them.</summary>
	public static readonly string[] ImplementedShapeIds = ShapeExpectations.Select(shape => shape.ShapeId).ToArray();

	/// <summary>
	/// Shape IDs whose fixture asserts rejection -- derived from <see cref="ShapeExpectations"/>. Fed to
	/// <see cref="ShapeInventoryDoc.AssertCompleteness"/> so the doc's Expected verdict word for each row is
	/// bound to what this class's fixture actually asserts (issue #1121).
	/// </summary>
	private static readonly HashSet<string> RejectedShapeIds =
		new(ShapeExpectations.Where(shape => shape.ExpectedErrorSubstring is not null).Select(shape => shape.ShapeId), StringComparer.Ordinal);

	/// <summary>
	/// Shape IDs whose expectation is "resolves zero inputs, no error" rather than "resolves the named input" --
	/// derived from <see cref="ShapeExpectations"/> together with <see cref="RejectedShapeIds"/> (a rejected
	/// shape is never a zero-input accept, regardless of its <c>ZeroInputs</c> flag).
	/// </summary>
	private static readonly HashSet<string> NoInputShapeIds =
		new(ShapeExpectations.Where(shape => shape.ZeroInputs && shape.ExpectedErrorSubstring is null).Select(shape => shape.ShapeId), StringComparer.Ordinal);

	/// <summary>Theory rows for <see cref="ShapeResolvesTheDeclaredInput"/>: every accepted shape outside <see cref="NoInputShapeIds"/>.</summary>
	public static TheoryData<string> DeclaredInputShapes => ShapesWhere(id => !NoInputShapeIds.Contains(id) && !RejectedShapeIds.Contains(id));

	/// <summary>Theory rows for <see cref="ShapeResolvesToZeroInputs_NotAnError"/>: every shape in <see cref="NoInputShapeIds"/>.</summary>
	public static TheoryData<string> ZeroInputShapes => ShapesWhere(NoInputShapeIds.Contains);

	/// <summary>Theory rows for <see cref="ShapeIsRejected"/>: every shape in <see cref="RejectedShapeIds"/>, paired with its expected error substring.</summary>
	public static TheoryData<string, string> RejectedShapes
	{
		get
		{
			TheoryData<string, string> data = [];
			foreach ((string shapeId, _, string? expectedError) in ShapeExpectations)
			{
				if (expectedError is not null)
				{
					data.Add(shapeId, expectedError);
				}
			}

			return data;
		}
	}

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
		ShapeInventoryDoc.AssertCompleteness("`InspecManifestParser` (`backend/Waypoint.Core/ComplianceContent/SemanticImport/InspecManifest.cs`)", ImplementedShapeIds, RejectedShapeIds);

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

	[Theory]
	[MemberData(nameof(RejectedShapes))]
	public void ShapeIsRejected(string shapeId, string expectedErrorSubstring)
	{
		string yaml = BuildYaml(shapeId);

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.Null(manifest);
		Assert.NotNull(error);
		Assert.Contains(expectedErrorSubstring, error);
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
		"document-start-end-markers" => """
			---
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			...
			""",
		"multi-document-stream" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			---
			name: unrelated-second-document
			inputs:
			  - name: other_input
			    type: String
			""",
		// A raw tab cannot start a token in block context outside a quoted scalar or comment
		// (YAML core schema); this is genuinely invalid YAML, not a parser gap.
		"tab-block-indentation" => "name: invented-profile\ninputs:\n\t- name: nsx_manager_address\n\t  type: String\n",
		"tab-in-trailing-comment" => "name: invented-profile\ninputs:\n  - name: nsx_manager_address # a comment\twith a tab character\n    type: String\n",
		"nested-name-under-value-mapping" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			    value:
			      name: unrelated-nested-name
			      default: invented.example.internal
			""",
		"nested-name-under-value-sequence" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			    value:
			      - name: unrelated-nested-name
			        default: invented.example.internal
			""",
		"inputs-depends-adjacency" => """
			name: invented-profile
			inputs:
			  - name: nsx_manager_address
			    type: String
			depends:
			  - name: invented-dependency-profile
			""",
		"quoted-scalar-name-double" => """
			name: invented-profile
			inputs:
			  - name: "nsx_manager_address"
			    type: String
			""",
		"quoted-scalar-name-single" => """
			name: invented-profile
			inputs:
			  - name: 'nsx_manager_address'
			    type: String
			""",
		_ => throw new ArgumentOutOfRangeException(nameof(shapeId), shapeId, "no fixture builder for this shape ID"),
	};
}
