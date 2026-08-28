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

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>
/// One XCCDF <c>Benchmark</c> XML document found inside a STIG package (issue #1073: a
/// package yields N of these, never one-or-error). <see cref="EntryPath"/> is a
/// diagnostic-only breadcrumb (e.g. <c>"U_NSX_STIG.zip &gt; NSX-Manager-xccdf.xml"</c>
/// for a zip-of-zips) so callers/errors can say which entry a candidate came from; it
/// is NEVER used to derive benchmark identity -- identity comes only from the parsed
/// XCCDF's own <c>Benchmark</c> metadata (id/title/version/release), never the archive
/// or entry filename.
/// </summary>
public sealed record XccdfZipEntry(string EntryPath, string XmlText);

/// <summary>
/// Safely locates and reads every XCCDF <c>Benchmark</c> XML document out of an
/// uploaded/synchronized DISA STIG <c>.zip</c> package -- issue #730 AC "Zip/XML
/// parsing has size, path traversal, entity-expansion, and malformed-input
/// protections", extended by issue #1073 to the three real vendor package shapes: flat
/// multi-XCCDF (sibling entries), nested-directory multi-XCCDF (one XCCDF per
/// component subdirectory), and zip-of-zips (entries that are themselves component
/// STIG zips, e.g. VCF bundles). This never extracts to disk (unlike
/// <c>ManagedToolDistributionInstaller</c>'s trusted-after-signature-verification tar
/// install): a STIG zip's only useful output for this importer is each XCCDF entry's
/// in-memory text, so there is no filesystem write surface to guard -- only
/// zip-slip-style entry NAMES (used solely to pick/recurse into entries, never to
/// build a filesystem path) and decompression-bomb-style entry/cumulative SIZES.
/// </summary>
public static class StigZipReader
{
	/// <summary>
	/// Bound on the zip archive itself (untrusted upload/sync input), and on any single
	/// nested zip-of-zips entry (issue #1073) -- a nested package is itself "an
	/// archive" and gets the same size discipline as the top-level one. The largest
	/// real vendor package measured (a VCF bundle nesting three component zips) is
	/// ~15 MB; 64 MB keeps >4x headroom for future/larger content without being an
	/// unbounded trust of attacker-controlled input.
	/// </summary>
	public const long MaxArchiveBytes = 64 * 1024 * 1024;

	/// <summary>Bound on any single decompressed XCCDF XML entry this reader will read into memory (decompression-bomb guard).</summary>
	public const long MaxEntryBytes = XccdfParser.MaxDocumentBytes;

	/// <summary>
	/// Bound on the number of entries a single zip level may declare (guards a zip
	/// bomb built from many small entries rather than one huge one). Also enforced as
	/// a shared, decrementing budget across the WHOLE recursive zip-of-zips walk (issue
	/// #1073 AC "entry-count bound") -- otherwise many small nested archives, each
	/// individually under this bound, could sum past it.
	/// </summary>
	public const int MaxEntryCount = 10_000;

	/// <summary>
	/// Bound on zip-of-zips nesting (issue #1073 AC "explicit depth bound"). Depth 0 is
	/// the top-level package; every real vendor bundle measured nests exactly one level
	/// (e.g. a VCF bundle's top-level zip containing whole NSX/vSphere/SDDC-Manager
	/// component zips). This allows one extra level of headroom over that measured
	/// need without being unbounded.
	/// </summary>
	public const int MaxRecursionDepth = 2;

	/// <summary>
	/// Cumulative decompressed-byte bound across the ENTIRE recursive walk of one
	/// package -- issue #1073's security-critical requirement: "a per-entry bound alone
	/// does not stop a nested bomb". Each entry/nested-archive already has its own
	/// bound, but a package built from many entries each just under that bound could
	/// still amplify unboundedly through recursion without this running total. The
	/// largest real vendor bundle measured (VCF 4.x readiness guide, walked
	/// recursively through its nested NSX-T + vSphere + SDDC-Manager component zips)
	/// decompresses to ~32 MB total; 256 MB keeps 8x headroom.
	/// </summary>
	public const long MaxCumulativeDecompressedBytes = 4 * MaxArchiveBytes;

