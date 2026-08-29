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

using System.IO.Compression;
using System.Text;
using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Tests.Core.ComplianceContent.ShapeInventory;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.Xccdf;

/// <summary>
/// Issue #1077 class-killing guard for <see cref="StigZipReader"/>: every row of the
/// "StigZipReader" section of <c>docs/compliance-content-shape-inventory.md</c> gets
/// an invented fixture here (<see cref="BuildZipForShape"/>) and an asserted expected
/// result, and <see cref="InventoryIsComplete"/> ties this class's implemented shape
/// IDs to that doc. <see cref="BuildZipForShape"/> is also reused by
/// <c>ShapeVerdictDump</c> so the differential harness (<c>scripts/parser-shape-diff.sh</c>)
/// exercises the exact same corpus as this class.
/// </summary>
public sealed class StigZipReaderShapeInventoryTests
{
	/// <summary>
	/// The single source of truth for what this class asserts about each documented shape, in the order the
	/// inventory doc documents them. Every other list in this class -- <see cref="ImplementedShapeIds"/>, the
	/// reject set fed to <see cref="ShapeInventoryDoc.AssertCompleteness"/>, and the theory rows of
	/// <see cref="ShapeIsAccepted"/>/<see cref="ShapeIsRejected"/> -- is derived from this table, so a shape
	/// cannot be dropped from the reject set while a fixture two dozen lines away still asserts rejection
	/// (issue #1121 round-1 review). A row with a non-null <c>ExpectedErrorSubstring</c> IS a
	/// <see cref="ShapeIsRejected"/> case; a row with a null one IS a <see cref="ShapeIsAccepted"/> case
	/// asserting <c>ExpectedEntryCount</c> entries.
	/// </summary>
	private static readonly (string ShapeId, int ExpectedEntryCount, string? ExpectedErrorSubstring)[] ShapeExpectations =
	[
		("single-benchmark", 1, null),
		("flat-multi-xccdf", 2, null),
		("nested-directory-multi-xccdf", 2, null),
		("zip-of-zips", 1, null),
		("zip-of-zips-depth-boundary", 1, null),
		("zip-of-zips-beyond-depth-boundary", 0, "recursion bound"),
		("case-insensitive-xccdf-suffix", 1, null),
		("non-xccdf-siblings-ignored", 1, null),
		("zip-slip-entry-name", 0, "unsafe path"),
		("no-xccdf-entry", 0, "does not contain any entry ending in '-xccdf.xml'"),
	];

	/// <summary>Shape IDs this class implements, in the order the inventory doc documents them.</summary>
	public static readonly string[] ImplementedShapeIds = ShapeExpectations.Select(shape => shape.ShapeId).ToArray();

	/// <summary>
	/// Shape IDs whose fixture asserts rejection -- derived from <see cref="ShapeExpectations"/>, hence from the
	/// very rows <see cref="ShapeIsRejected"/> runs. Fed to <see cref="ShapeInventoryDoc.AssertCompleteness"/> so
	/// the doc's Expected verdict word for each row is bound to what this class's fixture actually asserts
	/// (issue #1121). Removing a shape from this set is not a local edit: it moves that shape into
	/// <see cref="ShapeIsAccepted"/>, which then fails because the reader really does reject it.
	/// </summary>
	private static readonly HashSet<string> RejectedShapeIds =
		new(ShapeExpectations.Where(shape => shape.ExpectedErrorSubstring is not null).Select(shape => shape.ShapeId), StringComparer.Ordinal);

