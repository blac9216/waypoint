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

using System.Formats.Tar;
using System.IO.Compression;

namespace Waypoint.Tests.Support;

/// <summary>
/// Builds invented <c>.tar.gz</c> fixture archives modeling the sibling
/// <c>../vcf-docker-download/Dockerfile</c> VCFDT distribution layout
/// (<c>bin/vcf-download-tool</c> + <c>lib/</c>) for issue #686's safe-extraction/
/// activation tests. All content is invented/synthetic -- no real Broadcom binary or
/// vendor bytes are used anywhere in this repository.
/// </summary>
public static class ManagedToolDistributionFixture
{
	/// <summary><c>/bin/true</c> is a real, tiny, dynamically linked ELF that accepts and ignores any arguments and always exits 0 -- a safe stand-in "real executable" for smoke-test fixtures without shipping any vendor or built binary in the repo.</summary>
	private const string RealExecutableSource = "/bin/true";

	/// <summary>
	/// Writes a well-formed <c>.tar.gz</c> at <paramref name="archivePath"/> containing
	/// <c>bin/vcf-download-tool</c> (a copy of <see cref="RealExecutableSource"/>, so the
	/// installer's bounded smoke-test execution genuinely succeeds) and a non-empty
	/// <c>lib/</c> directory with one invented shared-library-shaped file.
	/// </summary>
	public static void WriteHappyPathArchive(string archivePath, string? extraLibFileName = null)
	{
		using MemoryStream entriesBuffer = new();
		WriteArchive(archivePath, writer =>
		{
			AddDirectory(writer, "bin");
			AddRealExecutable(writer, "bin/vcf-download-tool");
			AddDirectory(writer, "lib");
			AddRegularFile(writer, extraLibFileName ?? "lib/libvcfdt-fixture.so.1", "invented shared library bytes for the fixture, not real vendor content"u8.ToArray());
		});
	}

	/// <summary>An archive whose "executable" is actually the archive's own compressed bytes -- the exact issue #686 regression (the archive was previously copied straight into the executable path and failed with <c>Exec format error</c>). Used to prove such an archive is rejected and never activated, this time and on any retry.</summary>
	public static void WriteArchiveAsExecutableArchive(string archivePath)
	{
		byte[] archiveLikeBytes = "\x1f\x8b totally-not-an-elf invented bytes standing in for a mis-copied archive"u8.ToArray();
		WriteArchive(archivePath, writer =>
		{
			AddDirectory(writer, "bin");
			AddRegularFile(writer, "bin/vcf-download-tool", archiveLikeBytes);
			AddDirectory(writer, "lib");
			AddRegularFile(writer, "lib/libvcfdt-fixture.so.1", "invented shared library bytes"u8.ToArray());
		});
	}

	/// <summary>Missing the required <c>lib/</c> directory entirely -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.MissingLayout"/>.</summary>
	public static void WriteMissingLibArchive(string archivePath)
	{
		WriteArchive(archivePath, writer =>
		{
			AddDirectory(writer, "bin");
			AddRealExecutable(writer, "bin/vcf-download-tool");
		});
	}

	/// <summary>Missing the expected executable path entirely -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.MissingLayout"/>.</summary>
	public static void WriteMissingExecutableArchive(string archivePath)
	{
		WriteArchive(archivePath, writer =>
		{
			AddDirectory(writer, "lib");
			AddRegularFile(writer, "lib/libvcfdt-fixture.so.1", "invented shared library bytes"u8.ToArray());
		});
	}

	/// <summary>An entry using an absolute path -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.UnsafePath"/>.</summary>
	public static void WriteAbsolutePathArchive(string archivePath)
	{
		WriteArchive(archivePath, writer =>
		{
			AddRegularFile(writer, "/etc/waypoint-fixture-canary", "invented traversal-attempt content"u8.ToArray());
		});
	}