	/// <summary>
	/// Attempts to find and read every XCCDF <c>Benchmark</c> XML document from
	/// <paramref name="zipBytes"/>, recursing into nested zip-of-zips entries under
	/// bounded depth/entry-count/cumulative-size limits. Returns
	/// <see langword="false"/> plus an actionable <paramref name="error"/> on any
	/// oversized/malformed/unsafe package, or when the package (recursively) contains
	/// no XCCDF entry at all; never throws for untrusted content. A per-entry XML parse
	/// failure does not fail the whole package -- entries are independent, and the
	/// caller (<see cref="BenchmarkImporter"/>) surfaces each one's own outcome.
	/// </summary>
	public static bool TryReadXccdfEntries(byte[]? zipBytes, out IReadOnlyList<XccdfZipEntry> entries, out string? error)
	{
		entries = [];

		if (zipBytes is null || zipBytes.Length == 0)
		{
			error = "STIG package is empty or missing";
			return false;
		}

		if (zipBytes.Length > MaxArchiveBytes)
		{
			error = $"STIG package exceeds the {MaxArchiveBytes}-byte archive bound ({zipBytes.Length} bytes)";
			return false;
		}

		List<XccdfZipEntry> found = [];
		long cumulativeBytes = zipBytes.Length;
		int remainingEntryBudget = MaxEntryCount;

		if (!TryWalk(zipBytes, entryPathPrefix: null, depth: 0, ref cumulativeBytes, ref remainingEntryBudget, found, out error))
		{
			entries = [];
			return false;
		}

		if (found.Count == 0)
		{
			error = "STIG package does not contain any entry ending in '-xccdf.xml' (checked recursively through nested zip-of-zips entries)";
			entries = [];
			return false;
		}

		error = null;
		entries = found;
		return true;
	}

	/// <summary>
	/// Walks one zip level, collecting XCCDF entries into <paramref name="found"/> and
	/// recursing into nested ".zip" entries up to <see cref="MaxRecursionDepth"/>.
	/// <paramref name="cumulativeBytes"/> and <paramref name="remainingEntryBudget"/>
	/// are threaded by reference across the whole recursive walk of one top-level
	/// package -- they are NOT reset per nesting level, which is what makes the
	/// cumulative-size and total-entry-count bounds actually bound the whole walk
	/// rather than just each level in isolation.
	/// </summary>
	private static bool TryWalk(
		byte[] zipBytes,
		string? entryPathPrefix,
		int depth,
		ref long cumulativeBytes,
		ref int remainingEntryBudget,
		List<XccdfZipEntry> found,
		out string? error)
	{
		using MemoryStream archiveStream = new(zipBytes, writable: false);

		ZipArchive archive;
		try
		{
			archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
		}
		catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
		{
			error = $"STIG package is not a valid zip archive: {ex.Message}";
			return false;
		}

		using (archive)
		{
			if (archive.Entries.Count > MaxEntryCount)
			{
				error = $"STIG package declares more than the {MaxEntryCount}-entry bound ({archive.Entries.Count} entries)";
				return false;
			}

			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				remainingEntryBudget--;
				if (remainingEntryBudget < 0)
				{
					error = $"STIG package declares more than the {MaxEntryCount}-entry bound across its nested zip-of-zips entries";
					return false;
				}

				if (!IsSafeEntryName(entry.FullName))
				{
					error = $"STIG package entry '{entry.FullName}' has an unsafe path (absolute path or '..' traversal segment) and was rejected";
					return false;
				}

				string displayPath = entryPathPrefix is null ? entry.FullName : $"{entryPathPrefix} > {entry.FullName}";

				// XCCDF entries end in "-xccdf.xml" by DISA publishing convention (e.g. an
				// invented "U_Example_STIG_V1R1_Manual-xccdf.xml" shape); zip-of-zips
				// entries end in ".zip" (e.g. a VCF bundle's nested component packages).
				// Entry names are used ONLY to select/recurse -- never to build a
				// filesystem path (the zip-slip guard above already rejected unsafe ones).
				// Anything else (OVAL, manual PDF, CCI mapping, audit rules) is ignored.
				bool isXccdf = entry.FullName.EndsWith("-xccdf.xml", StringComparison.OrdinalIgnoreCase);
				bool isNestedZip = !isXccdf && entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

				if (!isXccdf && !isNestedZip)
				{
					continue;
				}

				long perEntryBound = isXccdf ? MaxEntryBytes : MaxArchiveBytes;
				if (entry.Length > perEntryBound)
				{
					error = $"STIG package entry '{displayPath}' exceeds the {perEntryBound}-byte per-entry bound ({entry.Length} bytes) -- rejected as a possible decompression bomb";
					return false;
				}

				if (!TryReadEntryBytes(entry, perEntryBound, displayPath, ref cumulativeBytes, out byte[]? bytes, out error))
				{
					return false;
				}

				if (isXccdf)
				{
					found.Add(new XccdfZipEntry(displayPath, Encoding.UTF8.GetString(bytes!)));
					continue;
				}

				if (depth + 1 > MaxRecursionDepth)
				{
					error = $"STIG package nests a zip entry '{displayPath}' beyond the {MaxRecursionDepth}-level recursion bound -- rejected as a possible nested decompression bomb";
					return false;
				}

				if (!TryWalk(bytes!, displayPath, depth + 1, ref cumulativeBytes, ref remainingEntryBudget, found, out error))
				{
					return false;
				}
			}
		}

