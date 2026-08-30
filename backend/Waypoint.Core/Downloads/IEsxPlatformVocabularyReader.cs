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
/// Resolves the <c>lcm.esx.supported.host.platforms</c> vendor vocabulary -- the
/// closed set of ESX host platform keys an <see cref="EsxAcquisitionSubscription"/>
/// may select from -- at request time, so it is never a hardcoded enum in this
/// codebase (issue #1470 AC). <c>#1026</c>'s ratified research found no existing
/// queryable surface for this key on main (only Broadcom's <c>patches</c>-keyed
/// <c>productVersionCatalog.json</c>, which this reader's own implementation reads),
/// so this interface is the surface issue #1470's own Risk section anticipated this
/// slice might need to add.
/// </summary>
public interface IEsxPlatformVocabularyReader
{
	/// <summary>
	/// Returns the current platform keys, read fresh from the vocabulary source every
	/// call (never cached) -- a vocabulary change on disk is reflected on the very
	/// next call, which is what proves "sourced, not hardcoded" testable. Returns an
	/// empty list, never throws, when the source document is absent or does not carry
	/// the vocabulary key -- an unavailable vocabulary degrades acquisition to "no
	/// selectable platforms yet," not a request failure.
	/// </summary>
	Task<IReadOnlyList<string>> GetSupportedPlatformsAsync(CancellationToken cancellationToken);
}
