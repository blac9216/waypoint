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

/// <summary>
/// The two `vcf-download-tool` command families a BOM component is fetched through
/// (research #1027 Q4): binaries addressed by <c>--component</c>
/// (<c>BundleSoftwareType</c>), or artifacts addressed by <c>--category</c>
/// (<c>ArtifactsCategoryType</c> -- Kubernetes-side content such as VKR/Supervisor
/// services lives here). A preset resolver that only spans one family silently
/// under-fetches the other -- this is the exact defect #1437's amendment calls out.
/// </summary>
public enum PresetToolFamily
{
	Binaries,
	Artifacts,
}

/// <summary>
/// The shipped, generation-independent classification of one BOM component name to
/// its <see cref="PresetToolFamily"/> and the filter value the download tool expects
/// for it (e.g. <c>--component VCENTER</c> or <c>--category VKS</c>). Loaded from
/// <c>PresetData/componentCatalog.json</c> by <see cref="PresetDataCatalog"/> -- never
/// hardcoded in resolver logic, so a new component name only ever needs a data-file
/// addition (#1437 acceptance: "no SKU list hardcoded outside the per-generation data
/// files").
/// </summary>
public sealed record CatalogComponent(string Name, PresetToolFamily ToolFamily, string FamilyValue);
