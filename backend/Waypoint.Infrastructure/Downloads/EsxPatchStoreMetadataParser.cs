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
using System.Security.Cryptography;
using System.Xml;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IEsxPatchStoreMetadataParser"/>
/// <remarks>
/// Reads only filesystem/zip content it is pointed at -- no network, no PowerShell,
/// no dependency on <c>vmware-umds</c> or <c>vcf-download-tool</c> being installed.
/// Untrusted-input discipline mirrors <c>XccdfParser</c> (issue #730): every XML
/// document parsed here (the consolidated index, each vendor's consolidated metadata
/// index, and each metadata zip's inner XML entries) goes through
/// <see cref="XmlReaderSettings"/> with DTD processing prohibited and no resolver, and
/// every bound is a size/count cap rather than an assumption about a well-behaved
/// store -- a store directory is operator-managed, not attacker-controlled, but a
/// truncated or half-synced download must never crash the parser (issue #1446 AC
/// implications; matches <c>Test-MetadataZip</c>'s tolerant-on-corrupt-zip contract in
/// the sibling reference).
/// </remarks>
public sealed class EsxPatchStoreMetadataParser : IEsxPatchStoreMetadataParser
{
	private const string HostupdateDirName = "hostupdate";
	private const string ConsolidatedIndexFileName = "__hostupdate20-consolidated-index__.xml";
	private const string ConsolidatedMetadataIndexFileName = "__hostupdate20-consolidated-metadata-index__.xml";
	private const string VendorIndexFileName = "vendor-index.xml";
	private const string VibsEntryDirPrefix = "vibs/";

	/// <summary>
	/// The tool's download-time staging tree (issue #1164): a sibling of
	/// <c>hostupdate/</c> under the 9.1 patch-store root that this parser must never
	/// walk into. It is not a valid vendor code, so a directory with this exact name
	/// found while enumerating vendor directories is skipped and warned about rather
	/// than treated as an empty/unknown vendor.
	/// </summary>
	private const string StagingTreeDirName = "hardlink-hostupdate";

	private static readonly string[] Depot91RelativeSegments = ["PROD", "COMP", "ESX_HOST", "patch-store"];

	/// <summary>Bound on any single index/metadata XML document this parser will attempt (untrusted-input discipline, matches <c>XccdfParser.MaxDocumentBytes</c>).</summary>
	public const int MaxXmlBytes = 8 * 1024 * 1024;

	/// <summary>Bound on entries a single metadata zip may contain before this parser gives up on it as a warning rather than a hang.</summary>
	public const int MaxZipEntries = 20_000;

	/// <summary>Bound on any single zip entry's uncompressed size this parser will read into memory.</summary>
	public const long MaxZipEntryBytes = 32 * 1024 * 1024;

	public EsxPatchStoreParseResult Parse(string storeRoot, EsxPatchStoreLayout? layout = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

		if (!Directory.Exists(storeRoot))
		{
			return EsxPatchStoreParseResult.Failed($"ESX patch store root does not exist: '{storeRoot}'");
		}

		(string HostupdateRoot, EsxPatchStoreLayout Layout)? resolved = ResolveHostupdateRoot(storeRoot, layout);
		if (resolved is null)
		{
			return layout is null
				? EsxPatchStoreParseResult.Failed(
					$"No '{HostupdateDirName}' directory found under '{storeRoot}' in either the legacy (store-root) or the VCFDT 9.1 (PROD/COMP/ESX_HOST/patch-store) layout.")
				: EsxPatchStoreParseResult.Failed(
					$"No '{HostupdateDirName}' directory found under '{storeRoot}' for the requested {layout} layout.");
		}

		(string hostupdateRoot, EsxPatchStoreLayout resolvedLayout) = resolved.Value;

		List<string> warnings = [];
		SortedSet<string> vendorCodes = new(StringComparer.Ordinal);
		foreach (string indexVendorCode in ParseConsolidatedIndexVendorCodes(hostupdateRoot, warnings))
		{
			vendorCodes.Add(indexVendorCode);
		}

		List<EsxPatchStoreMetadataBundle> bundles = [];

		foreach (string vendorDir in SafeEnumerateDirectories(hostupdateRoot, warnings))
		{
			string vendorCode = Path.GetFileName(vendorDir);

			// Never descend into the tool's own staging tree (#1164) -- it is not a
			// vendor and re-walking it would double-count content that is also present
			// under its real vendor directories once a download finalizes.
			if (string.Equals(vendorCode, StagingTreeDirName, StringComparison.OrdinalIgnoreCase))
			{
				warnings.Add($"Skipped '{vendorCode}' under '{hostupdateRoot}' -- it is the download tool's staging tree (#1164), not a vendor directory.");
				continue;
			}

			vendorCodes.Add(vendorCode);
			ParseVendorMetadataIndex(vendorDir, vendorCode, bundles, warnings);
		}

		EsxPatchStoreMetadata metadata = new(
			StoreRoot: storeRoot,
			Layout: resolvedLayout,
			HostupdateRoot: hostupdateRoot,
			VendorCodes: [.. vendorCodes],
			Bundles: bundles,
			Warnings: warnings);

		return EsxPatchStoreParseResult.Ok(metadata);
	}

