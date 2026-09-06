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

using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1696 deliverable 6: the shared depot-mini fixture stages BOTH documented ESX
/// patch-store root layouts (#1028) and the download tool's own staging-tree exception
/// (issue #1164, <c>hardlink-hostupdate</c>) side by side with the catalog tree. Per
/// PR #1742 review round-1 finding 1, this file now carries the two dual-layout cases
/// <c>EsxPatchStoreMetadataParserTests.cs</c> used to hand-build
/// (<c>Parse_LegacyLayout_ResolvesHostupdateAtStoreRoot</c> /
/// <c>Parse_Depot91Layout_ResolvesHostupdateInsideDepotTree</c>, retired there) --
/// those asserted <see cref="EsxPatchStoreMetadata.Layout"/>,
/// <see cref="EsxPatchStoreMetadata.HostupdateRoot"/>, and a single resolved bundle
/// against a hand-typed fixture tree; the same facts now hold against depot-mini's
/// single-sourced tree instead. <c>EsxPatchStoreMetadataParserTests.cs</c>'s remaining
/// ~30 cases (corrupt zips, malformed indexes, vendor-health edge cases, and the
/// vvs-byte-variance behavior test) stay hand-built -- see #1740.
/// </summary>
public sealed class EsxPatchStoreMetadataParserDepotMiniTests
{
	private readonly EsxPatchStoreMetadataParser _parser = new();

	[Fact]
	public void Parse_Depot91Layout_FindsVendorAndSkipsStagingTree()
	{
		// Depot91's store root is the DEPOT root itself -- the parser appends
		// PROD/COMP/ESX_HOST/patch-store/hostupdate internally (Depot91RelativeSegments).
		using DepotMiniFixture fixture = new();
		string expectedHostupdateRoot = Path.Combine(fixture.RootPath, "PROD", "COMP", "ESX_HOST", "patch-store", "hostupdate");

		EsxPatchStoreParseResult result = _parser.Parse(fixture.RootPath, EsxPatchStoreLayout.Depot91);

		Assert.True(result.Succeeded, result.FailureReason);
		Assert.Equal(EsxPatchStoreLayout.Depot91, result.Metadata!.Layout);
		Assert.Equal(expectedHostupdateRoot, result.Metadata.HostupdateRoot);
		Assert.Single(result.Metadata.Bundles);
		Assert.Contains("vmw", result.Metadata.VendorCodes);
		Assert.DoesNotContain("hardlink-hostupdate", result.Metadata.VendorCodes);
		Assert.Contains(result.Metadata.Warnings, w => w.Contains("hardlink-hostupdate", StringComparison.Ordinal));
	}

	[Fact]
	public void Parse_LegacyLayout_FindsVendorAtStoreRoot()
	{
		using DepotMiniFixture fixture = new();
		string storeRoot = Path.Combine(fixture.RootPath, "ESX_LEGACY_STORE");
		string expectedHostupdateRoot = Path.Combine(storeRoot, "hostupdate");

		EsxPatchStoreParseResult result = _parser.Parse(storeRoot, EsxPatchStoreLayout.Legacy);

		Assert.True(result.Succeeded, result.FailureReason);
		Assert.Equal(EsxPatchStoreLayout.Legacy, result.Metadata!.Layout);
		Assert.Equal(expectedHostupdateRoot, result.Metadata.HostupdateRoot);
		Assert.Single(result.Metadata.Bundles);
		Assert.Contains("vmw", result.Metadata.VendorCodes);
	}
}
