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

using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #1602 (default derivation) and issue #1829 (rooted/escape guard parity with
/// <c>BroadcomManagedToolCatalogVerifier.ResolveConfigured</c> /
/// <c>CatalogPullJobHandler.ResolveConfigured</c>).
/// </summary>
public sealed class EsxAcquisitionOptionsPostConfigureTests
{
	[Fact]
	public void PostConfigure_NoExplicitOverride_DerivesFromManagedToolOptions()
	{
		ManagedToolOptions managedTool = new()
		{
			LocalRepositoryPath = "/vcf",
			ProductVersionCatalogPath = "PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json",
		};
		EsxAcquisitionOptionsPostConfigure postConfigure = new(Options.Create(managedTool));
		EsxAcquisitionOptions options = new();

		postConfigure.PostConfigure(null, options);

		Assert.Equal(
			Path.GetFullPath(Path.Combine("/vcf", "PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json")),
			options.VocabularyDocumentPath);
	}

	[Fact]
	public void PostConfigure_ExplicitOverride_LeavesItUntouched()
	{
		ManagedToolOptions managedTool = new() { LocalRepositoryPath = "/vcf", ProductVersionCatalogPath = "catalog.json" };
		EsxAcquisitionOptionsPostConfigure postConfigure = new(Options.Create(managedTool));
		EsxAcquisitionOptions options = new() { VocabularyDocumentPath = "/explicit/override.json" };

		postConfigure.PostConfigure(null, options);

		Assert.Equal("/explicit/override.json", options.VocabularyDocumentPath);
	}

	[Fact]
	public void PostConfigure_RootedProductVersionCatalogPath_ThrowsInsteadOfSilentlyDiscardingLocalRepositoryPath()
	{
		ManagedToolOptions managedTool = new() { LocalRepositoryPath = "/vcf", ProductVersionCatalogPath = "/etc/passwd" };
		EsxAcquisitionOptionsPostConfigure postConfigure = new(Options.Create(managedTool));
		EsxAcquisitionOptions options = new();

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => postConfigure.PostConfigure(null, options));
		Assert.Contains("relative to the repository root", exception.Message);
	}

	[Fact]
	public void PostConfigure_EscapingProductVersionCatalogPath_ThrowsInsteadOfResolvingOutsideTheRoot()
	{
		ManagedToolOptions managedTool = new() { LocalRepositoryPath = "/vcf", ProductVersionCatalogPath = "../../escape.json" };
		EsxAcquisitionOptionsPostConfigure postConfigure = new(Options.Create(managedTool));
		EsxAcquisitionOptions options = new();

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => postConfigure.PostConfigure(null, options));
		Assert.Contains("escapes the repository root", exception.Message);
	}
}
