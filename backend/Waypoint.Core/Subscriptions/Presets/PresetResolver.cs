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

namespace Waypoint.Core.Subscriptions.Presets;

/// <inheritdoc cref="IPresetResolver"/>
public sealed class PresetResolver : IPresetResolver
{
	private readonly IReadOnlyDictionary<string, CatalogComponent> _catalog;
	private readonly IReadOnlyDictionary<string, PresetGenerationData> _generations;

	/// <summary>Defaults to the shipped data (<see cref="PresetDataCatalog"/>); tests may substitute
	/// their own catalog/generation maps without touching embedded resources.</summary>
	public PresetResolver(
		IReadOnlyDictionary<string, CatalogComponent>? catalog = null,
		IReadOnlyDictionary<string, PresetGenerationData>? generations = null)
	{
		_catalog = catalog ?? PresetDataCatalog.LoadComponentCatalog();
		_generations = generations ?? PresetDataCatalog.LoadGenerationFamilies();
	}

	public PresetResolutionResult Resolve(VcfManifestDocument manifest, string releaseVersion, string skuName)
	{
		ArgumentNullException.ThrowIfNull(manifest);

		VcfManifestRelease? release = manifest.Releases.FirstOrDefault(r => string.Equals(r.Version, releaseVersion, StringComparison.Ordinal));
		if (release is null)
		{
			return PresetResolutionResult.Failure(PresetResolutionError.ReleaseNotFound);
		}

		VcfManifestSku? sku = release.Sku.FirstOrDefault(s => string.Equals(s.Name, skuName, StringComparison.Ordinal));
		if (sku is null)
		{
			return PresetResolutionResult.Failure(PresetResolutionError.SkuNotFound);
		}

		Dictionary<string, VcfManifestBomEntry> bomByName = release.Bom.ToDictionary(b => b.Name, StringComparer.Ordinal);
		List<ResolvedComponent> components = new(sku.Bom.Count);

		foreach (VcfManifestSkuBomEntry entry in sku.Bom)
		{
			if (!bomByName.ContainsKey(entry.Name))
			{
				throw new InvalidOperationException(
					$"SKU '{skuName}' on release '{releaseVersion}' references component '{entry.Name}', which is absent from the release's top-level BOM.");
			}

			if (!_catalog.TryGetValue(entry.Name, out CatalogComponent? classification))
			{
				throw new InvalidOperationException(
					$"Component '{entry.Name}' has no tool-family classification in shipped preset data (PresetData/componentCatalog.json).");
			}

			IReadOnlyList<string> artifactNames = release.ArtifactsFor(entry.Name);
			components.Add(new ResolvedComponent(
				entry.Name,
				classification.ToolFamily,
				classification.FamilyValue,
				entry.AutomatedInstall,
				entry.LifecycleManagedBy,
				artifactNames.Select(name => new ResolvedArtifact(name)).ToList()));
		}

		return PresetResolutionResult.Success(new PresetResolution(releaseVersion, skuName, components));
	}

	public PresetResolutionResult ResolveFamily(VcfManifestDocument manifest, string generation, string releaseVersion, PresetFamily family)
	{
		if (!_generations.TryGetValue(generation, out PresetGenerationData? generationData))
		{
			return PresetResolutionResult.Failure(PresetResolutionError.GenerationNotFound);
		}

		if (!generationData.Families.TryGetValue(family, out string? skuName))
		{
			return PresetResolutionResult.Failure(PresetResolutionError.FamilyNotAvailableAtGeneration);
		}

		return Resolve(manifest, releaseVersion, skuName);
	}
}
