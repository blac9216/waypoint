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

using Waypoint.Core.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Drift guard for <see cref="OciBundleStatuses.All"/> against migration 0118's
/// <c>oci_bundles_status_check</c>, following this repo's convention for every other
/// closed-vocabulary/CHECK pairing (<c>RunTypesConstraintDriftTests</c>,
/// <c>InventoryItemTypesConstraintDriftTests</c>,
/// <c>ComponentResultStatusConstraintDriftTests</c>,
/// <c>SchemaMigrationTests.Migration0050/0051_...</c>): parse the authoritative value
/// set out of the embedded migration SQL, scoped to the <c>oci_bundles</c> table via
/// <see cref="ConstraintDriftScan"/> (issue #1814), and assert it equals the C#
/// constant, in order. #1413 and #1441 are both explicitly designed to drive rows
/// through this vocabulary, so a future widening or rename of the constraint that is
/// not mirrored in <see cref="OciBundleStatuses.All"/> must fail here rather than pass
/// silently.
/// </summary>
public sealed class OciBundleStatusesConstraintDriftTests
{
	[Fact]
	public void OciBundleStatusesAll_EqualsOciBundlesStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"oci_bundles", "oci_bundles_status_check", "status");

		Assert.Equal(OciBundleStatuses.All, constraintValues);
	}
}
