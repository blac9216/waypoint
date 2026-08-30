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

using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Tests.Core.ComplianceContent.ShapeInventory;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.Xccdf;

/// <summary>
/// Issue #1099 (extending #1077's fixture-shape-blindness guard, PR #1098's first
/// slice): every row of the "XccdfParser" section of
/// <c>docs/compliance-content-shape-inventory.md</c> gets an invented fixture here and
/// an asserted expected result, and <see cref="InventoryIsComplete"/> ties this class's
/// implemented shape IDs to that doc -- namespace/prefix variants and encoding-
/// declaration handling, the dimension <see cref="XccdfParserTests"/> (issue #730) did
/// not enumerate as a documented shape corpus. No real DISA/vendor XCCDF content
/// appears anywhere in this file -- every document below is an invented miniature only
/// shaped like public XCCDF structure.
/// </summary>
public sealed class XccdfParserShapeInventoryTests
{
	/// <summary>
	/// The single source of truth this class asserts about each documented shape, mirroring
	/// <c>StigZipReaderShapeInventoryTests.ShapeExpectations</c>: a row with a non-null
	/// <c>ExpectedErrorSubstring</c> IS a <see cref="ShapeIsRejected"/> case; a row with a null
	/// one IS a <see cref="ShapeIsAccepted"/> case asserting <c>ExpectedRuleCount</c> rules.
	/// </summary>
	private static readonly (string ShapeId, int ExpectedRuleCount, string? ExpectedErrorSubstring)[] ShapeExpectations =
	[
		("default-namespace-declared", 1, null),
		("prefixed-namespace-elements", 1, null),
		("no-namespace-declared", 1, null),
		("nested-group-within-group-rule", 1, null),
		("non-utf8-encoding-declaration-ignored-for-char-stream", 1, null),
		("mixed-case-title-and-version-child-elements-still-match", 0, null),
		("lowercase-benchmark-root-element", 0, "top-level 'Benchmark' element"),
		("byte-order-mark-before-declaration", 0, "not valid/safe XML"),
	];

	/// <summary>Shape IDs this class implements, in the order the inventory doc documents them.</summary>
	public static readonly string[] ImplementedShapeIds = ShapeExpectations.Select(shape => shape.ShapeId).ToArray();

	/// <summary>
	/// Shape IDs whose fixture asserts rejection -- derived from <see cref="ShapeExpectations"/>. Fed to
	/// <see cref="ShapeInventoryDoc.AssertCompleteness"/> so the doc's Expected verdict word for each row is bound
	/// to what this class's fixture actually asserts (issue #1121's pattern, carried into this extension).
	/// </summary>
	private static readonly HashSet<string> RejectedShapeIds =
		new(ShapeExpectations.Where(shape => shape.ExpectedErrorSubstring is not null).Select(shape => shape.ShapeId), StringComparer.Ordinal);

	/// <summary>Theory rows for <see cref="ShapeIsAccepted"/>: every shape in <see cref="ShapeExpectations"/> with no expected error.</summary>
	public static TheoryData<string, int> AcceptedShapes
	{
		get
		{
			TheoryData<string, int> data = [];
			foreach ((string shapeId, int expectedRuleCount, string? expectedError) in ShapeExpectations)
			{
				if (expectedError is null)
				{
					data.Add(shapeId, expectedRuleCount);
				}
			}

			return data;
		}
	}

	/// <summary>Theory rows for <see cref="ShapeIsRejected"/>: every shape in <see cref="ShapeExpectations"/> with an expected error.</summary>
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

	[Fact]
	public void InventoryIsComplete() =>
		ShapeInventoryDoc.AssertCompleteness("`XccdfParser` (`backend/Waypoint.Core/ComplianceContent/Xccdf/XccdfParser.cs`)", ImplementedShapeIds, RejectedShapeIds);

	[Theory]
	[MemberData(nameof(AcceptedShapes))]
	public void ShapeIsAccepted(string shapeId, int expectedRuleCount)
	{
		XccdfDocument? document = XccdfParser.TryParse(BuildDocumentForShape(shapeId), out string? error);

		Assert.Null(error);
		Assert.NotNull(document);
		Assert.Equal(expectedRuleCount, document!.Rules.Count);
	}

	[Theory]
	[MemberData(nameof(RejectedShapes))]
	public void ShapeIsRejected(string shapeId, string expectedErrorSubstring)
	{
		XccdfDocument? document = XccdfParser.TryParse(BuildDocumentForShape(shapeId), out string? error);

		Assert.Null(document);
		Assert.Contains(expectedErrorSubstring, error);
	}

	/// <summary>Whether <see cref="XccdfParser.TryParse"/> resolves the invented document for <paramref name="shapeId"/> -- the differential harness's boolean resolved/null signal.</summary>
	public static bool Resolves(string shapeId) => XccdfParser.TryParse(BuildDocumentForShape(shapeId), out _) is not null;

	/// <summary>Builds the invented XML fixture for one documented shape ID.</summary>
	public static string BuildDocumentForShape(string shapeId) => shapeId switch
	{
		"default-namespace-declared" => """
			<?xml version="1.0" encoding="UTF-8"?>
			<Benchmark xmlns="http://checklists.nist.gov/xccdf/1.2" id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>Invented Example STIG</title>
			  <version>1</version>
			  <Rule id="SV-1" severity="high"><title>r</title></Rule>
			</Benchmark>
			""",
		"prefixed-namespace-elements" => """
			<?xml version="1.0" encoding="UTF-8"?>
			<xccdf:Benchmark xmlns:xccdf="http://checklists.nist.gov/xccdf/1.2" id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <xccdf:title>Invented Example STIG</xccdf:title>
			  <xccdf:version>1</xccdf:version>
			  <xccdf:Rule id="SV-1" severity="high"><xccdf:title>r</xccdf:title></xccdf:Rule>
			</xccdf:Benchmark>
			""",
		"no-namespace-declared" => """
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>Invented Example STIG</title>
			  <version>1</version>
			  <Rule id="SV-1" severity="high"><title>r</title></Rule>
			</Benchmark>
			""",
		"nested-group-within-group-rule" => """
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>Invented Example STIG</title>
			  <version>1</version>
			  <Group id="G-1"><Group id="G-2"><Rule id="SV-1" severity="high"><title>r</title></Rule></Group></Group>
			</Benchmark>
			""",
		"non-utf8-encoding-declaration-ignored-for-char-stream" => """
			<?xml version="1.0" encoding="ISO-8859-1"?>
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>Invented Example STIG</title>
			  <version>1</version>
			  <Rule id="SV-1" severity="high"><title>r</title></Rule>
			</Benchmark>
			""",
		"mixed-case-title-and-version-child-elements-still-match" => """
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <TITLE>Invented Example STIG</TITLE>
			  <Version>1</Version>
			</Benchmark>
			""",
		"lowercase-benchmark-root-element" => """
			<benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>Invented Example STIG</title>
			  <version>1</version>
			</benchmark>
			""",
		"byte-order-mark-before-declaration" => "﻿" + """
			<?xml version="1.0" encoding="UTF-8"?>
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>Invented Example STIG</title>
			  <version>1</version>
			  <Rule id="SV-1" severity="high"><title>r</title></Rule>
			</Benchmark>
			""",
		_ => throw new ArgumentOutOfRangeException(nameof(shapeId), shapeId, "no fixture builder for this shape ID"),
	};
}
