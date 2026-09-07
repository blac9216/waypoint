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
/// Drift guard for <see cref="VksItemSources.All"/>/<see cref="VksReleaseLines.All"/>/
/// <see cref="VksNamingEras.All"/>/<see cref="VksParseStatuses.All"/> against migration
/// 0111's four named CHECK constraints on <c>vks_library_items</c>
/// (<c>vks_library_items_source_check</c>, <c>_release_line_check</c>,
/// <c>_naming_era_check</c>, <c>_parse_status_check</c>), following this repo's
/// <c>DepotArtifactStatusesConstraintDriftTests</c>/<c>VmToolsConstraintDriftTests</c>
/// convention of parsing the migration SQL itself rather than a live database.
/// Resolution is scoped by BOTH the owning table and the constraint name (#1795 AC1):
/// a CHECK is only read when it is declared inside <c>vks_library_items</c>' own
/// <c>CREATE TABLE</c> body or added to that table by a later
/// <c>ALTER TABLE ... ADD CONSTRAINT</c>, so neither a differently-named nor an
/// identically-named <c>source IN (...)</c> CHECK on some other table can be picked up
/// in its place. Column name alone would not do: four other migrations already declare
/// a <c>source IN (...)</c> CHECK on unrelated tables -- 0036 line 61 unnamed, and
/// 0037 line 32 (<c>managed_tool_installs_source_check</c>), 0052 line 51
/// (<c>benchmark_revisions_source_check</c>) and 0054 line 128
/// (<c>component_observations_source_check</c>) named after their own tables -- and a
/// column-only scan would silently repoint itself at whichever of those sorts last.
/// </summary>
public sealed class VksConstraintDriftTests
{
	private const string Table = "vks_library_items";

	[Fact]
	public void VksItemSourcesAll_IsInLockstepWithSourceCheckConstraintValueSet()
	{
		Assert.Equal(
			VksItemSources.All,
			ParseLatestCheckAcrossMigrations(ReadEmbeddedMigrations(), Table, "vks_library_items_source_check", "source"));
	}

	[Fact]
	public void VksReleaseLinesAll_IsInLockstepWithReleaseLineCheckConstraintValueSet()
	{
		Assert.Equal(
			VksReleaseLines.All,
			ParseLatestCheckAcrossMigrations(ReadEmbeddedMigrations(), Table, "vks_library_items_release_line_check", "release_line"));
	}

	[Fact]
	public void VksNamingErasAll_IsInLockstepWithNamingEraCheckConstraintValueSet()
	{
		Assert.Equal(
			VksNamingEras.All,
			ParseLatestCheckAcrossMigrations(ReadEmbeddedMigrations(), Table, "vks_library_items_naming_era_check", "naming_era"));
	}

	[Fact]
	public void VksParseStatusesAll_IsInLockstepWithParseStatusCheckConstraintValueSet()
	{
		Assert.Equal(
			VksParseStatuses.All,
			ParseLatestCheckAcrossMigrations(ReadEmbeddedMigrations(), Table, "vks_library_items_parse_status_check", "parse_status"));
	}

