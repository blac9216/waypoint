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

using Waypoint.Core.Secrets;
using Waypoint.Core.Subscriptions;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1421, migration 0104: this repo's real class-killing drift guard (the
/// <see cref="RepoCredentialBindingConstraintDriftTests"/> convention) for every
/// vocabulary migration 0104 hardcodes as a CHECK constraint -- parses the
/// authoritative value set out of the embedded migration SQL, scoped to the owning
/// table via <see cref="ConstraintDriftScan"/> (issue #1814 -- every
/// <c>*ConstraintDriftTests</c> guard shares that one table-scoped, ALTER-visible
/// helper rather than each carrying its own unscoped regex), and asserts it equals
/// the C# side, in order, so adding/removing a value on either side without the other
/// fails here rather than at runtime. Review round 1 finding F2 added the fourth test
/// below (<c>presets_stack_check</c> against <see cref="PresetStacks.All"/>) after
/// finding the migration introduced that vocabulary with neither a mirroring C#
/// constant nor a drift test, unlike every other vocabulary here.
/// </summary>
public sealed class SubscriptionsConstraintDriftTests
{
	[Fact]
	public void SubscriptionLineGranularityValuesAll_EqualsSubscriptionsLineGranularityCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"subscriptions", "subscriptions_line_granularity_check", "line_granularity");

		Assert.Equal(SubscriptionLineGranularityValues.All, constraintValues);
	}

	[Fact]
	public void SubscriptionLineGranularityValuesAll_EqualsPresetsLineGranularityCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"presets", "presets_line_granularity_check", "line_granularity");

		Assert.Equal(SubscriptionLineGranularityValues.All, constraintValues);
	}

	[Fact]
	public void RepoStoresAll_EqualsSubscriptionsLaneCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"subscriptions", "subscriptions_lane_check", "lane");

		// subscriptions.lane reuses the RepoStores.All acquisition-lane vocabulary
		// (migration 0104's own header comment) rather than a new one.
		Assert.Equal(RepoStores.All, constraintValues);
	}

	[Fact]
	public void PresetStacksAll_EqualsPresetsStackCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"presets", "presets_stack_check", "stack");

		Assert.Equal(PresetStacks.All, constraintValues);
	}
}