	/// <summary>
	/// Probes for <c>hostupdate/</c> under each known layout root. When
	/// <paramref name="forcedLayout"/> is given, only that layout's path is checked.
	/// Legacy is preferred on auto-detect: a store root that happens to also have
	/// <c>PROD/COMP/ESX_HOST/patch-store/hostupdate</c> nested inside it (e.g. a
	/// depot root reused as a legacy sidecar path by an unusual layout) resolves to
	/// its own top-level <c>hostupdate/</c> first.
	/// </summary>
	private static (string HostupdateRoot, EsxPatchStoreLayout Layout)? ResolveHostupdateRoot(string storeRoot, EsxPatchStoreLayout? forcedLayout)
	{
		string legacyPath = Path.Combine(storeRoot, HostupdateDirName);
		string depot91Path = Path.Combine([storeRoot, .. Depot91RelativeSegments, HostupdateDirName]);

		if (forcedLayout == EsxPatchStoreLayout.Legacy)
		{
			return Directory.Exists(legacyPath) ? (legacyPath, EsxPatchStoreLayout.Legacy) : null;
		}

		if (forcedLayout == EsxPatchStoreLayout.Depot91)
		{
			return Directory.Exists(depot91Path) ? (depot91Path, EsxPatchStoreLayout.Depot91) : null;
		}

		if (Directory.Exists(legacyPath))
		{
			return (legacyPath, EsxPatchStoreLayout.Legacy);
		}

		if (Directory.Exists(depot91Path))
		{
			return (depot91Path, EsxPatchStoreLayout.Depot91);
		}

		return null;
	}

	/// <summary>
	/// Best-effort read of the top-level consolidated index's vendor list (issue
	/// #1446: "walks the consolidated index"). A missing or unparseable index is a
	/// warning, not a failure -- the per-directory walk below is the ground truth for
	/// what can actually be parsed, and does not depend on this succeeding.
	/// </summary>
	private static List<string> ParseConsolidatedIndexVendorCodes(string hostupdateRoot, List<string> warnings)
	{
		string indexPath = Path.Combine(hostupdateRoot, ConsolidatedIndexFileName);
		List<string> codes = [];

		if (!File.Exists(indexPath))
		{
			warnings.Add($"Consolidated index not found: '{indexPath}' (vendor list will come from the directory listing only).");
			return codes;
		}

		XmlDocument? document = TryLoadXml(indexPath, warnings, "consolidated index");
		if (document?.DocumentElement is null)
		{
			return codes;
		}

		foreach (XmlElement vendorElement in EnumerateDescendants(document.DocumentElement, "vendor"))
		{
			string? code = vendorElement.InnerText?.Trim();
			if (!string.IsNullOrEmpty(code))
			{
				codes.Add(code);
			}
		}

		return codes;
	}

