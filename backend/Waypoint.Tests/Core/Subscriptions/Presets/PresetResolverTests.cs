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
using System.Text.Json.Serialization;
using Waypoint.Core.Subscriptions.Presets;
using Xunit;

namespace Waypoint.Tests.Core.Subscriptions.Presets;

/// <summary>
/// Fixture-driven tests for issue #1437's resolver against the sanitized (invented
/// values, never a real depot pull)
/// <c>Assets/Subscriptions/vcfManifest.sample.json</c> fixture.
/// </summary>
public sealed class PresetResolverTests
{
	private const string FixtureReleaseVersion = "9.9.0.0-fixture";
	private const string PriorGenerationReleaseVersion = "9.0.2.0-fixture";

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	private static readonly VcfManifestDocument Manifest = LoadFixtureManifest();

	private static VcfManifestDocument LoadFixtureManifest()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Subscriptions", "vcfManifest.sample.json");
		using FileStream stream = File.OpenRead(path);
		return JsonSerializer.Deserialize<VcfManifestDocument>(stream, JsonOptions)
			?? throw new InvalidOperationException("Fixture vcfManifest.sample.json produced no manifest.");
	}

	private static PresetResolver CreateResolver() => new();

	[Fact]
	public void Resolve_SpansBothToolFamilies_VkrIsNotUnderFetched()
	{
		PresetResolutionResult result = CreateResolver().Resolve(Manifest, FixtureReleaseVersion, "VCF");

		Assert.True(result.IsSuccess);
		ResolvedComponent vkr = Assert.Single(result.Resolution!.Components, c => c.Name == "VKR");
		Assert.Equal(PresetToolFamily.Artifacts, vkr.ToolFamily);
		Assert.Equal("VKS", vkr.FamilyValue);
		Assert.True(vkr.HasArtifacts);

		// Both tool families must be represented -- a resolver spanning only one would
		// silently under-fetch (#1437's amendment, the exact defect this proves absent).
		Assert.Contains(result.Resolution.Components, c => c.ToolFamily == PresetToolFamily.Binaries);
		Assert.Contains(result.Resolution.Components, c => c.ToolFamily == PresetToolFamily.Artifacts);
	}

	[Fact]
	public void Resolve_ZeroArtifactBomEntry_IsNonErrorAndDistinctFromSkuNotFound()
	{
		PresetResolutionResult resolved = CreateResolver().Resolve(Manifest, FixtureReleaseVersion, "VCF");
		Assert.True(resolved.IsSuccess);
		ResolvedComponent vmTools = Assert.Single(resolved.Resolution!.Components, c => c.Name == "VMTOOLS");
		Assert.False(vmTools.HasArtifacts);
		Assert.Empty(vmTools.Artifacts);

		PresetResolutionResult notFound = CreateResolver().Resolve(Manifest, FixtureReleaseVersion, "NOT_A_REAL_SKU");
		Assert.False(notFound.IsSuccess);
		Assert.Equal(PresetResolutionError.SkuNotFound, notFound.Error);
		Assert.Null(notFound.Resolution);
	}

	[Fact]
	public void Resolve_Vvf_IsProperSubsetOfVcf_IncludingSddcManagerFleetLcmAndDepotService()
	{
		PresetResolver resolver = CreateResolver();
		PresetResolutionResult vcf = resolver.Resolve(Manifest, FixtureReleaseVersion, "VCF");
		PresetResolutionResult vvf = resolver.Resolve(Manifest, FixtureReleaseVersion, "VVF");

		Assert.True(vcf.IsSuccess);
		Assert.True(vvf.IsSuccess);

		HashSet<string> vcfNames = vcf.Resolution!.Components.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
		HashSet<string> vvfNames = vvf.Resolution!.Components.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

		Assert.True(vvfNames.IsProperSubsetOf(vcfNames));
		Assert.Contains("SDDC_MANAGER_VCF", vvfNames);
		Assert.Contains("VCF_FLEET_LCM", vvfNames);
		Assert.Contains("DEPOT_SERVICE", vvfNames);
	}

	private static readonly string[] ExpectedVsphereEditionComponents =
		["VCENTER", "ESX_HOST", "VMTOOLS", "VCF_LICENSE_SERVER"];

	[Theory]
	[InlineData("VSPHERE_STANDARD")]
	[InlineData("VSPHERE_ESSENTIAL_PLUS")]
	[InlineData("VSPHERE_ENT_PLUS")]
	public void Resolve_VsphereEditions_ResolveToTheSameComponentSet(string skuName)
	{
		PresetResolutionResult result = CreateResolver().Resolve(Manifest, FixtureReleaseVersion, skuName);

		Assert.True(result.IsSuccess);
		Assert.Equal(
			ExpectedVsphereEditionComponents.OrderBy(n => n, StringComparer.Ordinal),
			result.Resolution!.Components.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
	}

	[Theory]
	[InlineData("VLR")]
	[InlineData("VLR_EDGE")]
	[InlineData("DLVM")]
	[InlineData("DSM")]
	public void Resolve_StandaloneAddOns_ResolveToASingleSelfContainedComponent(string skuName)
	{
		PresetResolutionResult result = CreateResolver().Resolve(Manifest, FixtureReleaseVersion, skuName);

		Assert.True(result.IsSuccess);
		ResolvedComponent component = Assert.Single(result.Resolution!.Components);
		Assert.Equal(skuName, component.Name);
		Assert.True(component.HasArtifacts);
	}

	[Fact]
	public void ResolveFamily_ShippedGenerationData_VlrEdgeOnlyExistsAtGeneration91()
	{
		PresetResolver resolver = CreateResolver();

		PresetResolutionResult atNewGeneration = resolver.ResolveFamily(Manifest, "9.1", FixtureReleaseVersion, PresetFamily.VlrEdge);
		Assert.True(atNewGeneration.IsSuccess);
		Assert.Equal("VLR_EDGE", Assert.Single(atNewGeneration.Resolution!.Components).Name);

		PresetResolutionResult atPriorGeneration = resolver.ResolveFamily(Manifest, "9.0.2", PriorGenerationReleaseVersion, PresetFamily.VlrEdge);
		Assert.False(atPriorGeneration.IsSuccess);
		Assert.Equal(PresetResolutionError.FamilyNotAvailableAtGeneration, atPriorGeneration.Error);

		// The same prior generation still resolves its other shipped families -- absence
		// of one family is per-generation data, not a resolver-wide failure.
		PresetResolutionResult vlrAtPriorGeneration = resolver.ResolveFamily(Manifest, "9.0.2", PriorGenerationReleaseVersion, PresetFamily.Vlr);
		Assert.True(vlrAtPriorGeneration.IsSuccess);
	}

	[Fact]
	public void ResolveFamily_UnknownGeneration_ReportsGenerationNotFound()
	{
		PresetResolutionResult result = CreateResolver().ResolveFamily(Manifest, "not-a-real-generation", FixtureReleaseVersion, PresetFamily.Vcf);

		Assert.False(result.IsSuccess);
		Assert.Equal(PresetResolutionError.GenerationNotFound, result.Error);
	}

	[Fact]
	public void Resolve_UnknownRelease_ReportsReleaseNotFound()
	{
		PresetResolutionResult result = CreateResolver().Resolve(Manifest, "0.0.0.0-does-not-exist", "VCF");

		Assert.False(result.IsSuccess);
		Assert.Equal(PresetResolutionError.ReleaseNotFound, result.Error);
	}

	[Fact]
	public void Resolve_ComponentWithNoShippedClassification_ThrowsRatherThanSilentlyDropping()
	{
		VcfManifestDocument manifestWithUnclassifiedComponent = new([
			new VcfManifestRelease(
				"VCF",
				"9.9.9.9-unclassified",
				[new VcfManifestBomEntry("NOT_IN_CATALOG", "1.0", null, null, null)],
				[new VcfManifestSku("VCF", null, [new VcfManifestSkuBomEntry("NOT_IN_CATALOG", true, "SELF")])]),
		]);

		PresetResolver resolver = CreateResolver();
		Assert.Throws<InvalidOperationException>(() => resolver.Resolve(manifestWithUnclassifiedComponent, "9.9.9.9-unclassified", "VCF"));
	}

	[Fact]
	public void PresetDataCatalog_ShippedComponentCatalog_ClassifiesEveryFixtureComponent()
	{
		IReadOnlyDictionary<string, CatalogComponent> catalog = PresetDataCatalog.LoadComponentCatalog();
		IReadOnlyList<string> fixtureComponentNames = Manifest.Releases
			.SelectMany(r => r.Bom)
			.Select(b => b.Name)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		foreach (string name in fixtureComponentNames)
		{
			Assert.True(catalog.ContainsKey(name), $"Shipped componentCatalog.json is missing a classification for '{name}'.");
		}
	}
}
