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

using System.Text.RegularExpressions;
using Waypoint.Core.Catalog;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1705's own root cause was exactly the defect class this repo has already
/// been burned by twice (<c>RepoCredentialBindingConstraintDriftTests</c>,
/// <c>OciBundleStatusesConstraintDriftTests</c>): a closed-vocabulary CHECK
/// constraint and its C#/PowerShell producers drifting apart with nothing to catch
/// it, because the only prior coverage exercised the "happy" values, never the
/// constraint's actual value list. <c>depot_artifacts_status_check</c> is, as of this
/// PR, declared twice (<c>0001_initial_schema.sql</c>'s original four values, widened
/// by migration 0129's DROP/ADD) -- the same "widened via the repo's DROP/ADD idiom"
/// shape <c>RepoCredentialBindingConstraintDriftTests</c>'s own
/// <c>CredentialTypesAll_...</c> fact and <c>OciBundleStatusesConstraintDriftTests</c>
/// already establish a convention for: scan every embedded migration in order and
/// assert against the LAST declaration, the one the fully-migrated database actually
/// enforces, rather than reading a single named file (review round 1, PR #1744,
/// finding 1 -- reading 0129 alone let a later migration silently re-widen the
/// constraint with nothing here noticing), now table-scoped to <c>depot_artifacts</c>
/// via the shared <see cref="ConstraintDriftScan"/> (issue #1814). The psm1 half is still read from the one
/// shipped module file directly. A value added to any one of the three places (SQL
/// CHECK across all migrations, <see cref="DepotArtifactStatuses"/>, the psm1's
/// <c>ValidateSet</c>) without the others fails here -- mutation-proof in both
/// directions for each pair.
/// </summary>
public sealed class DepotArtifactStatusesConstraintDriftTests
{
	private static readonly string CatalogIndexModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointCatalogIndex", "WaypointCatalogIndex.psm1");

	[Fact]
	public void DepotArtifactStatusesAll_EqualsDepotArtifactsStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"depot_artifacts", "depot_artifacts_status_check", "status");

		Assert.Equal(DepotArtifactStatuses.All, constraintValues);
	}

	[Fact]
	public void DepotArtifactStatusesPresenceSweepEmitted_EqualsWaypointCatalogIndexValidateSetValues()
	{
		string fullPath = Path.GetFullPath(CatalogIndexModulePath);
		Assert.True(File.Exists(fullPath), $"expected WaypointCatalogIndex.psm1 at '{fullPath}'");
		string psm1 = File.ReadAllText(fullPath);

		List<string> emitted = ParseValidateSetValues(psm1, "Status");

		Assert.Equal(DepotArtifactStatuses.PresenceSweepEmitted, emitted);
	}

	/// <summary>
	/// Every value the presence sweep can emit must already be a value the CHECK
	/// constraint accepts -- the exact gap issue #1705 found live. This is the
	/// assertion <see cref="DepotArtifactStatusesAll_EqualsDepotArtifactsStatusCheckConstraintValueSet"/>
	/// and <see cref="DepotArtifactStatusesPresenceSweepEmitted_EqualsWaypointCatalogIndexValidateSetValues"/>
	/// together already imply (both sets are asserted against the same
	/// <see cref="DepotArtifactStatuses"/> constants, and <c>PresenceSweepEmitted</c>
	/// is a subset of <c>All</c> by construction), stated as its own direct proof so a
	/// future edit to either constant list still cannot silently reopen the gap.
	/// </summary>
	[Fact]
	public void PresenceSweepEmittedStatuses_AreAllMembersOfTheFullVocabulary()
	{
		foreach (string status in DepotArtifactStatuses.PresenceSweepEmitted)
		{
			Assert.Contains(status, DepotArtifactStatuses.All);
		}
	}

	/// <summary>
	/// Extracts a PowerShell <c>[ValidateSet('a', 'b')]</c> attribute's value list
	/// immediately preceding a <c>[string]$&lt;parameterName&gt;</c> parameter
	/// declaration.
	/// </summary>
	private static List<string> ParseValidateSetValues(string psm1, string parameterName)
	{
		Match validateSet = Regex.Match(
			psm1,
			$@"ValidateSet\(([^)]*)\)\s*\]\s*\[string\]\${Regex.Escape(parameterName)}\b",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Assert.True(validateSet.Success, $"Could not locate a ValidateSet immediately before parameter '${parameterName}'.");

		MatchCollection values = Regex.Matches(validateSet.Groups[1].Value, "'([^']*)'");
		Assert.NotEmpty(values);
		return [.. values.Select(m => m.Groups[1].Value)];
	}
}
