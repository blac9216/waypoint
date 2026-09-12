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

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waypoint.Core.Subscriptions.Presets;

/// <summary>
/// Loads the shipped preset data files embedded in this assembly (mirrors the
/// migration-loading pattern in <c>Waypoint.Infrastructure.Data.NpgsqlSchemaMigrator</c>):
/// <c>PresetData/componentCatalog.json</c> (generation-independent tool-family
/// classification) and one <c>PresetData/Generations/*.json</c> file per generation
/// (family -&gt; SKU-name membership). Both are data, never a hardcoded list in C#
/// source, per #1437's acceptance criteria.
/// </summary>
public static class PresetDataCatalog
{
	private static readonly Assembly DataAssembly = typeof(PresetDataCatalog).Assembly;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	public static IReadOnlyDictionary<string, CatalogComponent> LoadComponentCatalog()
	{
		using Stream stream = OpenResourceEndingWith("componentCatalog.json");
		List<CatalogComponent>? components = JsonSerializer.Deserialize<List<CatalogComponent>>(stream, JsonOptions);
		if (components is null or [])
		{
			throw new InvalidOperationException("PresetData/componentCatalog.json produced no component classifications.");
		}

		return components.ToDictionary(c => c.Name, StringComparer.Ordinal);
	}

	public static IReadOnlyDictionary<string, PresetGenerationData> LoadGenerationFamilies()
	{
		Dictionary<string, PresetGenerationData> result = new(StringComparer.Ordinal);

		foreach (string resourceName in DataAssembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Generations.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
		{
			using Stream stream = DataAssembly.GetManifestResourceStream(resourceName)
				?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");
			GenerationFamiliesDto dto = JsonSerializer.Deserialize<GenerationFamiliesDto>(stream, JsonOptions)
				?? throw new InvalidOperationException($"Embedded resource '{resourceName}' produced no generation data.");

			Dictionary<PresetFamily, string> families = dto.Families.ToDictionary(
				pair => Enum.Parse<PresetFamily>(pair.Key, ignoreCase: true),
				pair => pair.Value);
			result[dto.Generation] = new PresetGenerationData(dto.Generation, families);
		}

		return result;
	}

	private static Stream OpenResourceEndingWith(string suffix)
	{
		string resourceName = DataAssembly.GetManifestResourceNames().First(name => name.EndsWith("." + suffix, StringComparison.Ordinal));
		return DataAssembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");
	}

	private sealed record GenerationFamiliesDto(string Generation, Dictionary<string, string> Families);
}
