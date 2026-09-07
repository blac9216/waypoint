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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Configuration for <see cref="IEsxPlatformVocabularyReader"/> (issue #1470).
/// Deliberately its own small option type rather than a new field on
/// <see cref="Waypoint.Core.Catalog.CatalogOptions"/> or <see cref="ManagedToolOptions"/>:
/// this slice reads the same already-authenticated vendor catalog document those
/// options already name (see <see cref="VocabularyDocumentPath"/>'s default), but as
/// an independently configurable path so the two concerns can evolve separately.
/// </summary>
public sealed class EsxAcquisitionOptions
{
	public const string SectionName = "EsxAcquisition";

	/// <summary>
	/// Absolute path to the vendor catalog document
	/// <see cref="IEsxPlatformVocabularyReader"/> parses the
	/// <c>lcm.esx.supported.host.platforms</c> vocabulary from. Left blank here
	/// deliberately -- issue #1602: the earlier default hand-copied
	/// <see cref="ManagedToolOptions.LocalRepositoryPath"/> +
	/// <see cref="ManagedToolOptions.ProductVersionCatalogPath"/> as a literal
	/// string, which silently drifted out of sync with a reconfigured
	/// <c>ManagedTool:LocalRepositoryPath</c>. <see cref="EsxAcquisitionOptionsPostConfigure"/>
	/// now derives the default from those two options directly (combined) whenever
	/// this is left unset, so the two stay in sync; setting this explicitly still
	/// overrides the derived value.
	/// </summary>
	public string VocabularyDocumentPath { get; set; } = string.Empty;
}
