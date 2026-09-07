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
/// One row of the <c>consumer_views</c> table (migration 0131, issue #1464, epic
/// #1183 -- split from design record #1162's closing comment): a named, operator-
/// chosen platform set that will later drive filtered ESX metadata generation
/// (Child B) and serving (Child D). Deliberately dumb -- no generation state, no
/// last-regenerated timestamp -- that belongs to Child B so this model stays small.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Name">Operator-chosen, unique (case-sensitive) view name.</param>
/// <param name="Platforms">
/// Ordered platform keys from the static <see cref="ConsumerViewPlatformVocabulary"/>
/// (e.g. <c>embeddedEsx-7.0-INTL</c>), validated at write time -- see that class's
/// doc comment. An EMPTY list is explicitly allowed and means "all platforms, no
/// filtering" -- the shipped default view (<see cref="DefaultViewId"/>) has an empty
/// set for exactly this reason, and a non-default view may have one too.
/// </param>
/// <param name="IsDefault">
/// Marks the single view representing the unfiltered/default store -- a boolean
/// singleton on an ordinary row, never a special-cased absence (issue #1464 AC).
/// "Exactly one row has this set to <c>true</c> at any time" is two layered
/// guarantees, not one: migration 0131's partial unique index enforces AT MOST one
/// (a second <c>is_default: true</c> write is a 409 <c>default_already_set</c> via
/// <c>ConsumerViewsController</c>'s write-path check); the seeded
/// <see cref="DefaultViewId"/> row plus the repository/API refusing to delete it or
/// clear <c>is_default</c> while it is the sole default (409 <c>default_required</c>)
/// enforce AT LEAST one. Together the default can be MOVED (mark a different row
/// default first), never removed outright.
/// </param>
public sealed record ConsumerView(
	Guid Id,
	string Name,
	IReadOnlyList<string> Platforms,
	bool IsDefault,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt)
{
	/// <summary>
	/// The well-known id of the default/unfiltered view seeded by migration 0131 --
	/// see that migration's header for the "at most one" vs "at least one" split this
	/// row is the "at least one" half of.
	/// </summary>
	public static readonly Guid DefaultViewId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}

/// <summary>
/// The static ESX platform-key vocabulary reconciled on issue #1156 (the shipped
/// <c>esx configuration -G</c> host-version list, hands-on against a real depot --
/// see that issue's findings comment). Validated against at write time by
/// <c>ConsumerViewsController</c> only -- this list is never a database CHECK
/// (matching migration 0117's <c>esx_acquisition_subscriptions.selected_platforms</c>
/// precedent, since the set is small and application-owned).
///
/// why: this vocabulary is deliberately hand-copied here rather than sourced from a
/// shared parser. If #1039 (the version comparator, Wave 1) later ships a shared
/// platform-key comparator/parser, this validation should be revisited to reuse it
/// instead of duplicating the vocabulary -- see #1464's own "Risks / Considerations."
/// Unlike <see cref="IEsxPlatformVocabularyReader"/> (which reads the LIVE
/// <c>lcm.esx.supported.host.platforms</c> vendor vocabulary from the depot's
/// authenticated catalog at request time, per #1470 AC), this list is intentionally
/// static: #1464's own AC calls for validation "against a known-good static
/// vocabulary list only," out of scope for wiring the live reader here.
/// </summary>
public static class ConsumerViewPlatformVocabulary
{
	/// <summary>
	/// The nine platform keys #1156 confirmed a pristine VCFDT extraction enables by
	/// default (<c>esx configuration -G</c>'s "Host Versions for which patch content
	/// will be downloaded" list), in the canonical
	/// <c>&lt;productLine&gt;-&lt;version&gt;-INTL</c> shape.
	/// </summary>
	public static readonly IReadOnlyList<string> All =
	[
		"embeddedEsx-6.7-INTL",
		"embeddedEsx-7.0-INTL",
		"embeddedEsx-8.0-INTL",
		"embeddedEsx-9.0-INTL",
		"embeddedEsx-9.1-INTL",
		"esxio-8.0-INTL",
		"esxio-9.0-INTL",
		"esxio-9.1-INTL",
		"armEsx-9.1-INTL",
	];
}