	/// <summary>An entry using a <c>..</c> traversal segment to escape the extraction root -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.UnsafePath"/>.</summary>
	public static void WriteTraversalArchive(string archivePath)
	{
		WriteArchive(archivePath, writer =>
		{
			AddRegularFile(writer, "../../etc/waypoint-fixture-canary", "invented traversal-attempt content"u8.ToArray());
		});
	}

	/// <summary>A symlink entry that targets a path outside the extraction root -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.UnsafeLink"/>.</summary>
	public static void WriteSymlinkEscapeArchive(string archivePath)
	{
		WriteArchive(archivePath, writer =>
		{
			AddDirectory(writer, "bin");
			AddSymlink(writer, "bin/vcf-download-tool", "../../../../etc/waypoint-fixture-canary");
			AddDirectory(writer, "lib");
			AddRegularFile(writer, "lib/libvcfdt-fixture.so.1", "invented shared library bytes"u8.ToArray());
		});
	}

	/// <summary>A special-file entry (a FIFO) -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.SpecialFile"/>.</summary>
	public static void WriteSpecialFileArchive(string archivePath)
	{
		WriteArchive(archivePath, writer =>
		{
			PaxTarEntry fifo = new(TarEntryType.Fifo, "bin/oddity") { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
			writer.WriteEntry(fifo);
		});
	}

	/// <summary>An archive whose entry count exceeds <paramref name="maxEntries"/> -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.ExpansionLimitExceeded"/>.</summary>
	public static void WriteTooManyEntriesArchive(string archivePath, int maxEntries)
	{
		WriteArchive(archivePath, writer =>
		{
			for (int i = 0; i <= maxEntries; i++)
			{
				AddRegularFile(writer, $"junk/file-{i}", [0]);
			}
		});
	}

	/// <summary>An archive whose single entry's declared/actual size exceeds <paramref name="maxBytes"/> -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.ExpansionLimitExceeded"/>.</summary>
	public static void WriteOversizedArchive(string archivePath, long maxBytes)
	{
		WriteArchive(archivePath, writer =>
		{
			byte[] oversized = new byte[maxBytes + 1024];
			AddRegularFile(writer, "bin/vcf-download-tool", oversized);
		});
	}

	/// <summary>Not a valid gzip/tar stream at all -- <see cref="Waypoint.Core.Downloads.ManagedToolDistributionRejectionKind.MalformedArchive"/>.</summary>
	public static void WriteMalformedArchive(string archivePath)
	{
		File.WriteAllBytes(archivePath, "this is not a gzip stream at all, invented garbage bytes"u8.ToArray());
	}

	private static void WriteArchive(string archivePath, Action<TarWriter> populate)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
		using FileStream fileStream = File.Create(archivePath);
		using GZipStream gzipStream = new(fileStream, CompressionLevel.Fastest);
		using TarWriter writer = new(gzipStream, TarEntryFormat.Pax, leaveOpen: false);
		populate(writer);
	}

	private static void AddDirectory(TarWriter writer, string name)
	{
		PaxTarEntry entry = new(TarEntryType.Directory, name)
		{
			Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
				| UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
		};
		writer.WriteEntry(entry);
	}

	private static void AddRegularFile(TarWriter writer, string name, byte[] content)
	{
		PaxTarEntry entry = new(TarEntryType.RegularFile, name)
		{
			Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
			DataStream = new MemoryStream(content),
		};
		writer.WriteEntry(entry);
	}

	private static void AddRealExecutable(TarWriter writer, string name)
	{
		byte[] bytes = File.ReadAllBytes(RealExecutableSource);
		PaxTarEntry entry = new(TarEntryType.RegularFile, name)
		{
			Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
				| UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
			DataStream = new MemoryStream(bytes),
		};
		writer.WriteEntry(entry);
	}

	private static void AddSymlink(TarWriter writer, string name, string target)
	{
		PaxTarEntry entry = new(TarEntryType.SymbolicLink, name) { LinkName = target };
		writer.WriteEntry(entry);
	}
}
