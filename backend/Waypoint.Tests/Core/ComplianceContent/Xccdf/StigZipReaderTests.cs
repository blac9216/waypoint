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
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.Xccdf;

/// <summary>
/// Issue #730 deliverable 2's zip-side hostile-input suite (size, path-traversal
/// (zip-slip), and malformed-archive protections around the STIG-zip -> XCCDF
/// extraction step), extended by issue #1073's multi-benchmark fan-out and bounded
/// zip-of-zips recursion. Every archive built here is invented, in-memory, and
/// contains only miniature invented XCCDF text (never real STIG/DISA content).
/// </summary>
public sealed class StigZipReaderTests
{
	private const string InventedXccdf = """
		<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
		  <title>Invented Example STIG</title>
		  <version update="1">1</version>
		  <Rule id="SV-1" severity="high"><title>r</title></Rule>
		</Benchmark>
		""";

	[Fact]
	public void TryReadXccdfEntries_NullOrEmptyBytes_ReturnsActionableError()
	{
		Assert.False(StigZipReader.TryReadXccdfEntries(null, out _, out string? errorForNull));
		Assert.Contains("empty or missing", errorForNull);

		Assert.False(StigZipReader.TryReadXccdfEntries([], out _, out string? errorForEmpty));
		Assert.Contains("empty or missing", errorForEmpty);
	}

	[Fact]
	public void TryReadXccdfEntries_OversizedArchive_IsRejected()
	{
		byte[] oversized = new byte[StigZipReader.MaxArchiveBytes + 1];

		Assert.False(StigZipReader.TryReadXccdfEntries(oversized, out _, out string? error));
		Assert.Contains("archive bound", error);
	}

	[Fact]
	public void TryReadXccdfEntries_NotAZipFile_ReturnsActionableErrorRatherThanThrowing()
	{
		byte[] notAZip = Encoding.UTF8.GetBytes("this is definitely not a zip archive");

		Assert.False(StigZipReader.TryReadXccdfEntries(notAZip, out _, out string? error));
		Assert.Contains("not a valid zip archive", error);
	}

