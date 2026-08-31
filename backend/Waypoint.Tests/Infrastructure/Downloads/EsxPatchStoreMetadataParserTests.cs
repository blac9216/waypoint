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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1446: <see cref="EsxPatchStoreMetadataParser"/> against invented,
/// shape-faithful fixtures of the <c>hostupdate/</c> tree research #1028 documented
/// (byte-compatible from UMDS 8.0.3 through VCFDT 9.1) -- no real vendor bytes, VIBs,
/// or store content anywhere in this file. Fixture roots are laid out exactly as the
/// real store lays them out (a top-level <c>hostupdate/</c> for the legacy layout, a
/// depot tree with <c>PROD/COMP/ESX_HOST/patch-store/hostupdate</c> nested inside for
/// 9.1) rather than flattened, per the #1629 round-1 review finding that a flattened
/// fixture hides dead code in path-handling logic.
/// </summary>
public sealed class EsxPatchStoreMetadataParserTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-esx-patch-store-").FullName;
	private readonly EsxPatchStoreMetadataParser _parser = new();

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	// ----- fixture construction -----------------------------------------------

	/// <summary>Writes <c>hostupdate/__hostupdate20-consolidated-index__.xml</c> under <paramref name="hostupdateDir"/> naming <paramref name="vendorCodes"/>.</summary>
	private static void WriteConsolidatedIndex(string hostupdateDir, params string[] vendorCodes)
	{
		Directory.CreateDirectory(hostupdateDir);
		string vendorListXml = string.Join(string.Empty, vendorCodes.Select(code => $"<vendor>{code}</vendor>"));
		File.WriteAllText(
			Path.Combine(hostupdateDir, "__hostupdate20-consolidated-index__.xml"),
			$"<hostupdate><vendorList>{vendorListXml}</vendorList></hostupdate>");
	}

	/// <summary>Writes one vendor directory's consolidated metadata index naming a single metadata entry, and returns the vendor directory path.</summary>
	private static string WriteVendorMetadataIndex(
		string hostupdateDir, string vendorCode, string location, string? productId = "ESXi900", string? version = "9.1.0", string? channelName = "vmw-ESXi-9.1")
	{
		string vendorDir = Path.Combine(hostupdateDir, vendorCode);
		Directory.CreateDirectory(vendorDir);
		File.WriteAllText(
			Path.Combine(vendorDir, "__hostupdate20-consolidated-metadata-index__.xml"),
			$"""
			<metadataList>
				<metadata>
					<productId>{productId}</productId>
					<version>{version}</version>
					<url>{location}</url>
					<channelName>{channelName}</channelName>
				</metadata>
			</metadataList>
			""");
		return vendorDir;
	}

	/// <summary>
	/// Builds a metadata zip with the research-documented shape: <c>vendor-index.xml</c>,
	/// a top-level bulletin XML, and per-VIB <c>vibs/*.xml</c> entries carrying
	/// <c>relative-path</c>/<c>checksum</c>.
	/// </summary>
	private static void WriteMetadataZip(string path, IReadOnlyList<(string RelativePath, string ChecksumSha256)> vibs)
	{
		using FileStream fileStream = File.Create(path);
		using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);

		using (StreamWriter writer = new(archive.CreateEntry("vendor-index.xml").Open()))
		{
			writer.Write("<vendorIndex/>");
		}

		using (StreamWriter writer = new(archive.CreateEntry("vmware.xml").Open()))
		{
			writer.Write("<bulletinList/>");
		}

		int i = 0;
		foreach ((string relativePath, string checksum) in vibs)
		{
			using StreamWriter writer = new(archive.CreateEntry($"vibs/vib-{i++}.xml").Open());
			writer.Write(
				$"""<vib><relative-path>{relativePath}</relative-path><packed-size>1024</packed-size><checksum checksum-type="sha-256">{checksum}</checksum></vib>""");
		}
	}

	// ----- AC1: content identity is independent of filename --------------------

	[Fact]
	public void Parse_SameZipBytesUnderDifferentFilenames_ProduceTheSameContentKey()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");

		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-a7f3.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a7f3.zip"), [("vib20/esx-update/pkg-a.vib", "aa".PadRight(64, '0'))]);

		// A second vendor directory holding byte-identical zip content under a
		// completely different (also non-deterministic 9.1-style) filename.
		string otherVendorDir = WriteVendorMetadataIndex(hostupdateDir, "HPE", "metadata-9c21.zip");
		WriteMetadataZip(Path.Combine(otherVendorDir, "metadata-9c21.zip"), [("vib20/esx-update/pkg-a.vib", "aa".PadRight(64, '0'))]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Equal(2, result.Metadata!.Bundles.Count);
		string[] contentKeys = [.. result.Metadata.Bundles.Select(b => b.ContentKey)];
		Assert.Equal(contentKeys[0], contentKeys[1]);
		// The filename is preserved for display only -- never used as identity.
		Assert.Contains(result.Metadata.Bundles, b => b.ZipRelativePath == "metadata-a7f3.zip");
		Assert.Contains(result.Metadata.Bundles, b => b.ZipRelativePath == "metadata-9c21.zip");
	}

	[Fact]
	public void Parse_DifferentZipBytes_ProduceDifferentContentKeys()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"), [("vib20/esx-update/pkg-a.vib", "aa".PadRight(64, '0'))]);

		EsxPatchStoreParseResult first = _parser.Parse(_root);

		File.Delete(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"));
		WriteMetadataZip(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"), [("vib20/esx-update/pkg-b.vib", "bb".PadRight(64, '0'))]);

		EsxPatchStoreParseResult second = _parser.Parse(_root);

		Assert.NotEqual(first.Metadata!.Bundles[0].ContentKey, second.Metadata!.Bundles[0].ContentKey);
	}

	[Fact]
	public void Parse_VibReferences_CarryRelativePathAndChecksum()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");
		WriteMetadataZip(
			Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"),
			[
				("vib20/esx-update/esx-update.vib", "11".PadRight(64, '0')),
				("vib20/esx-base/esx-base.vib", "22".PadRight(64, '0')),
			]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		EsxPatchStoreMetadataBundle bundle = Assert.Single(result.Metadata!.Bundles);
		Assert.Equal(2, bundle.Vibs.Count);
		Assert.Contains(bundle.Vibs, v => v.RelativePath == "vib20/esx-update/esx-update.vib" && v.ChecksumSha256 == "11".PadRight(64, '0'));
		Assert.Equal("ESXi900", bundle.ProductId);
		Assert.Equal("9.1.0", bundle.Version);
		Assert.Equal("vmw-ESXi-9.1", bundle.ChannelName);
	}

	// ----- AC2: both store-root layouts -----------------------------------------

	[Fact]
	public void Parse_LegacyLayout_ResolvesHostupdateAtStoreRoot()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"), [("vib20/esx-update/pkg.vib", "cc".PadRight(64, '0'))]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Equal(EsxPatchStoreLayout.Legacy, result.Metadata!.Layout);
		Assert.Equal(hostupdateDir, result.Metadata.HostupdateRoot);
		Assert.Single(result.Metadata.Bundles);
	}

	[Fact]
	public void Parse_Depot91Layout_ResolvesHostupdateInsideDepotTree()
	{
		// Rooted exactly as the real 9.1 depot lays it out: PROD/COMP/ESX_HOST/patch-store/hostupdate.
		string hostupdateDir = Path.Combine(_root, "PROD", "COMP", "ESX_HOST", "patch-store", "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-3.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-3.zip"), [("vib20/esx-update/pkg.vib", "dd".PadRight(64, '0'))]);

		// 9.1-only siblings of hostupdate/ at the patch-store root, per research #1028 --
		// version.txt, the vvs compatibility bundle, and the symlink-hostupdate symlink.
		// None of these must ever be walked or surfaced by this parser.
		string patchStoreRoot = Path.Combine(_root, "PROD", "COMP", "ESX_HOST", "patch-store");
		File.WriteAllText(Path.Combine(patchStoreRoot, "version.txt"), "9.1.0.0100.12345678");
		Directory.CreateDirectory(Path.Combine(patchStoreRoot, "vvs"));
		File.WriteAllBytes(Path.Combine(patchStoreRoot, "vvs", "vvs-consolidated-bundle.zip"), [1, 2, 3]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Equal(EsxPatchStoreLayout.Depot91, result.Metadata!.Layout);
		Assert.Equal(hostupdateDir, result.Metadata.HostupdateRoot);
		Assert.Single(result.Metadata.Bundles);
		Assert.DoesNotContain(result.Metadata.VendorCodes, code => code.Contains("vvs", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Parse_ForcedLayout_OnlyProbesThatLayoutsPath()
	{
		// A legacy-shaped store; forcing Depot91 must fail rather than silently
		// falling back to the legacy path that does exist.
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");

		EsxPatchStoreParseResult result = _parser.Parse(_root, EsxPatchStoreLayout.Depot91);

		Assert.False(result.Succeeded);
		Assert.Contains("Depot91", result.FailureReason);
	}

	[Fact]
	public void Parse_NeitherLayoutPresent_FailsWithActionableReason()
	{
		Directory.CreateDirectory(Path.Combine(_root, "some-other-content"));

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.False(result.Succeeded);
		Assert.Null(result.Metadata);
		Assert.Contains("hostupdate", result.FailureReason);
	}

	[Fact]
	public void Parse_StoreRootDoesNotExist_FailsWithActionableReason()
	{
		EsxPatchStoreParseResult result = _parser.Parse(Path.Combine(_root, "does-not-exist"));

		Assert.False(result.Succeeded);
		Assert.Contains("does not exist", result.FailureReason);
	}

	// ----- AC3: vvs bundle byte variance is never treated as corruption --------

	[Fact]
	public void Parse_VvsBundleByteVariance_NeverAppearsAsABundleOrWarning()
	{
		string hostupdateDir = Path.Combine(_root, "PROD", "COMP", "ESX_HOST", "patch-store", "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-1.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-1.zip"), [("vib20/esx-update/pkg.vib", "ee".PadRight(64, '0'))]);

		string vvsPath = Path.Combine(_root, "PROD", "COMP", "ESX_HOST", "patch-store", "vvs", "vvs-consolidated-bundle.zip");
		Directory.CreateDirectory(Path.GetDirectoryName(vvsPath)!);

		// vvs is not byte-stable across syncs (research #1028) -- simulate two
		// different byte states of the same logical bundle and confirm the parsed
		// model is identical either way (the parser never reads vvs/ at all, so the
		// bytes cannot influence anything it reports).
		File.WriteAllBytes(vvsPath, [1, 2, 3]);
		EsxPatchStoreParseResult first = _parser.Parse(_root);

		File.WriteAllBytes(vvsPath, [9, 8, 7, 6, 5]);
		EsxPatchStoreParseResult second = _parser.Parse(_root);

		Assert.True(first.Succeeded);
		Assert.True(second.Succeeded);
		Assert.Equal(first.Metadata!.Bundles.Select(b => b.ContentKey), second.Metadata!.Bundles.Select(b => b.ContentKey));
		Assert.DoesNotContain(first.Metadata.Warnings, w => w.Contains("vvs", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(second.Metadata.Warnings, w => w.Contains("vvs", StringComparison.OrdinalIgnoreCase));
	}

	// ----- AC4: the staging tree is excluded from the sweep ---------------------

	[Fact]
	public void Parse_HardlinkHostupdateStagingDirectory_IsExcludedAndWarned()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"), [("vib20/esx-update/pkg.vib", "ff".PadRight(64, '0'))]);

		// A staging-tree trap directly under hostupdate/, shaped like a vendor
		// directory (its own consolidated metadata index pointing at content) --
		// the parser must not walk it or count its content as a bundle.
		string stagingDir = WriteVendorMetadataIndex(hostupdateDir, "hardlink-hostupdate", "staged-metadata.zip");
		WriteMetadataZip(Path.Combine(stagingDir, "staged-metadata.zip"), [("vib20/esx-update/staged.vib", "12".PadRight(64, '0'))]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Single(result.Metadata!.Bundles);
		Assert.DoesNotContain(result.Metadata.VendorCodes, code => string.Equals(code, "hardlink-hostupdate", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("hardlink-hostupdate", StringComparison.OrdinalIgnoreCase) && w.Contains("staging", StringComparison.OrdinalIgnoreCase));
	}

	// ----- tolerant-parse behavior ----------------------------------------------

	[Fact]
	public void Parse_MissingMetadataZip_WarnsAndContinuesWithOtherVendors()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw", "HPE");

		// vmw's index references a zip that was never actually downloaded.
		WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");

		// HPE's zip is genuinely present.
		string hpeDir = WriteVendorMetadataIndex(hostupdateDir, "HPE", "hpe-esxi-metadata.zip");
		WriteMetadataZip(Path.Combine(hpeDir, "hpe-esxi-metadata.zip"), [("vib20/hpe-driver/pkg.vib", "33".PadRight(64, '0'))]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		EsxPatchStoreMetadataBundle bundle = Assert.Single(result.Metadata!.Bundles);
		Assert.Equal("HPE", bundle.VendorCode);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("vmw-ESXi-9.1-metadata.zip") && w.Contains("not found"));

		// Genuine absence (a metadata entry naming a zip that simply is not on disk)
		// must never set vendor health -- round-2 review finding F4's "do not
		// classify the genuine-absence shapes" instruction.
		Assert.Empty(result.Metadata.VendorHealth);
	}

	[Fact]
	public void Parse_CorruptMetadataZip_WarnsAndStillReturnsTheStore()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");
		File.WriteAllText(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"), "not actually a zip file");

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		EsxPatchStoreMetadataBundle bundle = Assert.Single(result.Metadata!.Bundles);
		Assert.Empty(bundle.Vibs);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("not a valid zip archive"));
	}

	[Fact]
	public void Parse_MalformedConsolidatedIndex_WarnsAndFallsBackToDirectoryListing()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		Directory.CreateDirectory(hostupdateDir);
		File.WriteAllText(Path.Combine(hostupdateDir, "__hostupdate20-consolidated-index__.xml"), "<not<valid</xml");

		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip"), [("vib20/esx-update/pkg.vib", "44".PadRight(64, '0'))]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Single(result.Metadata!.Bundles);
		Assert.Contains("vmw", result.Metadata.VendorCodes);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("consolidated index"));
	}

	[Fact]
	public void Parse_EmptyStore_SucceedsWithNoBundlesAndAWarning()
	{
		Directory.CreateDirectory(Path.Combine(_root, "hostupdate"));

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Empty(result.Metadata!.Bundles);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("Consolidated index not found"));
	}

	/// <summary>
	/// #1638 round-1 review finding 1: an unreadable <c>hostupdate/</c> root (a
	/// permissions/mount problem, not an empty store) must not look like a
	/// successful parse of an empty store, and the emitted warning must not
	/// misattribute the enumeration failure to a missing consolidated index. Skipped
	/// on Windows -- <see cref="UnixFileMode"/> does not model Windows ACLs, and this
	/// repro depends on denying directory traversal via Unix permission bits.
	/// </summary>
	[Fact]
	public void Parse_UnreadableHostupdateRoot_WarnsAndIsDistinguishableFromAnEmptyStore()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-a7f3.zip");

		// Write-only, no execute/traverse bit: denies both listing hostupdate/'s
		// subdirectories and stat'ing files directly inside it -- the reviewer's
		// mode-0200 reproduction.
		File.SetUnixFileMode(hostupdateDir, UnixFileMode.UserWrite);
		try
		{
			EsxPatchStoreParseResult result = _parser.Parse(_root);

			Assert.True(result.Succeeded);
			Assert.Empty(result.Metadata!.Bundles);
			Assert.Empty(result.Metadata.VendorCodes);
			Assert.Contains(
				result.Metadata.Warnings,
				w => w.Contains("Could not list vendor directories") && w.Contains(hostupdateDir));

			// The empty-store case (Parse_EmptyStore_SucceedsWithNoBundlesAndAWarning)
			// produces exactly one warning; an unreadable store must produce a second,
			// distinguishing warning naming the enumeration failure -- not just the
			// "not found" warning that an empty store would also emit.
			Assert.True(
				result.Metadata.Warnings.Count >= 2,
				$"expected an enumeration-failure warning in addition to any consolidated-index warning, got: {string.Join(" | ", result.Metadata.Warnings)}");
		}
		finally
		{
			// Restore a permissive mode so the fixture's temp-directory cleanup in
			// Dispose() can actually delete this subtree.
			File.SetUnixFileMode(hostupdateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	// ----- round-2 review finding F4: structural vendor health -------------------

	/// <summary>
	/// Round-2 review finding F4, reproduced directly against the parser: a
	/// zero-length vendor consolidated metadata index (the exact concurrent-write
	/// trigger round 1's finding 1 named) must set <see cref="EsxPatchStoreVendorHealth"/>
	/// with <see cref="EsxPatchStoreVendorHealthKind.EmptyIndex"/> for that vendor --
	/// this is what the reconciler's missing-detection gate keys on instead of the
	/// warning prose below.
	/// </summary>
	[Fact]
	public void Parse_ZeroLengthVendorIndex_SetsEmptyIndexVendorHealth()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-a.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a.zip"), [("vib20/esx-update/pkg-a.vib", "aa".PadRight(64, '0'))]);

		// Truncate the already-written index to zero bytes -- a concurrent
		// UMDS/rsync write caught mid-truncate.
		File.WriteAllText(Path.Combine(vendorDir, "__hostupdate20-consolidated-metadata-index__.xml"), string.Empty);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Empty(result.Metadata!.Bundles);
		Assert.True(result.Metadata.RootReadable);
		EsxPatchStoreVendorHealth health = Assert.Single(result.Metadata.VendorHealth);
		Assert.Equal("vmw", health.VendorCode);
		Assert.Equal(EsxPatchStoreVendorHealthKind.EmptyIndex, health.Kind);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("is empty"));
	}

	/// <summary>
	/// Round-2 review finding F4, reproduced directly against the parser: a
	/// half-written/malformed vendor consolidated metadata index must set
	/// <see cref="EsxPatchStoreVendorHealthKind.MalformedIndex"/> for that vendor.
	/// </summary>
	[Fact]
	public void Parse_MalformedVendorIndex_SetsMalformedIndexVendorHealth()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-a.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a.zip"), [("vib20/esx-update/pkg-a.vib", "aa".PadRight(64, '0'))]);

		// Truncated mid-tag -- a half-written concurrent sync, not empty and not
		// valid XML.
		File.WriteAllText(
			Path.Combine(vendorDir, "__hostupdate20-consolidated-metadata-index__.xml"),
			"<metadataList><metadata><productId>ESXi900</productId");

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.Empty(result.Metadata!.Bundles);
		Assert.True(result.Metadata.RootReadable);
		EsxPatchStoreVendorHealth health = Assert.Single(result.Metadata.VendorHealth);
		Assert.Equal("vmw", health.VendorCode);
		Assert.Equal(EsxPatchStoreVendorHealthKind.MalformedIndex, health.Kind);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("not valid/safe XML"));
	}

	[Fact]
	public void Parse_HealthyStore_HasNoVendorHealthEntries()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-a.zip");
		WriteMetadataZip(Path.Combine(vendorDir, "metadata-a.zip"), [("vib20/esx-update/pkg-a.vib", "aa".PadRight(64, '0'))]);

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		Assert.True(result.Succeeded);
		Assert.True(result.Metadata!.RootReadable);
		Assert.Empty(result.Metadata.VendorHealth);
	}

	/// <summary>
	/// Round-2 review finding F5: an unreadable <c>hostupdate/</c> root must set
	/// <see cref="EsxPatchStoreMetadata.RootReadable"/> to <see langword="false"/> --
	/// the field the reconciler's whole-run skip is keyed on, alongside the existing
	/// warning-prose assertion above.
	/// </summary>
	[Fact]
	public void Parse_UnreadableHostupdateRoot_SetsRootReadableFalse()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		WriteVendorMetadataIndex(hostupdateDir, "vmw", "metadata-a7f3.zip");

		File.SetUnixFileMode(hostupdateDir, UnixFileMode.UserWrite);
		try
		{
			EsxPatchStoreParseResult result = _parser.Parse(_root);

			Assert.True(result.Succeeded);
			Assert.False(result.Metadata!.RootReadable);
			Assert.Empty(result.Metadata.VendorHealth);
		}
		finally
		{
			File.SetUnixFileMode(hostupdateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	// ----- fallback VIB parsing (no vibs/*.xml entries) --------------------------

	[Fact]
	public void Parse_MetadataZipWithNoVibsDirectory_FallsBackToTopLevelRelativePathElements()
	{
		string hostupdateDir = Path.Combine(_root, "hostupdate");
		WriteConsolidatedIndex(hostupdateDir, "vmw");
		string vendorDir = WriteVendorMetadataIndex(hostupdateDir, "vmw", "vmw-ESXi-9.1-metadata.zip");

		string zipPath = Path.Combine(vendorDir, "vmw-ESXi-9.1-metadata.zip");
		using (FileStream fileStream = File.Create(zipPath))
		using (ZipArchive archive = new(fileStream, ZipArchiveMode.Create))
		{
			using (StreamWriter vendorIndexWriter = new(archive.CreateEntry("vendor-index.xml").Open()))
			{
				vendorIndexWriter.Write("<vendorIndex/>");
			}

			using StreamWriter metadataWriter = new(archive.CreateEntry("vmware.xml").Open());
			metadataWriter.Write(
				"""
				<bulletin>
					<vib><vibFile><relativePath>vib20/esx-update/pkg.vib</relativePath></vibFile></vib>
				</bulletin>
				""");
		}

		EsxPatchStoreParseResult result = _parser.Parse(_root);

		EsxPatchStoreMetadataBundle bundle = Assert.Single(result.Metadata!.Bundles);
		EsxPatchStoreVibReference vibReference = Assert.Single(bundle.Vibs);
		Assert.Equal("vib20/esx-update/pkg.vib", vibReference.RelativePath);
		Assert.Null(vibReference.ChecksumSha256);
	}
}
