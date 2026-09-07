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

using Waypoint.Core.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Jobs;

/// <summary>
/// Drift guard for the class of bug PR #712's review caught: <see cref="RunTypes.All"/>
/// had gone stale against <c>runs_run_type_check</c> (it listed 0001's 11 values while
/// 0042 had grown the constraint to 14). The <c>GET /runs/history</c> <c>run_type</c>
/// filter validates against <see cref="RunTypes.All"/>, so any divergence silently
/// 400s a legitimate history run type. This parses the authoritative
/// <c>runs_run_type_check</c> value set out of the embedded migration SQL, scoped to
/// the <c>runs</c> table via <see cref="ConstraintDriftScan"/> (issue #1814 -- every
/// <c>*ConstraintDriftTests</c> guard shares that one table-scoped, ALTER-visible
/// helper rather than each carrying its own unscoped regex), and asserts it is exactly
/// <see cref="RunTypes.All"/> -- no live database required, so it runs in the fast unit
/// pass and fails the instant a migration changes the constraint without the constant
/// being updated in lockstep (and vice versa).
/// </summary>
public sealed class RunTypesConstraintDriftTests
{
	[Fact]
	public void RunTypesAll_EqualsRunsRunTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"runs", "runs_run_type_check", "run_type");

		// Ordinal, order-sensitive equality: the constant is documented as matching the
		// constraint "verbatim", so drift in either the set or the order (which is how
		// the API and the frontend NON_COMPLIANCE_RUN_TYPES mirror read it) is a failure.
		Assert.Equal(RunTypes.All, constraintValues);
	}
}