	[Fact]
	public void TryReadXccdfEntries_ValidSingleBenchmarkPackage_ExtractsOneEntry()
	{
		byte[] zipBytes = BuildZip(("Invented_STIG_V1R1_Manual-xccdf.xml", InventedXccdf));

		bool ok = StigZipReader.TryReadXccdfEntries(zipBytes, out IReadOnlyList<XccdfZipEntry> entries, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		XccdfZipEntry entry = Assert.Single(entries);
		Assert.Contains("xccdf_invented.example_benchmark_EX-1-0_STIG", entry.XmlText);
		Assert.Equal("Invented_STIG_V1R1_Manual-xccdf.xml", entry.EntryPath);
	}

	[Fact]
	public void TryReadXccdfEntries_ZipSlipEntryName_IsRejected()
	{
		byte[] zipBytes = BuildZip(("../../etc/passwd-xccdf.xml", InventedXccdf));

		Assert.False(StigZipReader.TryReadXccdfEntries(zipBytes, out _, out string? error));
		Assert.Contains("unsafe path", error);
	}

	[Fact]
	public void TryReadXccdfEntries_AbsolutePathEntryName_IsRejected()
	{
		byte[] zipBytes = BuildZip(("/etc/passwd-xccdf.xml", InventedXccdf));

		Assert.False(StigZipReader.TryReadXccdfEntries(zipBytes, out _, out string? error));
		Assert.Contains("unsafe path", error);
	}

	[Fact]
	public void TryReadXccdfEntries_NoXccdfEntry_IsRejected()
	{
		byte[] zipBytes = BuildZip(("readme.txt", "not xccdf"));

		Assert.False(StigZipReader.TryReadXccdfEntries(zipBytes, out _, out string? error));
		Assert.Contains("does not contain any entry ending in '-xccdf.xml'", error);
	}

	[Fact]
	public void TryReadXccdfEntries_TooManyEntries_IsRejected()
	{
		(string Name, string Content)[] entries = new (string, string)[StigZipReader.MaxEntryCount + 1];
		for (int i = 0; i < entries.Length; i++)
		{
			entries[i] = ($"file-{i}.txt", "x");
		}

		byte[] zipBytes = BuildZip(entries);

		Assert.False(StigZipReader.TryReadXccdfEntries(zipBytes, out _, out string? error));
		Assert.Contains("entry bound", error);
	}

	[Fact]
	public void TryReadXccdfEntries_EntryDeclaringOversizedLength_IsRejectedBeforeReadingIntoMemory()
	{
		// A well-formed small zip whose single XCCDF-named entry's UNCOMPRESSED content
		// exceeds the per-entry bound -- proves the reader checks actual decompressed
		// size, not just the archive's overall byte length, guarding against a
		// decompression-bomb-style entry.
		string oversizedContent = new string('a', (int)StigZipReader.MaxEntryBytes + 1);
		byte[] zipBytes = BuildZip(("Oversized_STIG-xccdf.xml", oversizedContent));

		Assert.False(StigZipReader.TryReadXccdfEntries(zipBytes, out _, out string? error));
		Assert.Contains("per-entry bound", error);
	}

	/// <summary>
	/// Issue #1073 class-killing guard: the three real vendor package shapes (measured
	/// against the vendor compliance-content repository) each parse to their expected
	/// benchmark count, so the single-benchmark assumption cannot silently return.
	/// Every fixture below is invented -- no vendor/DISA content is reproduced here.
	/// </summary>
	[Fact]
	public void TryReadXccdfEntries_FlatMultiXccdf_ReturnsOneEntryPerSibling()
	{
		byte[] zipBytes = BuildZip(
			("Product_ESXi_STIG_Readiness_Guide_V1R1-xccdf.xml", InventedBenchmark("esxi")),
			("Product_vCenter_STIG_Readiness_Guide_V1R1-xccdf.xml", InventedBenchmark("vcenter")),
			("Product_VM_STIG_Readiness_Guide_V1R1-xccdf.xml", InventedBenchmark("vm")),
			("Product_STIG_Readiness_Guide_Overview.pdf", "not xccdf"));

		bool ok = StigZipReader.TryReadXccdfEntries(zipBytes, out IReadOnlyList<XccdfZipEntry> entries, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		Assert.Equal(3, entries.Count);
	}

	[Fact]
	public void TryReadXccdfEntries_NestedDirectoryMultiXccdf_ReturnsOneEntryPerComponentDirectory()
	{
		byte[] zipBytes = BuildZip(
			("Product_Supplemental/audit.rules", "not xccdf"),
			("Product_Supplemental/Product_ESXi_V1R1_Manual_STIG/Product_ESXi_STIG_V1R1_Manual-xccdf.xml", InventedBenchmark("esxi")),
			("Product_Supplemental/Product_vCenter_V1R1_Manual_STIG/Product_vCenter_STIG_V1R1_Manual-xccdf.xml", InventedBenchmark("vcenter")),
			("Product_Supplemental/Product_EAM_V1R1_Manual_STIG/Product_EAM_STIG_V1R1_Manual-xccdf.xml", InventedBenchmark("eam")),
			("Product_Supplemental/Product_STS_V1R1_Manual_STIG/Product_STS_STIG_V1R1_Manual-xccdf.xml", InventedBenchmark("sts")));

		bool ok = StigZipReader.TryReadXccdfEntries(zipBytes, out IReadOnlyList<XccdfZipEntry> entries, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		Assert.Equal(4, entries.Count);
	}

	[Fact]
	public void TryReadXccdfEntries_ZipOfZips_RecursesIntoNestedComponentPackagesAndReturnsEveryBenchmark()
	{
		byte[] nsxZip = BuildZip(("NSX-Manager-xccdf.xml", InventedBenchmark("nsx-manager")));
		byte[] vSphereZip = BuildZip(
			("vSphere-ESXi-xccdf.xml", InventedBenchmark("esxi")),
			("vSphere-vCenter-xccdf.xml", InventedBenchmark("vcenter")));
		byte[] sddcManagerZip = BuildZip(("SDDC-Manager-xccdf.xml", InventedBenchmark("sddc-manager")));

		byte[] bundleZip = BuildZipOfZips(
			("U_NSX_STIG.zip", nsxZip),
			("U_vSphere_STIG.zip", vSphereZip),
			("U_SDDC_Manager_STIG_Readiness_Guide.zip", sddcManagerZip),
			("Bundle_Overview.pdf", Encoding.UTF8.GetBytes("not xccdf")));

		bool ok = StigZipReader.TryReadXccdfEntries(bundleZip, out IReadOnlyList<XccdfZipEntry> entries, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		Assert.Equal(4, entries.Count);
		Assert.Contains(entries, e => e.EntryPath == "U_NSX_STIG.zip > NSX-Manager-xccdf.xml");
		Assert.Contains(entries, e => e.EntryPath == "U_vSphere_STIG.zip > vSphere-ESXi-xccdf.xml");
	}

	[Fact]
	public void TryReadXccdfEntries_ZipOfZipsBeyondRecursionDepthBound_IsRejected()
	{
		byte[] innermostZip = BuildZip(("Inner-xccdf.xml", InventedBenchmark("inner")));
		byte[] bundleZip = innermostZip;
		for (int level = 0; level <= StigZipReader.MaxRecursionDepth; level++)
		{
			bundleZip = BuildZipOfZips(($"level-{level}.zip", bundleZip));
		}

		Assert.False(StigZipReader.TryReadXccdfEntries(bundleZip, out _, out string? error));
		Assert.Contains("recursion bound", error);
	}

	[Fact]
	public void TryReadXccdfEntries_ZipOfZipsExceedingCumulativeDecompressedBound_IsRejected()
	{
		// Many nested zips, each individually within every per-entry bound, whose
		// summed decompressed content exceeds the cumulative bound -- proves the
		// running total is threaded across the WHOLE recursive walk, not reset per
		// nesting level (issue #1073's "cumulative...across the whole recursive walk"
		// requirement).
		string chunkContent = new string('a', 8 * 1024 * 1024 - 4096);
		int chunkCount = (int)(StigZipReader.MaxCumulativeDecompressedBytes / chunkContent.Length) + 2;

		(string Name, byte[] Content)[] innerZips = new (string, byte[])[chunkCount];
		for (int i = 0; i < chunkCount; i++)
		{
			byte[] inner = BuildZip(($"Inner-{i}-xccdf.xml", chunkContent));
			innerZips[i] = ($"inner-{i}.zip", inner);
		}

		byte[] bundleZip = BuildZipOfZips(innerZips);

		Assert.False(StigZipReader.TryReadXccdfEntries(bundleZip, out _, out string? error));
		Assert.Contains("cumulative decompressed bound", error);
	}

	// The real-vendor-content sanity check formerly here has moved to
	// Waypoint.Tests.Core.ComplianceContent.ShapeInventory.RealContentConformanceTests
	// (issue #1077), which reports accept/reject counts for every vendor-content
	// parser from one place rather than one ad hoc pass-fail check per parser.

	private static string InventedBenchmark(string suffix) => $$"""
		<Benchmark id="xccdf_invented.example_benchmark_{{suffix}}_EX-1-0_STIG">
		  <title>Invented Example STIG {{suffix}}</title>
		  <version update="1">1</version>
		  <Rule id="SV-1-{{suffix}}" severity="high"><title>r</title></Rule>
		</Benchmark>
		""";

	private static byte[] BuildZip(params (string Name, string Content)[] entries) =>
		BuildZipBytes(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

	/// <summary>Zip-of-zips fixture helper: an entry's content is itself pre-built zip bytes.</summary>
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
