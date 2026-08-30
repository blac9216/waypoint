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

namespace Waypoint.Core.Catalog;

/// <summary>
/// Parses Broadcom's real <c>productVersionCatalog.json</c> shape (the same document
/// <c>BroadcomManagedToolCatalogVerifier</c> authenticates for the VCFDT tool
/// distribution itself, and the sibling reference's <c>Get-VcsaLatestRelease</c>
/// resolves against for VCSA): a <c>patches</c> object keyed by component (e.g.
/// <c>VCENTER</c>), each an array of entries with <c>productVersion</c> and
/// <c>artifacts.bundles[].binaries[]</c> (each with <c>fileName</c>, <c>checksum</c>,
/// <c>size</c>). Flattens every binary across every component/entry into one
/// <see cref="DepotArtifactUpsert"/> per unique <c>fileName</c> (issue #687) --
/// the same file can legitimately appear in more than one bundle of the same entry
/// (an ISO shared across INSTALL and PATCH bundles), so last-write-wins per filename
/// is correct here, matching the sibling reference's own flattening rationale.
/// </summary>
public static class VendorProductVersionCatalogParser
{
	/// <summary>
	/// Parses <paramref name="json"/> into one <see cref="DepotArtifactUpsert"/> per
	/// unique binary filename found under <c>patches.*[].artifacts.bundles[].binaries[]</c>.
	/// Throws <see cref="JsonException"/> on malformed JSON -- callers classify that as
	/// a job failure (issue #687 AC: "malformed metadata ... visible and fail
	/// closed"), never as a silently empty result.
	/// </summary>
	public static IReadOnlyList<DepotArtifactUpsert> Parse(string json)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(json);

		using JsonDocument document = JsonDocument.Parse(json);
		if (!document.RootElement.TryGetProperty("patches", out JsonElement patches) || patches.ValueKind != JsonValueKind.Object)
		{
			return [];
		}

		Dictionary<string, DepotArtifactUpsert> byFileName = new(StringComparer.Ordinal);
		foreach (JsonProperty component in patches.EnumerateObject())
		{
			if (component.Value.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			foreach (JsonElement entry in component.Value.EnumerateArray())
			{
				string? version = entry.TryGetProperty("productVersion", out JsonElement versionElement) && versionElement.ValueKind == JsonValueKind.String
					? versionElement.GetString()
					: null;

				if (!entry.TryGetProperty("artifacts", out JsonElement artifacts)
					|| !artifacts.TryGetProperty("bundles", out JsonElement bundles)
					|| bundles.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				foreach (JsonElement bundle in bundles.EnumerateArray())
				{
					if (!bundle.TryGetProperty("binaries", out JsonElement binaries) || binaries.ValueKind != JsonValueKind.Array)
					{
						continue;
					}

					foreach (JsonElement binary in binaries.EnumerateArray())
					{
						DepotArtifactUpsert? upsert = TryParseBinary(binary, component.Name, version);
						if (upsert is not null)
						{
							byFileName[upsert.RelativePath] = upsert;
						}
					}
				}
			}
		}

		return [.. byFileName.Values];
	}

	private static DepotArtifactUpsert? TryParseBinary(JsonElement binary, string component, string? version)
	{
		if (!binary.TryGetProperty("fileName", out JsonElement fileNameElement) || fileNameElement.ValueKind != JsonValueKind.String)
		{
			return null;
		}

		string? fileName = fileNameElement.GetString();
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}

		string? checksum = binary.TryGetProperty("checksum", out JsonElement checksumElement) && checksumElement.ValueKind == JsonValueKind.String
			? checksumElement.GetString()
			: null;

		long? size = binary.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long sizeValue)
			? sizeValue
			: null;

		// "product"/"version" are migration 0007's GENERATED STORED columns (derived
		// from metadata->>'product'/'version') -- unlike the local filesystem walk
		// (CatalogIndexJobHandler, which always writes null for both), a connected
		// pull DOES know both from the authenticated vendor catalog itself, so this
		// is the one indexing path that actually populates the product/version
		// filters docs/api-contract.md's /catalog/artifacts exposes.
		Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
		{
			["product"] = component,
			["size_bytes"] = size,
		};
		if (!string.IsNullOrWhiteSpace(version))
		{
			metadata["version"] = version;
		}

		string metadataJson = JsonSerializer.Serialize(metadata);

		// fileName is passed as RelativePath (migration 0100, issue #1488): the
		// vendor catalog only ever gives a flat binary filename, never a nested
		// depot-relative path, so this remains the same string value as before --
		// what changed is that it now travels through the same explicitly named
		// catalog-identity field the offline disk walk uses, instead of a bare
		// ExternalId string standing in for two different things. Reconciling a
		// nested relative path for the connected side is presence-sweep behavior
		// (#1503), out of this slice's scope.
		return new DepotArtifactUpsert(fileName, checksum, "indexed", metadataJson, size);
	}
}
