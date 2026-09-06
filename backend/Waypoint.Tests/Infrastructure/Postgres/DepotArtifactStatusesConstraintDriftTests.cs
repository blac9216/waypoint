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

using System.Reflection;
using System.Text.RegularExpressions;
using Waypoint.Core.Catalog;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1705's own root cause was exactly the defect class this repo has already
/// been burned by twice (<c>RepoCredentialBindingConstraintDriftTests</c>,
/// <c>OciBundleStatusesConstraintDriftTests</c>): a closed-vocabulary CHECK
/// constraint and its C#/PowerShell producers drifting apart with nothing to catch
/// it, because the only prior coverage exercised the "happy" values, never the
/// constraint's actual value list. This class parses migration 0129's
/// <c>depot_artifacts_status_check</c> SQL and the shipped
/// <c>WaypointCatalogIndex.psm1</c> module directly, so a value added to any one of
/// the three places (SQL CHECK, <see cref="DepotArtifactStatuses"/>, the psm1's
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
		string migration0129 = ReadMigrationSql("0129_depot_artifacts_status_missing.sql");

		Assert.Equal(DepotArtifactStatuses.All, ParseCheckInList(migration0129, "depot_artifacts_status_check"));
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

	private static string ReadMigrationSql(string fileName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith(fileName, StringComparison.Ordinal)));
		using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	/// <summary>
	/// Extracts the single-quoted value list of a named <c>CONSTRAINT ... CHECK (col IN
	/// ('a', 'b', ...))</c> from migration SQL, in file order (matching
	/// <see cref="DepotArtifactStatuses.All"/>'s own declaration order -- this repo's
	/// <c>RepoCredentialBindingConstraintDriftTests.ParseCheckInList</c> convention).
	/// </summary>
	private static List<string> ParseCheckInList(string sql, string constraintName)
	{
		Match constraint = Regex.Match(
			sql,
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\([^)]*\bIN\s*\(([^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Assert.True(constraint.Success, $"Could not locate an IN-list CHECK named '{constraintName}'.");

		MatchCollection values = Regex.Matches(constraint.Groups[1].Value, "'([^']*)'");
		Assert.NotEmpty(values);
		return [.. values.Select(m => m.Groups[1].Value)];
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
