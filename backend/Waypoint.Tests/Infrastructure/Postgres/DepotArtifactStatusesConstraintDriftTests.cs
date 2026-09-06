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
/// constraint's actual value list. <c>depot_artifacts_status_check</c> is, as of this
/// PR, declared twice (<c>0001_initial_schema.sql</c>'s original four values, widened
/// by migration 0129's DROP/ADD) -- the same "widened via the repo's DROP/ADD idiom"
/// shape <c>RepoCredentialBindingConstraintDriftTests</c>'s own
/// <c>CredentialTypesAll_...</c> fact and <c>OciBundleStatusesConstraintDriftTests</c>
/// already establish a convention for: scan every embedded migration in order and
/// assert against the LAST declaration, the one the fully-migrated database actually
/// enforces, rather than reading a single named file (review round 1, PR #1744,
/// finding 1 -- reading 0129 alone let a later migration silently re-widen the
/// constraint with nothing here noticing). The psm1 half is still read from the one
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
		List<string> constraintValues = ParseLatestCheckAcrossMigrations("depot_artifacts_status_check", "status");

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
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>) and returns the value list of the LAST
	/// <paramref name="constraintName"/> CHECK constraint declared across them -- i.e.
	/// the constraint the fully-migrated database actually enforces. This repo's
	/// <c>RepoCredentialBindingConstraintDriftTests.ParseLatestCheckAcrossMigrations</c>
	/// convention: <c>depot_artifacts_status_check</c> is declared once in
	/// <c>0001_initial_schema.sql</c> and widened again by migration 0129's DROP/ADD,
	/// so only scanning every migration and keeping the last hit is safe against a
	/// further widening this test would otherwise miss (review round 1, PR #1744,
	/// finding 1).
	/// </summary>
	private static List<string> ParseLatestCheckAcrossMigrations(string constraintName, string columnName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Regex checkPattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			string sql = reader.ReadToEnd();

			foreach (Match match in checkPattern.Matches(sql))
			{
				latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
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
