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

namespace Waypoint.Core.Downloads.Photon;

/// <summary>
/// The Photon RPM repo axis (research #1029 finding 1), matching migration 0130's
/// <c>photon_repo_index_variant_check</c> verbatim -- this is the closed set.
/// <see cref="Waypoint.Tests.Core.Downloads.Photon.PhotonRepoVariantsConstraintDriftTests"/>
/// asserts this list stays byte-identical to the CHECK constraint the migration
/// produces. <see cref="Composite"/> is the bare <c>photon_&lt;version&gt;_&lt;arch&gt;</c>
/// repo (no variant word in its directory name); every other value's directory name is
/// <c>photon_&lt;variant&gt;_&lt;version&gt;_&lt;arch&gt;</c>.
/// </summary>
public static class PhotonRepoVariants
{
	public const string Release = "release";
	public const string Updates = "updates";
	public const string Extras = "extras";
	public const string Debuginfo = "debuginfo";
	public const string Srpms = "srpms";
	public const string Snapshots = "snapshots";
	public const string Composite = "composite";

	public static readonly IReadOnlyList<string> All =
	[
		Release, Updates, Extras, Debuginfo, Srpms, Snapshots, Composite,
	];
}

/// <summary>
/// The two arches every Photon repo exists in (research #1029 finding 1: "every repo
/// exists in both x86_64 and aarch64 variants"), matching migration 0130's
/// <c>photon_repo_index_arch_check</c> verbatim. Discovery indexes both unconditionally
/// (this issue's AC 2) -- arch opt-in is a later subscription-time concern
/// (<c>photon_subscription_config.arches</c>), never a discovery-time filter.
/// </summary>
public static class PhotonArches
{
	// CA1707 forbids an underscore in the identifier itself -- the underscore lives
	// only in the string VALUE, which matches the real x86_64 directory-name token.
	public const string X8664 = "x86_64";
	public const string Aarch64 = "aarch64";

	public static readonly IReadOnlyList<string> All = [X8664, Aarch64];
}
