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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Which of the two byte-compatible on-disk layouts a store's <c>hostupdate/</c> tree
/// was found under (research #1028: the wire format is unchanged across the
/// transition, only the root differs). See <see cref="IEsxPatchStoreMetadataParser"/>.
/// </summary>
public enum EsxPatchStoreLayout
{
	/// <summary>UMDS 8.x/9.0: <c>&lt;storeRoot&gt;/hostupdate/...</c> -- the store root is a sidecar UMDS volume.</summary>
	Legacy,

	/// <summary>VCFDT 9.1: <c>&lt;storeRoot&gt;/PROD/COMP/ESX_HOST/patch-store/hostupdate/...</c> -- the store root is the depot root the download tool already owns.</summary>
	Depot91,
}

/// <summary>
/// One VIB reference resolved out of a metadata bundle's <c>vibs/*.xml</c> entries (or,
/// when a bundle carries no such entries, out of its top-level metadata XML's
/// <c>relativePath</c> elements -- the sibling reference's fallback, see
/// <c>vcf-download-manager.umds.ps1</c>'s <c>Test-MetadataZip</c>). <see cref="RelativePath"/>
/// is relative to the vendor directory the owning bundle lives in, matching the depot
/// metadata's own convention.
/// </summary>
public sealed record EsxPatchStoreVibReference(string RelativePath, string? ChecksumSha256);

/// <summary>
/// One metadata bundle (a metadata zip resolved off a vendor's consolidated metadata
/// index), identified by <see cref="ContentKey"/> -- a SHA-256 of the zip's own bytes --
/// rather than by <see cref="ZipRelativePath"/>. Research #1028 found the 9.1 store's
/// micro-depot zips are named non-deterministically (<c>metadata-&lt;n&gt;.zip</c>); two
/// bundles with identical bytes under different names are the same bundle, and
/// <see cref="ZipRelativePath"/> exists for diagnostics/display only -- callers must
/// never use it as an identity key (issue #1446 AC: "same content identity ...
/// regardless of its filename").
/// </summary>
public sealed record EsxPatchStoreMetadataBundle(
	string VendorCode,
	string ContentKey,
	string ZipRelativePath,
	string? ProductId,
	string? Version,
	string? ChannelName,
	IReadOnlyList<EsxPatchStoreVibReference> Vibs);

/// <summary>
/// Closed set of ways one vendor's parse can leave that vendor with zero or partial
/// bundles despite the overall parse still returning <c>Succeeded=true</c> (issue
/// #1447 round-2 review findings F4/F5). This is the machine-readable counterpart to
/// the matching entry this parser also always adds to <see cref="EsxPatchStoreMetadata.Warnings"/>
/// for humans -- <see cref="EsxPatchStoreVendorHealth"/> is what a consumer (the
/// reconciler's missing-detection gate) keys on instead, so a reworded warning string
/// can never silently un-gate it. Each member below is tied to one specific parser
/// call site; see <see cref="EsxPatchStoreVendorHealth"/> and
/// <c>EsxPatchStoreMetadataParser</c>'s own remarks for the exact mapping. Only
/// shapes that leave a vendor's bundle list incomplete are represented here --
/// genuine absence (no consolidated metadata index file at all, or a metadata entry
/// naming a zip that is not on disk) is not a health failure and carries no entry.
/// </summary>
public enum EsxPatchStoreVendorHealthKind
{
	/// <summary>The vendor's consolidated metadata index file exists but could not be opened/read (I/O or permission failure).</summary>
	UnreadableIndex,

	/// <summary>The vendor's consolidated metadata index file is present but empty or all-whitespace -- e.g. a concurrent writer caught mid-truncate.</summary>
	EmptyIndex,

	/// <summary>
	/// The vendor's consolidated metadata index file is present and non-empty but is
	/// not well-formed/safe XML, or exceeds the parser's document-size bound -- e.g.
	/// a half-written concurrent sync (the exact UMDS-write-race round 1's finding 1
	/// named).
	/// </summary>
	MalformedIndex,

	/// <summary>A metadata zip the vendor's index names could not be opened/read (the zip's bytes could not even be hashed for its content key).</summary>
	UnreadableZip,
}

/// <summary>
/// One vendor's degraded-parse finding: <see cref="VendorCode"/> plus the
/// <see cref="EsxPatchStoreVendorHealthKind"/> that left it with zero or partial
/// bundles this run. A vendor may appear more than once (e.g. two separate
/// unreadable zips under the same vendor); a consumer that only needs "is this
/// vendor degraded at all" collapses on <see cref="VendorCode"/>.
/// </summary>
public sealed record EsxPatchStoreVendorHealth(string VendorCode, EsxPatchStoreVendorHealthKind Kind);

/// <summary>
/// The parsed content of one ESX patch store's <c>hostupdate/</c> tree: every vendor
/// code the consolidated index (or, failing that, the directory listing) named, and
/// every metadata bundle resolved under them, content-keyed per
/// <see cref="EsxPatchStoreMetadataBundle"/>. <see cref="Warnings"/> carries
/// non-fatal parse anomalies (a missing consolidated index, an unreadable zip, a
/// metadata entry whose zip does not exist) for humans -- issue #1447's reconciler is
/// the layer that turns these into surfaced discrepancies; this parser only reports
/// what it found and what it could not read, and never throws for malformed store
/// content. <see cref="RootReadable"/> and <see cref="VendorHealth"/> are the
/// machine-readable counterpart of that same information (round-2 review findings
/// F4/F5): <see cref="RootReadable"/> is <see langword="false"/> only when the
/// <c>hostupdate/</c> root itself could not be enumerated (every vendor directory
/// walk was skipped, so <see cref="VendorCodes"/>/<see cref="Bundles"/> reflect
/// nothing about the store's real content this run); <see cref="VendorHealth"/> names
/// every vendor whose own parse left it with zero or partial bundles despite the
/// overall parse succeeding. A consumer that needs to tell "genuinely empty/removed"
/// apart from "could not be read this run" must key on these fields, never on the
/// prose in <see cref="Warnings"/>.
/// </summary>
public sealed record EsxPatchStoreMetadata(
	string StoreRoot,
	EsxPatchStoreLayout Layout,
	string HostupdateRoot,
	IReadOnlyList<string> VendorCodes,
	IReadOnlyList<EsxPatchStoreMetadataBundle> Bundles,
	IReadOnlyList<string> Warnings,
	bool RootReadable,
	IReadOnlyList<EsxPatchStoreVendorHealth> VendorHealth);

/// <summary>Outcome of <see cref="IEsxPatchStoreMetadataParser.Parse"/>.</summary>
public sealed record EsxPatchStoreParseResult(bool Succeeded, EsxPatchStoreMetadata? Metadata, string? FailureReason)
{
	public static EsxPatchStoreParseResult Ok(EsxPatchStoreMetadata metadata)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		return new EsxPatchStoreParseResult(true, metadata, null);
	}

	public static EsxPatchStoreParseResult Failed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new EsxPatchStoreParseResult(false, null, reason);
	}
}