		error = null;
		return true;
	}

	/// <summary>
	/// Reads one entry's decompressed bytes with an explicit running-total cap rather
	/// than trusting the entry's declared (attacker-controlled) <c>Length</c> header --
	/// a crafted zip can under-declare <c>Length</c> while the decompressed stream
	/// keeps producing bytes (issue #829). Also advances the whole-walk cumulative
	/// bound (issue #1073) so no single entry/nested-archive bound alone has to stop a
	/// bomb built from many entries each individually within bounds.
	/// </summary>
	private static bool TryReadEntryBytes(
		ZipArchiveEntry entry,
		long perEntryBound,
		string displayPath,
		ref long cumulativeBytes,
		out byte[]? bytes,
		out string? error)
	{
		try
		{
			using Stream entryStream = entry.Open();
			using MemoryStream buffer = new();

			byte[] chunk = new byte[81920];
			long total = 0;
			int read;
			while ((read = entryStream.Read(chunk, 0, chunk.Length)) > 0)
			{
				total += read;
				if (total > perEntryBound)
				{
					error = $"STIG package entry '{displayPath}' decompressed beyond the {perEntryBound}-byte bound -- rejected as a possible decompression bomb";
					bytes = null;
					return false;
				}

				cumulativeBytes += read;
				if (cumulativeBytes > MaxCumulativeDecompressedBytes)
				{
					error = $"STIG package exceeds the {MaxCumulativeDecompressedBytes}-byte cumulative decompressed bound across its nested zip-of-zips entries -- rejected as a possible decompression bomb";
					bytes = null;
					return false;
				}

				buffer.Write(chunk, 0, read);
			}

			error = null;
			bytes = buffer.ToArray();
			return true;
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException)
		{
			error = $"STIG package entry '{displayPath}' could not be read: {ex.Message}";
			bytes = null;
			return false;
		}
	}

	/// <summary>
	/// Zip-slip guard: rejects any entry name that is rooted/absolute or contains a
	/// <c>..</c> traversal segment. This reader never writes an entry to disk, but the
	/// same unsafe names are refused outright rather than merely "not extracted" -- a
	/// package that declares them is already not a well-formed DISA STIG export.
	/// </summary>
	private static bool IsSafeEntryName(string entryName)
	{
		if (string.IsNullOrWhiteSpace(entryName))
		{
			return false;
		}

		string normalized = entryName.Replace('\\', '/');
		if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
		{
			return false;
		}

		string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.All(segment => segment != "..");
	}
}
