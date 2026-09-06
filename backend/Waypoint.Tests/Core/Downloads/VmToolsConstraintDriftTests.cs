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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Drift guard for <see cref="VmToolsPlatforms.All"/>/<see cref="VmToolsFileTypes.All"/>
/// against migration 0109's NAMED <c>vmtools_artifact_index_platform_check</c> and
/// <c>vmtools_artifact_index_file_type_check</c> constraints, following
/// <c>DepotArtifactStatusesConstraintDriftTests</c>'s convention (review round 2 relay
/// on this PR, PR #1765, R1 -- superseding this file's own round-1 table-scoped-paren
/// approach, which closed a decoy-table hole but opened an ALTER-invisibility one: a
/// later migration re-declaring the constraint via this repo's DROP CONSTRAINT/ADD
/// CONSTRAINT idiom sits outside a CREATE TABLE's parens and would have gone unseen).
/// Matching on the constraint NAME rather than table position is both table-scoped (the
/// name is unique per table, and this repo's naming convention prefixes it with the
/// table name) and ALTER-visible (an <c>ALTER TABLE ... ADD CONSTRAINT &lt;name&gt;
/// CHECK (...)</c> re-declaration matches the same literal text pattern as the inline
/// column-constraint form). Scans every embedded migration in order and keeps the LAST
/// declaration -- the one the fully-migrated database actually enforces -- exactly
/// <c>ParseLatestCheckAcrossMigrations</c>'s convention.
/// </summary>
public sealed class VmToolsConstraintDriftTests
{
	[Fact]
	public void VmToolsPlatformsAll_IsInLockstepWithPlatformCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckAcrossMigrations(
			ReadEmbeddedMigrations(), "vmtools_artifact_index_platform_check", "platform");
		Assert.Equal(VmToolsPlatforms.All, constraintValues);
	}

	[Fact]
	public void VmToolsFileTypesAll_IsInLockstepWithFileTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckAcrossMigrations(
			ReadEmbeddedMigrations(), "vmtools_artifact_index_file_type_check", "file_type");
		Assert.Equal(VmToolsFileTypes.All, constraintValues);
	}

	/// <summary>
	/// Mutation check (direction a): a CHECK constraint with a DIFFERENT name declared
	/// on a different table must never be picked up as though it were
	/// <c>vmtools_artifact_index_platform_check</c>. Name-based matching excludes it
	/// simply because the literal name differs -- unlike the round-1 fix's paren-block
	/// approach, no table-position reasoning is needed at all.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_IgnoresDifferentlyNamedConstraintOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE other_thing (
			    platform TEXT NOT NULL CONSTRAINT other_thing_platform_check CHECK (platform IN ('bogus'))
			);

			CREATE TABLE IF NOT EXISTS vmtools_artifact_index (
			    platform TEXT NOT NULL CONSTRAINT vmtools_artifact_index_platform_check CHECK (platform IN ('windows', 'linux'))
			);
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(
			migrations, "vmtools_artifact_index_platform_check", "platform");

		Assert.Equal(["windows", "linux"], values);
	}

	/// <summary>
	/// Mutation check (direction b): the reviewer's own mutation from the round-1
	/// relay finding -- a later migration widening
	/// <c>vmtools_artifact_index_platform_check</c> via this repo's
	/// <c>DROP CONSTRAINT</c>/<c>ADD CONSTRAINT</c> idiom (migration 0129's own shape
	/// for <c>depot_artifacts_status_check</c>) -- MUST be picked up as the latest
	/// declaration, proving this guard is ALTER-visible where the round-1 CREATE-TABLE-
	/// paren-scoped approach was not.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_PicksUpALaterAlterTableDropAddConstraint()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vmtools_artifact_index (
			    platform TEXT NOT NULL CONSTRAINT vmtools_artifact_index_platform_check CHECK (platform IN ('windows', 'linux', 'arm', 'unknown'))
			);
			""",
			"""
			ALTER TABLE vmtools_artifact_index DROP CONSTRAINT IF EXISTS vmtools_artifact_index_platform_check;
			ALTER TABLE vmtools_artifact_index ADD CONSTRAINT vmtools_artifact_index_platform_check CHECK (platform IN ('windows'));
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(
			migrations, "vmtools_artifact_index_platform_check", "platform");

		Assert.Equal(["windows"], values);
	}

	private static List<string> ReadEmbeddedMigrations()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		List<string> sqlTexts = [];
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			sqlTexts.Add(reader.ReadToEnd());
		}

		return sqlTexts;
	}

	/// <summary>
	/// Reads every migration text, in order, and returns the value list of the LAST
	/// <paramref name="constraintName"/> CHECK constraint declared across them -- i.e.
	/// the constraint the fully-migrated database actually enforces. Matches
	/// <c>DepotArtifactStatusesConstraintDriftTests.ParseLatestCheckAcrossMigrations</c>'s
	/// convention: matching on the constraint NAME (not table position) means a later
	/// <c>DROP CONSTRAINT</c>/<c>ADD CONSTRAINT</c> re-declaration is visible, since it
	/// is the same literal <c>CONSTRAINT &lt;name&gt; CHECK (...)</c> text shape whether
	/// it appears inline in a <c>CREATE TABLE</c> or in a later <c>ALTER TABLE</c>.
	/// </summary>
	private static List<string> ParseLatestCheckAcrossMigrations(
		IEnumerable<string> migrationSqlTexts, string constraintName, string columnName)
	{
		Regex checkPattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (string sql in migrationSqlTexts)
		{
			foreach (Match match in checkPattern.Matches(sql))
			{
				latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}
}
