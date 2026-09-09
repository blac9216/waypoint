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

using Waypoint.Core.Scans;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Drift guard for migration 0063's three closed-vocabulary CHECK constraints, same
/// mechanism as <c>RunTypesConstraintDriftTests</c>: parse the authoritative value set
/// straight out of the embedded migration SQL, table-scoped via
/// <see cref="ConstraintDriftScan"/> (issue #1814), and assert the application-side
/// constant matches exactly, in order -- no live database required.
/// </summary>
public sealed class ComponentResultStatusConstraintDriftTests
{
	[Fact]
	public void ComponentResultStatuses_All_EqualsComponentResultsStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"component_results", "component_results_status_check", "status");
		Assert.Equal(ComponentResultStatuses.All, constraintValues);
	}

	[Fact]
	public void ComponentFindingStatuses_All_EqualsComponentResultFindingsStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"component_result_findings", "component_result_findings_status_check", "status");
		Assert.Equal(ComponentFindingStatuses.All, constraintValues);
	}

	[Fact]
	public void ComponentFindingSeverities_All_EqualsComponentResultFindingsSeverityCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"component_result_findings", "component_result_findings_severity_check", "severity");
		Assert.Equal(ComponentFindingSeverities.All, constraintValues);
	}

	[Fact]
	public void ComponentResultArtifactKinds_All_EqualsComponentResultArtifactsKindCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"component_result_artifacts", "component_result_artifacts_kind_check", "kind");
		Assert.Equal(ComponentResultArtifactKinds.All, constraintValues);
	}
}
