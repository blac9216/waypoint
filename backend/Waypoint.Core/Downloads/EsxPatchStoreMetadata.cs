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
/// The parsed content of one ESX patch store's <c>hostupdate/</c> tree: every vendor
/// code the consolidated index (or, failing that, the directory listing) named, and
/// every metadata bundle resolved under them, content-keyed per
/// <see cref="EsxPatchStoreMetadataBundle"/>. <see cref="Warnings"/> carries
/// non-fatal parse anomalies (a missing consolidated index, an unreadable zip, a
/// metadata entry whose zip does not exist) -- issue #1447's reconciler is the layer
/// that turns these into surfaced discrepancies; this parser only reports what it
/// found and what it could not read, and never throws for malformed store content.
/// </summary>
public sealed record EsxPatchStoreMetadata(
	string StoreRoot,
	EsxPatchStoreLayout Layout,
	string HostupdateRoot,
	IReadOnlyList<string> VendorCodes,
	IReadOnlyList<EsxPatchStoreMetadataBundle> Bundles,
	IReadOnlyList<string> Warnings);

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
