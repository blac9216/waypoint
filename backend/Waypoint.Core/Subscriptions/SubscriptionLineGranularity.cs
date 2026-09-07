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

namespace Waypoint.Core.Subscriptions;

/// <summary>
/// The tracking-line vocabulary a <see cref="Subscription"/> or <see cref="Preset"/>
/// declares (issue #1421, ADR-0028, epic #16 decision 5: "tracking at
/// subminor/minor/major granularity ... no hardcoded major versions"). Migration
/// 0104's <c>line_granularity</c> columns store the lower-kebab-case member names
/// below (<c>subminor</c>/<c>minor</c>/<c>major</c>) -- see
/// <see cref="SubscriptionLineGranularityValues"/> for the string<->enum mapping every
/// caller and CHECK-constraint drift test must go through, rather than each caller
/// inventing its own string literal.
/// <para>
/// Issue #1421 AC amendment (2026-09-07, review round 1 F-finding): ADR-0028's
/// Decision names exactly three tracking widths -- "tracking granularity is
/// subminor/minor/major ... and adopting a subscription pulls the whole release
/// (every bundle/binary), not a filtered subset". "Pulls the whole release" is
/// artifact completeness (what a lane acquires once a subscription is in-line), not
/// a fourth tracking width -- a prior revision of this type modelled it as one
/// (<c>WholeRelease</c>), which matched every parseable candidate and was removed.
/// </para>
/// </summary>
public enum SubscriptionLineGranularity
{
	/// <summary>First three numeric segments must match the anchor's line, e.g. <c>8.0.3</c>. Narrowest option; maps to <see cref="Waypoint.Core.Versions.VersionLineGranularity.Subminor"/>.</summary>
	Subminor,

	/// <summary>First two numeric segments must match the anchor's line, e.g. <c>8.0</c>. Maps to <see cref="Waypoint.Core.Versions.VersionLineGranularity.Minor"/>.</summary>
	Minor,

	/// <summary>First numeric segment must match the anchor's line, e.g. <c>8</c>. Maps to <see cref="Waypoint.Core.Versions.VersionLineGranularity.Major"/>.</summary>
	Major,
}

/// <summary>
/// The single string<->enum mapping for <see cref="SubscriptionLineGranularity"/>,
/// matching migration 0104's <c>subscriptions_line_granularity_check</c>/
/// <c>presets_line_granularity_check</c> CHECK constraints exactly (proven by
/// <see cref="Waypoint.Tests.Infrastructure.Postgres.SubscriptionsConstraintDriftTests"/>).
/// One place, so a repository or the migration's value list can never drift from the
/// enum silently.
/// </summary>
public static class SubscriptionLineGranularityValues
{
	public const string Subminor = "subminor";
	public const string Minor = "minor";
	public const string Major = "major";

	/// <summary>Declaration order matches the migration's CHECK constraint value list.</summary>
	public static readonly IReadOnlyList<string> All = [Subminor, Minor, Major];

	public static string ToDbValue(SubscriptionLineGranularity granularity) => granularity switch
	{
		SubscriptionLineGranularity.Subminor => Subminor,
		SubscriptionLineGranularity.Minor => Minor,
		SubscriptionLineGranularity.Major => Major,
		_ => throw new ArgumentOutOfRangeException(nameof(granularity), granularity, null),
	};

	public static SubscriptionLineGranularity FromDbValue(string value) => value switch
	{
		Subminor => SubscriptionLineGranularity.Subminor,
		Minor => SubscriptionLineGranularity.Minor,
		Major => SubscriptionLineGranularity.Major,
		_ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unrecognised subscription line granularity."),
	};
}
