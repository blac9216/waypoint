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

using System.Text.Json.Serialization;

namespace Waypoint.Core.Subscriptions.Presets;

/// <summary>
/// Minimal deserialization shape of a depot's <c>vcfManifest.json</c> (issue #1437,
/// research #1027 Q4): <c>{ releases: [...] }</c>, each release carrying a union
/// <see cref="Bom"/> (component name -&gt; version metadata) and a per-<see cref="Sku"/>
/// membership list. Fields the resolver does not consume (sequenceNumber,
/// creationTime, minCompatibleVcfVersion, eol, ...) are intentionally omitted --
/// unknown JSON members are ignored by <see cref="System.Text.Json.JsonSerializer"/>
/// by default. <see cref="VcfManifestRelease.CatalogArtifacts"/> is a fixture/loader
/// convenience standing in for the separate <c>productVersionCatalog.json</c>
/// <c>patches[&lt;name&gt;]</c> join described in #1027 -- joining the real catalog
/// file is out of this issue's scope.
/// </summary>
public sealed record VcfManifestDocument(IReadOnlyList<VcfManifestRelease> Releases);

public sealed record VcfManifestRelease(
	string Product,
	string Version,
	IReadOnlyList<VcfManifestBomEntry> Bom,
	IReadOnlyList<VcfManifestSku> Sku,
	IReadOnlyDictionary<string, IReadOnlyList<string>>? CatalogArtifacts = null)
{
	/// <summary>Artifact file names for a component, or an empty list when the component is a valid
	/// zero-artifact BOM member (#1027 Q4: 12 of 60 names have no catalog <c>patches</c> entry) or is
	/// simply absent from this fixture's <see cref="CatalogArtifacts"/> map -- both cases are
	/// non-error and indistinguishable from each other by design.</summary>
	public IReadOnlyList<string> ArtifactsFor(string componentName) =>
		CatalogArtifacts is not null && CatalogArtifacts.TryGetValue(componentName, out IReadOnlyList<string>? artifacts)
			? artifacts
			: [];
}

/// <summary>A top-level union BOM entry: component identity plus version metadata, independent of any SKU.</summary>
public sealed record VcfManifestBomEntry(
	string Name,
	string Version,
	string? ChangeId,
	string? PublicName,
	[property: JsonPropertyName("releaseURL")] string? ReleaseUrl);

/// <summary>A SKU (e.g. <c>VCF</c>, <c>VVF</c>, <c>VLR_EDGE</c>) and the component names it pulls in.</summary>
public sealed record VcfManifestSku(string Name, string? Description, IReadOnlyList<VcfManifestSkuBomEntry> Bom);

/// <summary>A SKU's BOM membership entry -- name and lifecycle metadata only, no version (#1027 Q4).</summary>
public sealed record VcfManifestSkuBomEntry(string Name, bool AutomatedInstall, string? LifecycleManagedBy);
