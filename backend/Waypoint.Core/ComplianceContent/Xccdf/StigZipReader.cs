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

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>
/// Safely locates and reads the single XCCDF XML entry out of an uploaded/synchronized
/// DISA STIG <c>.zip</c> package -- issue #730 AC "Zip/XML parsing has size, path
/// traversal, entity-expansion, and malformed-input protections". This never extracts
/// to disk (unlike <c>ManagedToolDistributionInstaller</c>'s trusted-after-signature-
/// verification tar install): a STIG zip's only useful output for this importer is the
/// XCCDF entry's in-memory text, so there is no filesystem write surface to guard --
/// only zip-slip-style entry NAMES (used solely to pick the right entry, never to
/// build a filesystem path) and decompression-bomb-style entry SIZES.
/// </summary>
public static class StigZipReader
{
	/// <summary>Bound on the zip archive itself (untrusted upload/sync input).</summary>
	public const long MaxArchiveBytes = 64 * 1024 * 1024;

	/// <summary>Bound on any single decompressed entry this reader will read into memory (decompression-bomb guard).</summary>
	public const long MaxEntryBytes = XccdfParser.MaxDocumentBytes;

	/// <summary>Bound on the number of entries a package may declare (guards a zip bomb built from many small entries rather than one huge one).</summary>
	public const int MaxEntryCount = 10_000;

	/// <summary>
	/// Attempts to find and read the XCCDF <c>Benchmark</c> XML text from
	/// <paramref name="zipBytes"/>. Returns <see langword="null"/> plus an actionable
	/// <paramref name="error"/> on any oversized/malformed/ambiguous/unsafe package;
	/// never throws for untrusted content.
	/// </summary>
	public static string? TryReadXccdfEntry(byte[]? zipBytes, out string? error)
	{
		if (zipBytes is null || zipBytes.Length == 0)
		{
			error = "STIG package is empty or missing";
			return null;
		}

		if (zipBytes.Length > MaxArchiveBytes)
		{
			error = $"STIG package exceeds the {MaxArchiveBytes}-byte archive bound ({zipBytes.Length} bytes)";
			return null;
		}

		using MemoryStream archiveStream = new(zipBytes, writable: false);

		ZipArchive archive;
		try
		{
			archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
		}
		catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
		{
			error = $"STIG package is not a valid zip archive: {ex.Message}";
			return null;
		}

		using (archive)
		{
			if (archive.Entries.Count > MaxEntryCount)
			{
				error = $"STIG package declares more than the {MaxEntryCount}-entry bound ({archive.Entries.Count} entries)";
				return null;
			}

			List<ZipArchiveEntry> xccdfCandidates = [];
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				if (!IsSafeEntryName(entry.FullName))
				{
					error = $"STIG package entry '{entry.FullName}' has an unsafe path (absolute path or '..' traversal segment) and was rejected";
					return null;
				}

				if (entry.Length > MaxEntryBytes)
				{
					error = $"STIG package entry '{entry.FullName}' exceeds the {MaxEntryBytes}-byte per-entry bound ({entry.Length} bytes) -- rejected as a possible decompression bomb";
					return null;
				}

				// XCCDF entries end in "-xccdf.xml" by DISA publishing convention (e.g. an
				// invented "U_Example_STIG_V1R1_Manual-xccdf.xml" shape). Anything else
				// (OVAL, manual PDF, CCI mapping) is ignored -- this reader's only job is
				// finding the one benchmark document, never interpreting the rest of the
				// package.
				if (entry.FullName.EndsWith("-xccdf.xml", StringComparison.OrdinalIgnoreCase))
				{
					xccdfCandidates.Add(entry);
				}
			}

			if (xccdfCandidates.Count == 0)
			{
				error = "STIG package does not contain an entry ending in '-xccdf.xml'";
				return null;
			}

			if (xccdfCandidates.Count > 1)
			{
				error = $"STIG package contains {xccdfCandidates.Count} '-xccdf.xml' entries; exactly one is required to identify the benchmark unambiguously";
				return null;
			}

			ZipArchiveEntry xccdfEntry = xccdfCandidates[0];
			try
			{
				using Stream entryStream = xccdfEntry.Open();
				using MemoryStream buffer = new();

				// Read with an explicit running-total cap rather than trusting the
				// entry's declared (attacker-controlled) Length header -- a crafted zip
				// can under-declare Length while the decompressed stream keeps producing
				// bytes.
				byte[] chunk = new byte[81920];
				long total = 0;
				int read;
				while ((read = entryStream.Read(chunk, 0, chunk.Length)) > 0)
				{
					total += read;
					if (total > MaxEntryBytes)
					{
						error = $"STIG package entry '{xccdfEntry.FullName}' decompressed beyond the {MaxEntryBytes}-byte bound -- rejected as a possible decompression bomb";
						return null;
					}

					buffer.Write(chunk, 0, read);
				}

				error = null;
				return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException)
			{
				error = $"STIG package entry '{xccdfEntry.FullName}' could not be read: {ex.Message}";
				return null;
			}
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