	/// <summary>
	/// Parses one vendor directory's consolidated metadata index and resolves every
	/// metadata entry it names into a content-identified <see cref="EsxPatchStoreMetadataBundle"/>.
	/// </summary>
	private static void ParseVendorMetadataIndex(string vendorDir, string vendorCode, List<EsxPatchStoreMetadataBundle> bundles, List<string> warnings)
	{
		string indexPath = Path.Combine(vendorDir, ConsolidatedMetadataIndexFileName);
		if (!File.Exists(indexPath))
		{
			warnings.Add($"Vendor '{vendorCode}': no consolidated metadata index at '{indexPath}'.");
			return;
		}

		XmlDocument? document = TryLoadXml(indexPath, warnings, $"vendor '{vendorCode}' consolidated metadata index");
		if (document?.DocumentElement is null)
		{
			return;
		}

		foreach (XmlElement metadataElement in EnumerateDescendants(document.DocumentElement, "metadata"))
		{
			string? rawLocation = FindValue(metadataElement, "relativePath") ?? FindValue(metadataElement, "url");
			if (string.IsNullOrWhiteSpace(rawLocation))
			{
				warnings.Add($"Vendor '{vendorCode}': a metadata entry has neither a relativePath nor a url -- skipped.");
				continue;
			}

			// The index's location field is historically a full download URL (legacy
			// UMDS) or a bare relative path (9.1 micro-depots); both resolve to a
			// same-named file directly inside the vendor directory once acquired.
			string fileName = Path.GetFileName(rawLocation.Trim().Replace('\\', '/'));
			if (string.IsNullOrWhiteSpace(fileName))
			{
				warnings.Add($"Vendor '{vendorCode}': metadata entry location '{rawLocation}' did not resolve to a filename -- skipped.");
				continue;
			}

			string zipPath = Path.Combine(vendorDir, fileName);
			if (!File.Exists(zipPath))
			{
				warnings.Add($"Vendor '{vendorCode}': metadata zip '{fileName}' referenced by the index was not found on disk.");
				continue;
			}

			EsxPatchStoreMetadataBundle? bundle = TryParseMetadataZip(
				zipPath,
				fileName,
				vendorCode,
				productId: FindValue(metadataElement, "productId"),
				version: FindValue(metadataElement, "version"),
				channelName: FindValue(metadataElement, "channelName"),
				warnings);

			if (bundle is not null)
			{
				bundles.Add(bundle);
			}
		}
	}

	/// <summary>
	/// Opens one metadata zip, computes its content-identity key from the zip's own
	/// bytes (never its filename), and resolves the VIB references inside it. Any
	/// failure to open or read the zip is a warning, not an exception -- a truncated
	/// or half-synced download must not abort the rest of the store's parse.
	/// </summary>
	private static EsxPatchStoreMetadataBundle? TryParseMetadataZip(
		string zipPath, string displayRelativePath, string vendorCode, string? productId, string? version, string? channelName, List<string> warnings)
	{
		string contentKey;
		try
		{
			using FileStream stream = File.OpenRead(zipPath);
			contentKey = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"Vendor '{vendorCode}': could not read '{displayRelativePath}' to compute its content key: {ex.Message}");
			return null;
		}

		List<EsxPatchStoreVibReference> vibs;
		try
		{
			vibs = ParseVibReferences(zipPath, vendorCode, displayRelativePath, warnings);
		}
		catch (InvalidDataException ex)
		{
			warnings.Add($"Vendor '{vendorCode}': '{displayRelativePath}' is not a valid zip archive: {ex.Message}");
			return new EsxPatchStoreMetadataBundle(vendorCode, contentKey, displayRelativePath, productId, version, channelName, []);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The content-key read above already opened and hashed the file
			// successfully; a failure here means it became unreadable in the
			// window between that read and this one (concurrent vendor-tool sync
			// deleting/locking it). Never let that escape -- it is exactly the
			// kind of transient store condition this parser's "never throws"
			// contract exists for.
			warnings.Add($"Vendor '{vendorCode}': could not open '{displayRelativePath}' to read its VIB references: {ex.GetType().Name}: {ex.Message}");
			return new EsxPatchStoreMetadataBundle(vendorCode, contentKey, displayRelativePath, productId, version, channelName, []);
		}

