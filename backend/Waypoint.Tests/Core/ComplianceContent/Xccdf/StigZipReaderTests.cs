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
/// Issue #730 deliverable 2's zip-side hostile-input suite: size, path-traversal
/// (zip-slip), and malformed-archive protections around the STIG-zip -> XCCDF
/// extraction step. Every archive built here is invented, in-memory, and contains only
/// miniature invented XCCDF text (never real STIG content).
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
	public void TryReadXccdfEntry_NullOrEmptyBytes_ReturnsActionableError()
	{
		Assert.Null(StigZipReader.TryReadXccdfEntry(null, out string? errorForNull));
		Assert.Contains("empty or missing", errorForNull);

		Assert.Null(StigZipReader.TryReadXccdfEntry([], out string? errorForEmpty));
		Assert.Contains("empty or missing", errorForEmpty);
	}

	[Fact]
	public void TryReadXccdfEntry_OversizedArchive_IsRejected()
	{
		byte[] oversized = new byte[StigZipReader.MaxArchiveBytes + 1];

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(oversized, out error));
		Assert.Contains("archive bound", error);
	}

	[Fact]
	public void TryReadXccdfEntry_NotAZipFile_ReturnsActionableErrorRatherThanThrowing()
	{
		byte[] notAZip = Encoding.UTF8.GetBytes("this is definitely not a zip archive");

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(notAZip, out error));
		Assert.Contains("not a valid zip archive", error);
	}

	[Fact]
	public void TryReadXccdfEntry_ValidPackage_ExtractsXccdfText()
	{
		byte[] zipBytes = BuildZip(("Invented_STIG_V1R1_Manual-xccdf.xml", InventedXccdf));

		string? xml = StigZipReader.TryReadXccdfEntry(zipBytes, out string? error);

		Assert.Null(error);
		Assert.NotNull(xml);
		Assert.Contains("xccdf_invented.example_benchmark_EX-1-0_STIG", xml);
	}

	[Fact]
	public void TryReadXccdfEntry_ZipSlipEntryName_IsRejected()
	{
		byte[] zipBytes = BuildZip(("../../etc/passwd-xccdf.xml", InventedXccdf));

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(zipBytes, out error));
		Assert.Contains("unsafe path", error);
	}

	[Fact]
	public void TryReadXccdfEntry_AbsolutePathEntryName_IsRejected()
	{
		byte[] zipBytes = BuildZip(("/etc/passwd-xccdf.xml", InventedXccdf));

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(zipBytes, out error));
		Assert.Contains("unsafe path", error);
	}

	[Fact]
	public void TryReadXccdfEntry_NoXccdfEntry_IsRejected()
	{
		byte[] zipBytes = BuildZip(("readme.txt", "not xccdf"));

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(zipBytes, out error));
		Assert.Contains("does not contain an entry ending in '-xccdf.xml'", error);
	}

	[Fact]
	public void TryReadXccdfEntry_MultipleXccdfEntries_IsRejectedAsAmbiguous()
	{
		byte[] zipBytes = BuildZip(
			("First_STIG-xccdf.xml", InventedXccdf),
			("Second_STIG-xccdf.xml", InventedXccdf));

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(zipBytes, out error));
		Assert.Contains("2 '-xccdf.xml' entries", error);
	}

	[Fact]
	public void TryReadXccdfEntry_TooManyEntries_IsRejected()
	{
		(string Name, string Content)[] entries = new (string, string)[StigZipReader.MaxEntryCount + 1];
		for (int i = 0; i < entries.Length; i++)
		{
			entries[i] = ($"file-{i}.txt", "x");
		}

		byte[] zipBytes = BuildZip(entries);

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(zipBytes, out error));
		Assert.Contains("entry bound", error);
	}

	[Fact]
	public void TryReadXccdfEntry_EntryDeclaringOversizedLength_IsRejectedBeforeReadingIntoMemory()
	{
		// A well-formed small zip whose single XCCDF-named entry's UNCOMPRESSED content
		// exceeds the per-entry bound -- proves the reader checks actual decompressed
		// size, not just the archive's overall byte length, guarding against a
		// decompression-bomb-style entry.
		string oversizedContent = new string('a', (int)StigZipReader.MaxEntryBytes + 1);
		byte[] zipBytes = BuildZip(("Oversized_STIG-xccdf.xml", oversizedContent));

		string? error;
		Assert.Null(StigZipReader.TryReadXccdfEntry(zipBytes, out error));
		Assert.Contains("per-entry bound", error);
	}

	private static byte[] BuildZip(params (string Name, string Content)[] entries)
	{
		using MemoryStream stream = new();
		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string name, string content) in entries)
			{
				ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
				using Stream entryStream = entry.Open();
				byte[] bytes = Encoding.UTF8.GetBytes(content);
				entryStream.Write(bytes, 0, bytes.Length);
			}
		}

		return stream.ToArray();
	}
}
