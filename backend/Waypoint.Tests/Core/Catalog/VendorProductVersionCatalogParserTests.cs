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

using System.Text.Json;
using Waypoint.Core.Catalog;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Catalog;

/// <summary>
/// Issue #687: <see cref="VendorProductVersionCatalogParser"/> against Broadcom's real
/// <c>productVersionCatalog.json</c> shape (the same document
/// <c>BroadcomManagedToolCatalogVerifier</c> authenticates for the VCFDT tool
/// distribution). Per PR #1742 review round-1 finding 1, the two layout-asserting
/// cases that used to hand-type their own catalog JSON now run against the shared
/// <c>depot-mini</c> fixture (issue #1696) instead -- the flatten-across-components/
/// bundles shape and the dedup-by-filename rule are both structural facts the shared
/// catalog document already encodes; the edge-case behaviour tests below (malformed
/// JSON, missing keys, a binary with no fileName) stay hand-typed since they assert
/// parser tolerance, not depot layout.
/// </summary>
public sealed class VendorProductVersionCatalogParserTests
{
	[Fact]
	public void Parse_FlattensBinariesAcrossComponentsAndBundles()
	{
		using DepotMiniFixture fixture = new();

		IReadOnlyList<DepotArtifactUpsert> result = VendorProductVersionCatalogParser.Parse(fixture.CatalogJson);

		// Flattens across components: VCENTER and NSX both contribute. RelativePath is
		// the depot-relative identity (issue #1784), not the bare catalog fileName.
		DepotArtifactUpsert vcenterBinary = Assert.Single(result, r => r.RelativePath == "PROD/COMP/VCENTER/vcsa-patch.iso");
		Assert.Equal("indexed", vcenterBinary.Status);
		Assert.Contains("\"product\":\"VCENTER\"", vcenterBinary.MetadataJson);
		Assert.Contains("\"version\":\"9.1.0.5210.25573614\"", vcenterBinary.MetadataJson);
		long materializedSize = new FileInfo(Path.Combine(fixture.RootPath, "PROD", "COMP", "VCENTER", "vcsa-patch.iso")).Length;
		Assert.Contains($"\"size_bytes\":{materializedSize}", vcenterBinary.MetadataJson);
		Assert.Equal(64, vcenterBinary.Sha256!.Length);

		Assert.Single(result, r => r.RelativePath == "PROD/COMP/NSX/nsx-missing.ova");

		// Flattens across bundles of the SAME entry: 9.1.0.6543 has two bundles
		// (b2, b2b), each contributing its own binary.
		Assert.Single(result, r => r.RelativePath == "PROD/COMP/VCENTER/vcsa-fixture-9.1.0.6543.iso");
		Assert.Single(result, r => r.RelativePath == "PROD/COMP/VCENTER/vcsa-fixture-9.1.0.6543-patch.iso");
	}

	/// <summary>
	/// Issue #1783: the parser carries each bundle's own <c>id</c> onto
	/// <see cref="DepotArtifactUpsert.BundleId"/> -- the identifier the real
	/// vcf-download-tool's <c>binaries download --id</c> actually selects on (#1027
	/// finding), never the same value as the binary's fileName
	/// (<see cref="DepotArtifactUpsert.RelativePath"/>). Uses depot-mini's real
	/// catalog document (bundle <c>b1</c> carries <c>vcsa-patch.iso</c>) rather than a
	/// hand-typed fixture, per PR #1742's own shared-fixture convention this file
	/// already follows.
	/// </summary>
	[Fact]
	public void Parse_CarriesBundleIdOntoUpsert_DistinctFromFileName()
	{
		using DepotMiniFixture fixture = new();

		IReadOnlyList<DepotArtifactUpsert> result = VendorProductVersionCatalogParser.Parse(fixture.CatalogJson);

		DepotArtifactUpsert vcenterBinary = Assert.Single(result, r => r.RelativePath == "PROD/COMP/VCENTER/vcsa-patch.iso");
		Assert.Equal("b1", vcenterBinary.BundleId);
		Assert.NotEqual(vcenterBinary.RelativePath, vcenterBinary.BundleId);

		// A second bundle (b2b) of the SAME catalog entry as b2 carries its OWN id --
		// bundle id is per-bundle, not per-entry/per-component.
		DepotArtifactUpsert secondBundleBinary = Assert.Single(result, r => r.RelativePath == "PROD/COMP/VCENTER/vcsa-fixture-9.1.0.6543-patch.iso");
		Assert.Equal("b2b", secondBundleBinary.BundleId);
	}

	[Fact]
	public void Parse_BundleWithNoIdField_StillParsesWithNullBundleId()
	{
		const string json = """
			{
			  "patches": {
			    "VCENTER": [
			      {
			        "productVersion": "8.0.3",
			        "artifacts": { "bundles": [
			          { "binaries": [ { "fileName": "no-bundle-id.iso", "checksum": "aa", "size": 100 } ] }
			        ] }
			      }
			    ]
			  }
			}
			""";

		DepotArtifactUpsert upsert = Assert.Single(VendorProductVersionCatalogParser.Parse(json));
		Assert.Equal("PROD/COMP/VCENTER/no-bundle-id.iso", upsert.RelativePath);
		Assert.Null(upsert.BundleId);
	}

	[Fact]
	public void Parse_SameFileNameAcrossBundles_DeduplicatesByFileName()
	{
		using DepotMiniFixture fixture = new();

		IReadOnlyList<DepotArtifactUpsert> result = VendorProductVersionCatalogParser.Parse(fixture.CatalogJson);

		// NSX's "4.2.0" entry carries "nsx-missing.ova" in two bundles (b3, b3b) with
		// different checksums -- the parser must keep exactly one entry, the LAST
		// bundle in document order (b3b's checksum), never two rows for one filename.
		DepotArtifactUpsert nsxBinary = Assert.Single(result, r => r.RelativePath == "PROD/COMP/NSX/nsx-missing.ova");
		Assert.Equal("0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b0b3b", nsxBinary.Sha256);
	}

	[Fact]
	public void Parse_MissingPatchesKey_ReturnsEmpty()
	{
		Assert.Empty(VendorProductVersionCatalogParser.Parse("""{"other":"stuff"}"""));
	}

	[Fact]
	public void Parse_EmptyComponentArray_ReturnsEmpty()
	{
		Assert.Empty(VendorProductVersionCatalogParser.Parse("""{"patches":{"VCENTER":[]}}"""));
	}

	[Fact]
	public void Parse_BinaryMissingFileName_IsSkippedNotThrown()
	{
		const string json = """
			{
			  "patches": {
			    "VCENTER": [
			      {
			        "productVersion": "8.0.3",
			        "artifacts": { "bundles": [
			          { "id": "b1", "binaries": [ { "checksum": "aa", "size": 100 } ] }
			        ] }
			      }
			    ]
			  }
			}
			""";

		Assert.Empty(VendorProductVersionCatalogParser.Parse(json));
	}

	[Fact]
	public void Parse_MalformedJson_ThrowsJsonException()
	{
		Assert.ThrowsAny<JsonException>(() => VendorProductVersionCatalogParser.Parse("{not-valid"));
	}
}
