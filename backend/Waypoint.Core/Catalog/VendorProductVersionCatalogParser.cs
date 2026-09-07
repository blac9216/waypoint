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
/// <see cref="DepotArtifactUpsert"/> per unique <see cref="DepotRelativePaths.Resolve"/>
/// identity (issue #687; rekeyed from a bare <c>fileName</c> to the depot-relative
/// path by issue #1784 -- see <see cref="DepotArtifactUpsert.RelativePath"/>'s own doc
/// comment) -- the same file can legitimately appear in more than one bundle of the
/// same entry (an ISO shared across INSTALL and PATCH bundles), so last-write-wins per
/// identity is correct here, matching the sibling reference's own flattening
/// rationale.
///
/// Also carries each bundle's own <c>id</c> field onto
/// <see cref="DepotArtifactUpsert.BundleId"/> (migration 0132, issue #1783) -- the
/// identifier the real vcf-download-tool's <c>binaries download --id</c> actually
/// selects on (#1027 finding: <c>BINARY_NOT_FOUND_IN_LOCAL_PVC</c>, "bundles are
/// addressed by catalog id"), never the same value as <c>fileName</c>. A binary whose
/// bundle carries no <c>id</c> still parses -- the bundle id is a sibling fact, not a
/// precondition for this method's own contract; the enqueue path
/// (<c>DownloadsController.QueueBinariesDownload</c>) is what refuses to queue a
/// <c>binaries-download</c> job for a null bundle id.
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

		Dictionary<string, DepotArtifactUpsert> byRelativePath = new(StringComparer.Ordinal);
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

					string? bundleId = bundle.TryGetProperty("id", out JsonElement bundleIdElement) && bundleIdElement.ValueKind == JsonValueKind.String
						? bundleIdElement.GetString()
						: null;

					foreach (JsonElement binary in binaries.EnumerateArray())
					{
						DepotArtifactUpsert? upsert = TryParseBinary(binary, component.Name, version, bundleId);
						if (upsert is not null)
						{
							byRelativePath[upsert.RelativePath] = upsert;
						}
					}
				}
			}
		}

		return [.. byRelativePath.Values];
	}

	private static DepotArtifactUpsert? TryParseBinary(JsonElement binary, string component, string? version, string? bundleId)
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

		// Issue #1784: RelativePath is now the SAME depot-relative identity
		// (PROD/COMP/<component>/<fileName>) the offline presence sweep resolves
		// (WaypointCatalogIndex.psm1's Get-CatalogEntryDepotRelativePath) -- prior to
		// this fix it was the vendor catalog's bare fileName, so a stack that both
		// pulled and swept wrote two rows per artifact under two different identities,
		// with contradictory statuses (live validation). DepotRelativePaths.Resolve is
		// the single shared rule; CatalogPullJobHandler reconciles any pre-#1784 row
		// still keyed under the legacy bare-fileName identity on the next pull.
		//
		// bundleId (migration 0130, issue #1783) travels alongside the depot-relative
		// RelativePath -- the two are independent identifiers (see the class doc
		// comment above) and this rebase's conflict resolution keeps both.
		return new DepotArtifactUpsert(DepotRelativePaths.Resolve(component, fileName), checksum, DepotArtifactStatuses.Indexed, metadataJson, size, bundleId);
	}
}