		return new EsxPatchStoreMetadataBundle(vendorCode, contentKey, displayRelativePath, productId, version, channelName, vibs);
	}

	/// <summary>
	/// Prefers the checksum-bearing <c>vibs/*.xml</c> entries research #1028 found
	/// (each carries <c>relative-path</c>/<c>relativePath</c> and a sha-256
	/// <c>checksum</c>); when a zip carries none, falls back to scanning its one
	/// top-level metadata XML entry (excluding <c>vendor-index.xml</c>) for bare
	/// <c>relativePath</c> elements, matching the sibling reference's
	/// <c>Test-MetadataZip</c> fallback (no checksum available there).
	/// </summary>
	private static List<EsxPatchStoreVibReference> ParseVibReferences(string zipPath, string vendorCode, string displayRelativePath, List<string> warnings)
	{
		using ZipArchive archive = ZipFile.OpenRead(zipPath);

		if (archive.Entries.Count > MaxZipEntries)
		{
			warnings.Add($"Vendor '{vendorCode}': '{displayRelativePath}' has {archive.Entries.Count} entries, over the {MaxZipEntries} bound -- skipped.");
			return [];
		}

		Dictionary<string, EsxPatchStoreVibReference> byRelativePath = new(StringComparer.Ordinal);

		List<ZipArchiveEntry> vibXmlEntries = [.. archive.Entries.Where(entry =>
			entry.FullName.Replace('\\', '/').StartsWith(VibsEntryDirPrefix, StringComparison.OrdinalIgnoreCase)
			&& entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))];

		foreach (ZipArchiveEntry entry in vibXmlEntries)
		{
			XmlDocument? document = TryLoadZipEntryXml(entry, vendorCode, warnings);
			if (document?.DocumentElement is null)
			{
				continue;
			}

			string? relativePath = FindValue(document.DocumentElement, "relative-path") ?? FindValue(document.DocumentElement, "relativePath");
			if (string.IsNullOrWhiteSpace(relativePath))
			{
				continue;
			}

			string? checksum = FindChecksumSha256(document.DocumentElement);
			byRelativePath[relativePath] = new EsxPatchStoreVibReference(relativePath, checksum);
		}

		if (byRelativePath.Count > 0)
		{
			return [.. byRelativePath.Values];
		}

		// Fallback: no vibs/*.xml -- scan the one top-level metadata entry (mirrors the
		// sibling reference, which reads only this form).
		ZipArchiveEntry? topLevelMetadataEntry = archive.Entries.FirstOrDefault(entry =>
			!entry.FullName.Contains('/') && !entry.FullName.Contains('\\')
			&& entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(entry.Name, VendorIndexFileName, StringComparison.OrdinalIgnoreCase));

		if (topLevelMetadataEntry is null)
		{
			return [];
		}

		XmlDocument? topDocument = TryLoadZipEntryXml(topLevelMetadataEntry, vendorCode, warnings);
		if (topDocument?.DocumentElement is null)
		{
			return [];
		}

		foreach (XmlElement relativePathElement in EnumerateDescendants(topDocument.DocumentElement, "relativePath"))
		{
			string? relativePath = relativePathElement.InnerText?.Trim();
			if (!string.IsNullOrEmpty(relativePath))
			{
				byRelativePath[relativePath] = new EsxPatchStoreVibReference(relativePath, ChecksumSha256: null);
			}
		}

		return [.. byRelativePath.Values];
	}

	private static string? FindChecksumSha256(XmlElement root)
	{
		foreach (XmlElement checksumElement in EnumerateDescendants(root, "checksum"))
		{
			string type = checksumElement.GetAttribute("checksum-type");
			if (string.IsNullOrEmpty(type) || string.Equals(type, "sha-256", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "sha256", StringComparison.OrdinalIgnoreCase))
			{
				string? value = checksumElement.InnerText?.Trim();
				if (!string.IsNullOrEmpty(value))
				{
					return value;
				}
			}
		}

		return null;
	}

	private static XmlDocument? TryLoadZipEntryXml(ZipArchiveEntry entry, string vendorCode, List<string> warnings)
	{
		if (entry.Length > MaxZipEntryBytes)
		{
			warnings.Add($"Vendor '{vendorCode}': zip entry '{entry.FullName}' is {entry.Length} bytes, over the {MaxZipEntryBytes}-byte bound -- skipped.");
			return null;
		}

		string content;
		try
		{
			using Stream entryStream = entry.Open();
			using StreamReader reader = new(entryStream);
			content = reader.ReadToEnd();
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException)
		{
			warnings.Add($"Vendor '{vendorCode}': could not read zip entry '{entry.FullName}': {ex.Message}");
			return null;
		}

		return TryParseXmlText(content, $"zip entry '{entry.FullName}'", warnings);
	}

	private static XmlDocument? TryLoadXml(string path, List<string> warnings, string description)
	{
		string content;
		try
		{
			content = File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"Could not read {description} at '{path}': {ex.Message}");
			return null;
		}

		return TryParseXmlText(content, $"{description} at '{path}'", warnings);
	}

	private static XmlDocument? TryParseXmlText(string content, string description, List<string> warnings)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			warnings.Add($"{description} is empty.");
			return null;
		}

		if (content.Length > MaxXmlBytes)
		{
			warnings.Add($"{description} exceeds the {MaxXmlBytes}-byte parse bound ({content.Length} bytes).");
			return null;
		}

		XmlReaderSettings settings = new()
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersInDocument = MaxXmlBytes,
			IgnoreComments = true,
			IgnoreProcessingInstructions = true,
			IgnoreWhitespace = true,
			CloseInput = true,
		};

		XmlDocument document = new() { XmlResolver = null };
		try
		{
			using StringReader textReader = new(content);
			using XmlReader reader = XmlReader.Create(textReader, settings);
			document.Load(reader);
		}
		catch (Exception ex) when (ex is XmlException or InvalidOperationException or NotSupportedException)
		{
			warnings.Add($"{description} is not valid/safe XML: {ex.Message}");
			return null;
		}

		return document;
	}

	/// <summary>
	/// Lists the immediate subdirectories of <paramref name="path"/>, or an empty
	/// list with a warning if the directory cannot be enumerated (permissions,
	/// transient I/O). This must never be silent: an unreadable
	/// <c>hostupdate/</c> root is otherwise indistinguishable from a legitimately
	/// empty one, and the caller's other warnings (e.g. "consolidated index not
	/// found") would misattribute the failure to a missing file rather than a
	/// denied directory listing.
	/// </summary>
	private static string[] SafeEnumerateDirectories(string path, List<string> warnings)
	{
		try
		{
			return Directory.GetDirectories(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"Could not list vendor directories under '{path}': {ex.GetType().Name}: {ex.Message}");
			return [];
		}
	}

	/// <summary>Looks for a case-insensitive child element named <paramref name="localName"/> first (the real UMDS index shape), then falls back to a same-named attribute.</summary>
	private static string? FindValue(XmlElement parent, string localName)
	{
		foreach (XmlNode child in parent.ChildNodes)
		{
			if (child is XmlElement element && string.Equals(element.LocalName, localName, StringComparison.OrdinalIgnoreCase))
			{
				string? text = element.InnerText?.Trim();
				if (!string.IsNullOrEmpty(text))
				{
					return text;
				}
			}
		}

		string attributeValue = parent.GetAttribute(localName);
		return string.IsNullOrEmpty(attributeValue) ? null : attributeValue.Trim();
	}

	private static IEnumerable<XmlElement> EnumerateDescendants(XmlElement root, string localName)
	{
		XmlNodeList? nodes = root.SelectNodes($".//*[local-name()='{localName}']");
		if (nodes is null)
		{
			yield break;
		}

		foreach (XmlNode node in nodes)
		{
			if (node is XmlElement element)
			{
				yield return element;
			}
		}
	}
}