	/// <summary>Theory rows for <see cref="ShapeIsAccepted"/>: every shape in <see cref="ShapeExpectations"/> with no expected error.</summary>
	public static TheoryData<string, int> AcceptedShapes
	{
		get
		{
			TheoryData<string, int> data = [];
			foreach ((string shapeId, int expectedCount, string? expectedError) in ShapeExpectations)
			{
				if (expectedError is null)
				{
					data.Add(shapeId, expectedCount);
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
		ShapeInventoryDoc.AssertCompleteness("`StigZipReader` (`backend/Waypoint.Core/ComplianceContent/Xccdf/StigZipReader.cs`)", ImplementedShapeIds, RejectedShapeIds);

	[Theory]
	[MemberData(nameof(AcceptedShapes))]
	public void ShapeIsAccepted(string shapeId, int expectedCount)
	{
		byte[] zip = BuildZipForShape(shapeId);

		bool ok = StigZipReader.TryReadXccdfEntries(zip, out IReadOnlyList<XccdfZipEntry> entries, out string? error);

		Assert.True(ok, error);
		Assert.Null(error);
		Assert.Equal(expectedCount, entries.Count);
	}

	[Theory]
	[MemberData(nameof(RejectedShapes))]
	public void ShapeIsRejected(string shapeId, string expectedErrorSubstring)
	{
		byte[] zip = BuildZipForShape(shapeId);

		bool ok = StigZipReader.TryReadXccdfEntries(zip, out _, out string? error);

		Assert.False(ok);
		Assert.Contains(expectedErrorSubstring, error);
	}

	/// <summary>
	/// Whether <see cref="StigZipReader.TryReadXccdfEntries"/> resolves at least one
	/// XCCDF entry for <paramref name="shapeId"/> -- the coarse "resolved vs. not"
	/// signal the differential harness diffs old-vs-new on, mirroring the boolean
	/// resolved/null signal <c>ShapeVerdictDump</c> uses for the manifest parser.
	/// </summary>
	public static bool Resolves(string shapeId)
	{
		bool ok = StigZipReader.TryReadXccdfEntries(BuildZipForShape(shapeId), out IReadOnlyList<XccdfZipEntry> entries, out _);
		return ok && entries.Count > 0;
	}

	/// <summary>Builds the invented zip fixture for one documented shape ID.</summary>
	public static byte[] BuildZipForShape(string shapeId)
	{
		switch (shapeId)
		{
			case "single-benchmark":
				return BuildZip(("Invented_STIG_V1R1_Manual-xccdf.xml", InventedBenchmark("a")));
			case "flat-multi-xccdf":
				return BuildZip(
					("Product_ESXi_STIG_V1R1-xccdf.xml", InventedBenchmark("esxi")),
					("Product_vCenter_STIG_V1R1-xccdf.xml", InventedBenchmark("vcenter")),
					("Product_Overview.pdf", "not xccdf"));
			case "nested-directory-multi-xccdf":
				return BuildZip(
					("Product_Supplemental/audit.rules", "not xccdf"),
					("Product_Supplemental/Product_ESXi_V1R1_Manual_STIG/Product_ESXi_STIG-xccdf.xml", InventedBenchmark("esxi")),
					("Product_Supplemental/Product_EAM_V1R1_Manual_STIG/Product_EAM_STIG-xccdf.xml", InventedBenchmark("eam")));
			case "zip-of-zips":
			{
				byte[] inner = BuildZip(("NSX-Manager-xccdf.xml", InventedBenchmark("nsx")));
				return BuildZipOfZips(("U_NSX_STIG.zip", inner));
			}

			case "zip-of-zips-depth-boundary":
			{
				byte[] bundle = BuildZip(("Inner-xccdf.xml", InventedBenchmark("inner")));
				for (int level = 0; level < StigZipReader.MaxRecursionDepth; level++)
				{
					bundle = BuildZipOfZips(($"level-{level}.zip", bundle));
				}

				return bundle;
			}

			case "zip-of-zips-beyond-depth-boundary":
			{
				byte[] bundle = BuildZip(("Inner-xccdf.xml", InventedBenchmark("inner")));
				for (int level = 0; level <= StigZipReader.MaxRecursionDepth; level++)
				{
					bundle = BuildZipOfZips(($"level-{level}.zip", bundle));
				}

				return bundle;
			}

			case "case-insensitive-xccdf-suffix":
				return BuildZip(("Invented_STIG-XCCDF.XML", InventedBenchmark("upper")));
			case "non-xccdf-siblings-ignored":
				return BuildZip(
					("Invented_STIG-xccdf.xml", InventedBenchmark("a")),
					("readme.txt", "not xccdf"),
					("Invented_STIG_Overview.pdf", "not xccdf"));
			case "zip-slip-entry-name":
				return BuildZip(("../../etc/passwd-xccdf.xml", InventedBenchmark("a")));
			case "no-xccdf-entry":
				return BuildZip(("readme.txt", "not xccdf"));
			default:
				throw new ArgumentOutOfRangeException(nameof(shapeId), shapeId, "no fixture builder for this shape ID");
		}
	}

	private static string InventedBenchmark(string suffix) => $$"""
		<Benchmark id="xccdf_invented.example_benchmark_{{suffix}}_EX-1-0_STIG">
		  <title>Invented Example STIG {{suffix}}</title>
		  <version update="1">1</version>
		  <Rule id="SV-1-{{suffix}}" severity="high"><title>r</title></Rule>
		</Benchmark>
		""";

	private static byte[] BuildZip(params (string Name, string Content)[] entries) =>
		BuildZipBytes(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

	private static byte[] BuildZipOfZips(params (string Name, byte[] Content)[] entries) => BuildZipBytes(entries);

	private static byte[] BuildZipBytes(IEnumerable<(string Name, byte[] Content)> entries)
	{
		using MemoryStream stream = new();
		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string name, byte[] bytes) in entries)
			{
				ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
				using Stream entryStream = entry.Open();
				entryStream.Write(bytes, 0, bytes.Length);
			}
		}

		return stream.ToArray();
	}
}
