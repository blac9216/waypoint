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
/// <see cref="StigZipReaderShapeInventoryTests.ImplementedShapeIds"/>) to that path.
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
