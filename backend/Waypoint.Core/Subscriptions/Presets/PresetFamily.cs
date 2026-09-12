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
/// The shipped preset families #1437 requires: the two stack presets, the three
/// (identical-component-set) vSphere editions, and the four self-contained standalone
/// add-ons. This enum is the stable key a caller asks for; which manifest SKU name it
/// maps to at a given generation -- and whether it exists at that generation at all,
/// e.g. <see cref="VlrEdge"/> first appearing at generation 9.1 -- is per-generation
/// data (<see cref="PresetGenerationData"/>), never a literal here.
/// </summary>
public enum PresetFamily
{
	Vcf,
	Vvf,
	VsphereStandard,
	VsphereEssentialPlus,
	VsphereEnterprisePlus,
	Vlr,
	VlrEdge,
	Dlvm,
	Dsm,
}

/// <summary>
/// One generation's <see cref="PresetFamily"/> -&gt; manifest SKU name membership,
/// loaded from a shipped <c>PresetData/Generations/*.json</c> file. A family absent
/// from <see cref="Families"/> does not exist at this generation (e.g. <c>VlrEdge</c>
/// is absent from the 9.0.2 file) -- that is a data fact, not a resolver code path.
/// </summary>
public sealed record PresetGenerationData(string Generation, IReadOnlyDictionary<PresetFamily, string> Families);
