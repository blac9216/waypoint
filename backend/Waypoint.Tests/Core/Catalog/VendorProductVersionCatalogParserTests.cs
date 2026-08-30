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
using Xunit;

namespace Waypoint.Tests.Core.Catalog;

/// <summary>
/// Issue #687: <see cref="VendorProductVersionCatalogParser"/> against Broadcom's real
/// <c>productVersionCatalog.json</c> shape (the same document
/// <c>BroadcomManagedToolCatalogVerifier</c> authenticates for the VCFDT tool
/// distribution).
/// </summary>
public sealed class VendorProductVersionCatalogParserTests
{
	[Fact]
	public void Parse_FlattensBinariesAcrossComponentsAndBundles()
	{
		const string json = """
			{
			  "patches": {
			    "VCENTER": [
			      {
			        "productVersion": "8.0.3.00900-25413364",
			        "artifacts": { "bundles": [
			          { "id": "b1", "binaries": [
			            { "fileName": "a.iso", "checksum": "aa", "size": 100 },
			            { "fileName": "b.zip", "checksum": "bb", "size": 200 }
			          ] }
			        ] }
			      }
			    ],
			    "NSX": [
			      {
			        "productVersion": "4.2.0",
			        "artifacts": { "bundles": [
			          { "id": "b2", "binaries": [ { "fileName": "c.ova", "checksum": "cc", "size": 300 } ] }
			        ] }
			      }
			    ]
			  }
			}
			""";

		IReadOnlyList<DepotArtifactUpsert> result = VendorProductVersionCatalogParser.Parse(json);

		Assert.Equal(3, result.Count);
		DepotArtifactUpsert a = Assert.Single(result, r => r.RelativePath == "a.iso");
		Assert.Equal("aa", a.Sha256);
		Assert.Equal("indexed", a.Status);
		Assert.Contains("\"product\":\"VCENTER\"", a.MetadataJson);
		Assert.Contains("\"version\":\"8.0.3.00900-25413364\"", a.MetadataJson);
		Assert.Contains("\"size_bytes\":100", a.MetadataJson);
	}

	[Fact]
	public void Parse_SameFileNameAcrossBundles_DeduplicatesByFileName()
	{
		const string json = """
			{
			  "patches": {
			    "VCENTER": [
			      {
			        "productVersion": "8.0.3",
			        "artifacts": { "bundles": [
			          { "id": "install", "binaries": [ { "fileName": "shared.iso", "checksum": "aa", "size": 100 } ] },
			          { "id": "patch", "binaries": [ { "fileName": "shared.iso", "checksum": "aa", "size": 100 } ] }
			        ] }
			      }
			    ]
			  }
			}
			""";

		IReadOnlyList<DepotArtifactUpsert> result = VendorProductVersionCatalogParser.Parse(json);

		Assert.Single(result);
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