	/// <summary>
	/// #1795 AC2: a <c>source IN (...)</c> CHECK on another table, in a HIGHER-numbered
	/// migration than 0111, must never change what this guard reads -- here in its
	/// hardest form, the decoy carrying the REAL constraint's own name
	/// (<c>vks_library_items_source_check</c>) on <c>reviewer_probe_table</c>. That is
	/// the round-2 reviewer's own probe, which turned a name-only guard red with
	/// <c>Actual: ["bogus"]</c>; table scoping is what makes the real
	/// <c>['depot','public']</c> still resolve.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_IgnoresAnIdenticallyNamedCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public'))
			);
			""",
			"""
			CREATE TABLE reviewer_probe_table (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('bogus'))
			);
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(migrations, Table, "vks_library_items_source_check", "source");

		Assert.Equal(["depot", "public"], values);
	}

	/// <summary>
	/// #1795 AC2, the everyday form: a DIFFERENTLY-named <c>source IN (...)</c> CHECK on
	/// another table in a higher-numbered migration -- the shape 0037/0052/0054 actually
	/// ship -- must likewise leave the real value set untouched.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_IgnoresADifferentlyNamedSourceCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public'))
			);
			""",
			"""
			CREATE TABLE managed_tool_installs (
			    source TEXT NOT NULL,
			    CONSTRAINT managed_tool_installs_source_check CHECK (source IN ('local-repository', 'depot', 'upload'))
			);
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(migrations, Table, "vks_library_items_source_check", "source");

		Assert.Equal(["depot", "public"], values);
	}

	/// <summary>
	/// A widened real <c>vks_library_items_source_check</c> constraint value set must
	/// be picked up (i.e. no longer equal <see cref="VksItemSources.All"/>) -- the
	/// mutation-test half of #1795's acceptance criteria, proving this guard actually
	/// fails when the vocabulary genuinely diverges rather than passing vacuously.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_DetectsAGenuinelyWidenedRealConstraint()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public', 'widened'))
			);
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(migrations, Table, "vks_library_items_source_check", "source");

		Assert.NotEqual(VksItemSources.All, values);
	}

	/// <summary>
	/// Table scoping must not cost ALTER visibility (the hole
	/// <c>VmToolsConstraintDriftTests</c> was corrected for on PR #1765): a later
	/// migration re-declaring the constraint via this repo's
	/// <c>DROP CONSTRAINT</c>/<c>ADD CONSTRAINT</c> idiom (migration 0129's own shape)
	/// sits outside any <c>CREATE TABLE</c> parens and MUST still be read as the latest
	/// declaration -- the one a fully-migrated database actually enforces.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_PicksUpALaterAlterTableDropAddConstraint()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public'))
			);
			""",
			"""
			ALTER TABLE vks_library_items DROP CONSTRAINT IF EXISTS vks_library_items_source_check;
			ALTER TABLE vks_library_items ADD CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot'));
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(migrations, Table, "vks_library_items_source_check", "source");

		Assert.Equal(["depot"], values);
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
	/// Reads every migration text, in migration order (ordinal on the zero-padded
	/// filename prefix, matching <see cref="NpgsqlSchemaMigrator"/>), and returns the
	/// value list of the LAST <paramref name="constraintName"/> CHECK constraint
	/// declared ON <paramref name="table"/> across them -- i.e. the constraint the
	/// fully-migrated database actually enforces. Both scopes are load-bearing: the
	/// TABLE scope is what #1795 AC1 requires (a same-named CHECK on another table is
	/// invisible here), and the NAME scope is what keeps sibling CHECKs on the same
	/// column of the same table apart.
	/// </summary>
	private static List<string> ParseLatestCheckAcrossMigrations(
		IEnumerable<string> migrationSqlTexts, string table, string constraintName, string columnName)
	{
		List<string>? latest = null;
		foreach (string sql in migrationSqlTexts)
		{
			foreach (List<string> values in FindTableScopedChecks(sql, table, constraintName, columnName))
			{
				latest = values;
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}

	/// <summary>
	/// Every declaration of <paramref name="constraintName"/> on
	/// <paramref name="table"/> within one migration text, in the order it appears.
	/// Two shapes count, and only these two: a <c>CONSTRAINT &lt;name&gt; CHECK (...)</c>
	/// inside that table's own <c>CREATE TABLE</c> body (inline on a column or as a
	/// table-level constraint), and an <c>ALTER TABLE &lt;table&gt; ADD CONSTRAINT
	/// &lt;name&gt; CHECK (...)</c> re-declaration. Anything declared inside another
	/// table's body -- however it is named -- is not a declaration on this table and is
	/// skipped.
	/// </summary>
	private static List<List<string>> FindTableScopedChecks(string sql, string table, string constraintName, string columnName)
	{
		Regex checkPattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex alterPattern = new(
			$@"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?{Regex.Escape(table)}\s+ADD\s+CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

		List<(int Position, List<string> Values)> found = [];

		foreach ((int bodyStart, int bodyEnd) in CreateTableBodies(sql, table))
		{
			foreach (Match match in checkPattern.Matches(sql[bodyStart..bodyEnd]))
			{
				found.Add((bodyStart + match.Index, ParseValues(match)));
			}
		}

		foreach (Match match in alterPattern.Matches(sql))
		{
			found.Add((match.Index, ParseValues(match)));
		}

		return [.. found.OrderBy(entry => entry.Position).Select(entry => entry.Values)];
	}

	/// <summary>
	/// The (start, end) character bounds of the body of every
	/// <c>CREATE TABLE [IF NOT EXISTS] &lt;table&gt; (...)</c> in one migration text,
	/// found by walking parentheses from the opening one to its match so that nested
	/// parens (a <c>CHECK (...)</c>, a <c>NUMERIC(10, 2)</c>) do not end the body early.
	/// </summary>
	private static List<(int Start, int End)> CreateTableBodies(string sql, string table)
	{
		Regex headerPattern = new(
			$@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?{Regex.Escape(table)}\s*\(",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

		List<(int Start, int End)> bodies = [];
		foreach (Match header in headerPattern.Matches(sql))
		{
			int start = header.Index + header.Length;
			int depth = 1;
			int index = start;
			while (index < sql.Length && depth > 0)
			{
				if (sql[index] == '(')
				{
					depth++;
				}
				else if (sql[index] == ')')
				{
					depth--;
				}

				index++;
			}

			bodies.Add((start, depth == 0 ? index - 1 : sql.Length));
		}

		return bodies;
	}

	private static List<string> ParseValues(Match match)
	{
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);
		return [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
	}
}
