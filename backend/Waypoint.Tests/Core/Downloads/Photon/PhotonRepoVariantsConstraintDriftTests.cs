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

using Waypoint.Core.Downloads.Photon;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Downloads.Photon;

/// <summary>
/// Drift guard for <see cref="PhotonRepoVariants.All"/>/<see cref="PhotonArches.All"/>
/// against migration 0130's <c>photon_repo_index_variant_check</c>/
/// <c>photon_repo_index_arch_check</c>, scoped to the <c>photon_repo_index</c> table
/// via <see cref="ConstraintDriftScan"/> (issue #1814 -- every
/// <c>*ConstraintDriftTests</c> guard shares that one table-scoped, ALTER-visible
/// helper rather than each carrying its own unscoped regex): parse the authoritative
/// value set out of the embedded migration SQL and assert it equals the C# constant
/// set (as sets -- PhotonRepoDiscoveryJobHandler iterates
/// <see cref="PhotonRepoVariants.All"/> in a fixed order that is a design choice, not
/// something the CHECK constraint's declaration order constrains).
/// </summary>
public sealed class PhotonRepoVariantsConstraintDriftTests
{
	[Fact]
	public void PhotonRepoVariantsAll_EqualsVariantCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"photon_repo_index", "photon_repo_index_variant_check", "variant");

		Assert.Equal(
			new HashSet<string>(PhotonRepoVariants.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	[Fact]
	public void PhotonArchesAll_EqualsArchCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"photon_repo_index", "photon_repo_index_arch_check", "arch");

		Assert.Equal(
			new HashSet<string>(PhotonArches.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}
}
