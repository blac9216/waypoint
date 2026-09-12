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
/// A resolver failure mode. <see cref="SkuNotFound"/> (the SKU name does not exist on
/// this release) is deliberately distinct from a successfully resolved component that
/// has zero artifacts (#1437 acceptance: the two must be distinguishable) -- the
/// latter is not an error at all and appears as a normal <see cref="ResolvedComponent"/>
/// with an empty <see cref="ResolvedComponent.Artifacts"/> list.
/// </summary>
public enum PresetResolutionError
{
	None,
	ReleaseNotFound,
	SkuNotFound,
	GenerationNotFound,
	FamilyNotAvailableAtGeneration,
}

/// <summary>One artifact file resolved for a component. Deliberately thin -- checksum/size join against
/// the real product version catalog is out of this issue's scope.</summary>
public sealed record ResolvedArtifact(string FileName);

/// <summary>
/// One BOM component resolved for a (release, SKU) pair, tagged with the tool family
/// and filter value a download-tool invocation needs (#1437: spanning both families is
/// the whole point). <see cref="HasArtifacts"/> <c>false</c> is a valid, non-error
/// outcome (a zero-artifact BOM member, #1027 Q4), not "not found".
/// </summary>
public sealed record ResolvedComponent(
	string Name,
	PresetToolFamily ToolFamily,
	string FamilyValue,
	bool AutomatedInstall,
	string? LifecycleManagedBy,
	IReadOnlyList<ResolvedArtifact> Artifacts)
{
	public bool HasArtifacts => Artifacts.Count > 0;
}

public sealed record PresetResolution(string ReleaseVersion, string SkuName, IReadOnlyList<ResolvedComponent> Components);

/// <summary>Outcome of a resolve call: either a successful <see cref="Resolution"/>, or an <see cref="Error"/>
/// naming why not -- never both, never neither.</summary>
public sealed record PresetResolutionResult(PresetResolutionError Error, PresetResolution? Resolution)
{
	public bool IsSuccess => Error == PresetResolutionError.None && Resolution is not null;

	public static PresetResolutionResult Success(PresetResolution resolution) => new(PresetResolutionError.None, resolution);

	public static PresetResolutionResult Failure(PresetResolutionError error) => new(error, null);
}

/// <summary>
/// Derives the concrete, cross-tool-family artifact set for a (release, SKU) pair from
/// a depot's <c>vcfManifest.json</c> SKU BOM (issue #1437). Shipped presets are thin
/// declarations of a <see cref="PresetFamily"/> at a generation over this resolver,
/// never hand-curated artifact lists.
/// </summary>
public interface IPresetResolver
{
	/// <summary>Resolves a manifest SKU by its literal name on a specific release.</summary>
	PresetResolutionResult Resolve(VcfManifestDocument manifest, string releaseVersion, string skuName);

	/// <summary>Resolves a shipped <see cref="PresetFamily"/> at a generation, translating it to the
	/// manifest SKU name that generation's shipped data declares before resolving as <see cref="Resolve"/> would.</summary>
	PresetResolutionResult ResolveFamily(VcfManifestDocument manifest, string generation, string releaseVersion, PresetFamily family);
}
