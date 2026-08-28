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
/// Issue #1085 regression guard: <see cref="BenchmarkImporter.ImportZip(byte[]?)"/>'s
/// <c>ContentDigest</c> must be a pure function of parsed XCCDF content, never of the
/// archive entry path (<c>SourceEntryPath</c>) the content happened to arrive under.
/// This is a precondition for both cross-package dedup (#1074 -- the same benchmark
/// shipped in two vendor packages must digest identically) and identity hygiene (#1075
/// -- kind/identity must never derive from filename or entry path). Every fixture here
/// is invented.
/// </summary>
public sealed class BenchmarkImporterDigestPathIndependenceTests
{
	private const string InventedBenchmarkXml = """
		<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
		  <title>Invented Example STIG</title>
		  <version update="1">1</version>
		  <Rule id="SV-1" severity="high"><title>r1</title></Rule>
		  <Rule id="SV-2" severity="low"><title>r2</title></Rule>
		</Benchmark>
		""";

	/// <summary>
	/// Byte-identical XCCDF content, delivered under two entirely different entry paths
	/// from two differently-named packages -- one a flat single-entry zip, the other a
	/// nested zip-of-zips -- must digest identically so that cross-package duplicate
	/// content actually dedups (#1074). The distinct entry-path assertion proves the
	/// fixture isn't passing vacuously: the paths really did diverge.
	/// </summary>
	[Fact]
	public void ImportZip_SameContentFromDifferentPackagesAndEntryPaths_ProducesTheSameDigest()
	{
		// "Package" here means the top-level zip byte array handed to ImportZip -- the
		// importer only ever sees entry names/paths inside an archive, never the
		// archive's own filename, so the flat/nested SHAPE of each package (not a
		// fictional outer filename) is what drives the two different entry paths below.
		byte[] flatZip = BuildZip(("Alpha-xccdf.xml", InventedBenchmarkXml));

		byte[] innerZip = BuildZip(("Nested-xccdf.xml", InventedBenchmarkXml));
		byte[] nestedZip = BuildZipOfZips(("Bravo_Bundle/Bravo-Inner.zip", innerZip));

		IReadOnlyList<BenchmarkImportResult> flatResults = BenchmarkImporter.ImportZip(flatZip);
		IReadOnlyList<BenchmarkImportResult> nestedResults = BenchmarkImporter.ImportZip(nestedZip);

		BenchmarkImportCandidate flatCandidate = Assert.Single(flatResults).Candidate!;
		BenchmarkImportCandidate nestedCandidate = Assert.Single(nestedResults).Candidate!;

		// The fixture must actually exercise different entry paths -- otherwise this
		// test would pass even if SourceEntryPath were folded into the digest.
		Assert.NotEqual(flatCandidate.SourceEntryPath, nestedCandidate.SourceEntryPath);
		Assert.Contains("Alpha-xccdf.xml", flatCandidate.SourceEntryPath);
		Assert.Contains("Nested-xccdf.xml", nestedCandidate.SourceEntryPath);

		Assert.Equal(flatCandidate.ContentDigest, nestedCandidate.ContentDigest);
	}

	private static byte[] BuildZip((string Name, string Content) entry) =>
		BuildZipBytes([(entry.Name, Encoding.UTF8.GetBytes(entry.Content))]);

	/// <summary>Zip-of-zips fixture helper: an entry's content is itself pre-built zip bytes.</summary>
	private static byte[] BuildZipOfZips((string Name, byte[] Content) entry) => BuildZipBytes([entry]);

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
