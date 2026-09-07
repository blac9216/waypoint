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
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Support;

/// <summary>
/// Shared table-scoped CHECK-constraint-vocabulary scan for every
/// <c>*ConstraintDriftTests</c> guard (issue #1814). Every one of these guards parses
/// the authoritative value list of a closed-vocabulary CHECK constraint out of the
/// embedded migration SQL and asserts it against a C#/PowerShell closed set. Before
/// this helper, each guard carried its own copy of
/// <c>CONSTRAINT\s+&lt;name&gt;\s+CHECK\s*\(\s*&lt;column&gt;\s+IN\s*\((?&lt;values&gt;[^)]*)\)</c>
/// matched against the FULL TEXT of every embedded migration -- so a CHECK carrying
/// the same constraint NAME declared on a DIFFERENT table would have been read in
/// place of the real one (found auditing the pattern PR #1782 round 3 introduced for
/// <c>vks_library_items_source_check</c>, and mutation-proven there with a decoy
/// migration declaring the same constraint name on an unrelated table).
///
/// <c>VmToolsConstraintDriftTests</c>'s own round-1 fix (PR #1765 relay) tried scoping
/// purely by CREATE TABLE paren position and found that approach ALTER-invisible: a
/// later migration re-declaring the constraint via this repo's
/// <c>DROP CONSTRAINT</c>/<c>ADD CONSTRAINT</c> idiom sits outside any CREATE TABLE's
/// parens, so a paren-only scope would silently miss the widening. This helper covers
/// both shapes: it scans a table's own <c>CREATE TABLE</c> body (matching balanced
/// parens, not a single-line regex, so a nested paren inside the body cannot truncate
/// the scan early) AND any <c>ALTER TABLE &lt;table&gt; ADD CONSTRAINT &lt;name&gt;
/// CHECK (...)</c> restatement naming that same table, across every embedded migration
/// in order, keeping the LAST declaration found -- i.e. the constraint the
/// fully-migrated database actually enforces, scoped to the one table the caller
/// asked about.
/// </summary>
internal static class ConstraintDriftScan
{
	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>).
	/// </summary>
	internal static IReadOnlyList<string> ReadEmbeddedMigrationSqlInOrder()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		List<string> sqlTexts = new(resourceNames.Length);
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			sqlTexts.Add(reader.ReadToEnd());
		}

		return sqlTexts;
	}

	/// <summary>
	/// Scans every embedded migration and returns the value list of the LAST
	/// <paramref name="constraintName"/> CHECK constraint declared on
	/// <paramref name="tableName"/>, asserting the constraint was found. This is the
	/// entry point every <c>*ConstraintDriftTests</c> guard's production code should
	/// call; <see cref="ParseLatestTableScopedCheckAcrossSql"/> below is the
	/// caller-supplied-SQL core, exposed separately so a test can prove the
	/// table-scoping/ALTER-visibility behavior with synthetic decoy SQL rather than by
	/// adding a real migration file (which would need a real reserved slot).
	/// </summary>
	internal static List<string> ParseLatestTableScopedCheckAcrossMigrations(string tableName, string constraintName, string columnName)
	{
		List<string>? latest = ParseLatestTableScopedCheckAcrossSql(ReadEmbeddedMigrationSqlInOrder(), tableName, constraintName, columnName);
		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}

	/// <summary>
	/// Core scan, decoupled from the embedded-resource source so tests can pass
	/// synthetic migration texts. Returns <see langword="null"/> if
	/// <paramref name="constraintName"/> was never declared on <paramref name="tableName"/>.
	/// </summary>
	internal static List<string>? ParseLatestTableScopedCheckAcrossSql(
		IEnumerable<string> migrationSqlInOrder, string tableName, string constraintName, string columnName)
	{
		Regex inlineCheck = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex alterAddConstraint = new(
			$@"ALTER TABLE\s+(?:IF EXISTS\s+)?(?:ONLY\s+)?{Regex.Escape(tableName)}\s+ADD CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (string sql in migrationSqlInOrder)
		{
			foreach (string body in FindCreateTableBodies(sql, tableName))
			{
				foreach (Match match in inlineCheck.Matches(body))
				{
					latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
				}
			}

			foreach (Match match in alterAddConstraint.Matches(sql))
			{
				latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
			}
		}

		return latest;
	}

	/// <summary>
	/// Yields the balanced-paren body text of every
	/// <c>CREATE TABLE [IF NOT EXISTS] &lt;tableName&gt; (...)</c> declaration in
	/// <paramref name="sql"/> -- walking parens by hand (not a <c>[^)]*</c> regex)
	/// because a table body legitimately nests parens (e.g. a
	/// <c>CHECK (col IN ('a', 'b'))</c> or a <c>NUMERIC(10, 2)</c> column type), which
	/// would truncate a naive single-level match at the first inner close-paren.
	/// </summary>
	private static IEnumerable<string> FindCreateTableBodies(string sql, string tableName)
	{
		Regex header = new(
			$@"CREATE TABLE\s+(?:IF NOT EXISTS\s+)?{Regex.Escape(tableName)}\s*\(",
			RegexOptions.IgnoreCase);

		foreach (Match headerMatch in header.Matches(sql))
		{
			int start = headerMatch.Index + headerMatch.Length;
			int depth = 1;
			int i = start;
			while (i < sql.Length && depth > 0)
			{
				if (sql[i] == '(')
				{
					depth++;
				}
				else if (sql[i] == ')')
				{
					depth--;
				}

				i++;
			}

			if (depth == 0)
			{
				yield return sql[start..(i - 1)];
			}
		}
	}
}
