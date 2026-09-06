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

namespace Waypoint.Core.Versions;

/// <summary>
/// Tracking granularity for a subscription/preset (epic #16 decision 5: "tracking at
/// subminor/minor/major granularity; no hardcoded major versions; tracking a version
/// pulls the whole release"). Ordered coarsest-first only for readability; callers pick
/// whichever member matches the operator's chosen granularity.
/// </summary>
public enum VersionLineGranularity
{
	/// <summary>First numeric segment only, e.g. <c>8</c> from <c>8.0.3.00900</c>.</summary>
	Major = 1,

	/// <summary>First two numeric segments, e.g. <c>8.0</c> from <c>8.0.3.00900</c>.</summary>
	Minor = 2,

	/// <summary>First three numeric segments, e.g. <c>8.0.3</c> from <c>8.0.3.00900</c>.</summary>
	Subminor = 3,
}

/// <summary>
/// Extracts the tracking "line" a subscription pins to from a parsed
/// <see cref="ProductVersion"/> -- e.g. subscribing at <see
/// cref="VersionLineGranularity.Minor"/> to <c>9.1</c> should match every <c>9.1.x.y</c>
/// release without the caller hardcoding a major version (decision 5). Pure and
/// side-effect-free; wiring a subscription's evaluation loop against this is #1421's/
/// #1437's slice, not this one's -- this type only exposes the extraction primitive.
/// </summary>
public static class VersionLineExtractor
{
	/// <summary>
	/// Returns the dotted line for <paramref name="granularity"/>, or <c>null</c> when
	/// <paramref name="version"/> is unparsed or has fewer numeric segments than the
	/// requested granularity needs -- this never fabricates a trailing <c>.0</c> that
	/// was not actually present in the source string, since that would claim precision
	/// the vendor version did not carry.
	/// </summary>
	public static string? TryGetLine(ProductVersion version, VersionLineGranularity granularity)
	{
		ArgumentNullException.ThrowIfNull(version);

		int needed = (int)granularity;
		if (!version.IsParsed || version.NumericSegments.Count < needed)
		{
			return null;
		}

		return string.Join('.', version.NumericSegments.Take(needed));
	}
}
