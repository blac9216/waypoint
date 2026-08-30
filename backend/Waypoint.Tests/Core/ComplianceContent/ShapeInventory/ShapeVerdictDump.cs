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

using System.Text.Json;
using Waypoint.Tests.Core.ComplianceContent.SemanticImport;
using Waypoint.Tests.Core.ComplianceContent.Xccdf;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.ShapeInventory;

/// <summary>
/// Issue #1077's differential harness support: when
/// <c>WAYPOINT_SHAPE_DUMP_PATH</c> is set, writes a JSON map of
/// <c>"&lt;parser&gt;/&lt;shape-id&gt;" -&gt; resolved (bool)</c> for the full shape
/// corpus (<see cref="InspecManifestShapeInventoryTests.ImplementedShapeIds"/>,
/// <see cref="StigZipReaderShapeInventoryTests.ImplementedShapeIds"/>,
/// <see cref="XccdfParserShapeInventoryTests.ImplementedShapeIds"/>,
/// <see cref="VendorHierarchyInterpreterLeafManifestShapeInventoryTests.ImplementedShapeIds"/>)
/// to that path. This list is hardcoded per parser, so a NEW inventory section is only
/// reachable by the differential harness once its loop is added here as well as its doc
/// rows and fixture class (PR #1208 round-1 review).
/// <c>scripts/parser-shape-diff.sh</c> runs this once per ref (old and new) and diffs
/// the two JSON files -- see that script and the "Real-content conformance and
/// differential checks" section of <c>docs/compliance-content-shape-inventory.md</c>
/// for why a differential over a synthetic corpus is a DIFFERENT property from
/// real-content conformance, why a silent-miss fix needs both, and why neither can
/// see a shape that is not already an inventory row.
///
/// When the env var is unset (the normal `dotnet test` run), this is a clean no-op --
/// it never affects the pass/fail of a regular test run.
///
/// When <c>WAYPOINT_SHAPE_EXPECTED_DUMP_PATH</c> is also set, additionally writes a JSON map of
/// <c>"&lt;parser&gt;/&lt;shape-id&gt;" -&gt; "accept" | "reject" | null</c>, sourced from
/// <see cref="ShapeInventoryDoc.ClassifyShapes"/> -- the SAME doc-row classification
/// <c>ShapeInventoryDoc.AssertExpectedVocabulary</c>/<c>AssertCompleteness</c> already assert against. The
/// differential script reads this instead of re-parsing the markdown table itself, so the two readers cannot
/// split a row into columns differently (issue #1120).
/// </summary>
public sealed class ShapeVerdictDump
{
	private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

	[Fact]
	public void DumpVerdictsWhenRequested()
	{
		string? path = Environment.GetEnvironmentVariable("WAYPOINT_SHAPE_DUMP_PATH");
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		Dictionary<string, bool> verdicts = [];
		foreach (string shapeId in InspecManifestShapeInventoryTests.ImplementedShapeIds)
		{
			verdicts[$"InspecManifestParser/{shapeId}"] = InspecManifestShapeInventoryTests.Resolves(shapeId);
		}

		foreach (string shapeId in StigZipReaderShapeInventoryTests.ImplementedShapeIds)
		{
			verdicts[$"StigZipReader/{shapeId}"] = StigZipReaderShapeInventoryTests.Resolves(shapeId);
		}

		foreach (string shapeId in XccdfParserShapeInventoryTests.ImplementedShapeIds)
		{
			verdicts[$"XccdfParser/{shapeId}"] = XccdfParserShapeInventoryTests.Resolves(shapeId);
		}

		foreach (string shapeId in VendorHierarchyInterpreterLeafManifestShapeInventoryTests.ImplementedShapeIds)
		{
			verdicts[$"VendorHierarchyInterpreter/{shapeId}"] =
				VendorHierarchyInterpreterLeafManifestShapeInventoryTests.Resolves(shapeId);
		}

		WriteJson(path, verdicts);

		string? expectedPath = Environment.GetEnvironmentVariable("WAYPOINT_SHAPE_EXPECTED_DUMP_PATH");
		if (string.IsNullOrWhiteSpace(expectedPath))
		{
			return;
		}

		Dictionary<string, string?> expected = [];
		foreach ((string shapeId, string? verdict) in ShapeInventoryDoc.ClassifyShapes("`InspecManifestParser` (`backend/Waypoint.Core/ComplianceContent/SemanticImport/InspecManifest.cs`)"))
		{
			expected[$"InspecManifestParser/{shapeId}"] = verdict;
		}

		foreach ((string shapeId, string? verdict) in ShapeInventoryDoc.ClassifyShapes("`StigZipReader` (`backend/Waypoint.Core/ComplianceContent/Xccdf/StigZipReader.cs`)"))
		{
			expected[$"StigZipReader/{shapeId}"] = verdict;
		}

		// Issue #1099: Get-WaypointProfileDeclaredInputNameSet (WaypointScan.psm1) is
		// PowerShell, not C#, so this dump does not run its parser -- only the doc's
		// classification of each row's Expected cell. The RESOLVED half of that
		// parser's verdicts comes from scripts/dump-waypoint-scan-shape-verdicts.ps1
		// instead, and scripts/parser-shape-diff.sh merges the two before diffing, so
		// this parser's classification still comes from the SAME ShapeInventoryDoc
		// code every other parser's does (issue #1120's "one classification, read by
		// both checks" property), even though nothing here executes it.
		foreach ((string shapeId, string? verdict) in ShapeInventoryDoc.ClassifyShapes("`Get-WaypointProfileDeclaredInputNameSet` (`WaypointScan.psm1`)"))
		{
			expected[$"Get-WaypointProfileDeclaredInputNameSet/{shapeId}"] = verdict;
		}

		foreach ((string shapeId, string? verdict) in ShapeInventoryDoc.ClassifyShapes("`XccdfParser` (`backend/Waypoint.Core/ComplianceContent/Xccdf/XccdfParser.cs`)"))
		{
			expected[$"XccdfParser/{shapeId}"] = verdict;
		}

		foreach ((string shapeId, string? verdict) in ShapeInventoryDoc.ClassifyShapes("`VendorHierarchyInterpreter` leaf-manifest dimension (`backend/Waypoint.Core/ComplianceContent/SemanticImport/VendorHierarchyInterpreter.cs`)"))
		{
			expected[$"VendorHierarchyInterpreter/{shapeId}"] = verdict;
		}

		WriteJson(expectedPath, expected);
	}

	private static void WriteJson<TValue>(string path, Dictionary<string, TValue> content)
	{
		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, JsonSerializer.Serialize(content, IndentedJson));
	}
}
