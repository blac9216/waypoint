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
///
/// <para>Both halves read SQL lexically rather than textually: comments, string
/// literals, quoted identifiers and dollar-quoted bodies are recognised, so a paren
/// in comment prose cannot walk the body scope out of its own table and a
/// commented-out declaration is not mistaken for a live one. See
/// <see cref="FindCreateTableBodies"/> for what that does and does not cover, and
/// PR #1832 round-2 finding F10 for the cross-table read it closes.</para>
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

			foreach (Match match in alterAddConstraint.Matches(BlankComments(sql)))
			{
				latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
			}
		}

		return latest;
	}

	/// <summary>
	/// Yields the body text of every <c>CREATE TABLE [IF NOT EXISTS] &lt;tableName&gt;
	/// (...)</c> declaration in <paramref name="sql"/>, delimited by walking parens by
	/// hand (not a <c>[^)]*</c> regex) because a table body legitimately nests parens
	/// (e.g. a <c>CHECK (col IN ('a', 'b'))</c> or a <c>NUMERIC(10, 2)</c> column type),
	/// which would truncate a naive single-level match at the first inner close-paren.
	///
	/// <para>The walk is SQL-lexically aware, via <see cref="ClassifySql"/>: parens are
	/// counted ONLY where they are code. A paren inside a <c>--</c> line comment, a
	/// <c>/* */</c> block comment, a single-quoted literal, a double-quoted identifier
	/// or a <c>$tag$</c> dollar-quoted body is not counted, and a <c>CREATE TABLE</c>
	/// header that itself sits inside a comment or literal is not treated as a
	/// declaration. This is not defensive theatre: before it, comment parens that
	/// cancelled ACROSS two bodies (one body's comment carrying a net extra <c>(</c>,
	/// a later body's a net extra <c>)</c>) walked the first table's "body" straight
	/// through the second, and the scan then returned the OTHER table's CHECK value
	/// list with nothing asserting -- exactly the cross-table read #1814 exists to
	/// eliminate (PR #1832 round-2 finding F10). The corpus really contains that
	/// shape: <c>0063_component_results.sql</c>, <c>0054_components.sql</c> and
	/// <c>0023_run_secrets.sql</c> all carry unbalanced parens in prose comments
	/// INSIDE a <c>CREATE TABLE</c> body; they happen to cancel within one body today,
	/// so before this fix the repo's drift guards were correct by accident of English
	/// punctuation rather than by the scoping logic.</para>
	///
	/// <para>Comment text is BLANKED (each comment character replaced by a space) in
	/// the yielded body, so a CHECK constraint mentioned in prose cannot be parsed as
	/// a real declaration; literal and quoted-identifier text is PRESERVED, because
	/// the caller's value-list regex reads the constraint's quoted values out of it.
	/// What the walk does NOT do: it does not understand <c>E'...'</c> backslash
	/// escapes (no migration in this corpus uses them) and it does not resolve
	/// schema-qualified table names (no migration in this corpus emits them either).
	/// Both are stated limits, not silent ones -- an unresolvable table yields no body
	/// and <see cref="ParseLatestTableScopedCheckAcrossMigrations"/>'s asserts fire.</para>
	/// </summary>
	private static IEnumerable<string> FindCreateTableBodies(string sql, string tableName)
	{
		SqlSpan[] spans = ClassifySql(sql);

		Regex header = new(
			$@"CREATE TABLE\s+(?:IF NOT EXISTS\s+)?{Regex.Escape(tableName)}\s*\(",
			RegexOptions.IgnoreCase);

		foreach (Match headerMatch in header.Matches(sql))
		{
			// A CREATE TABLE that is itself commented out, or that appears inside a
			// string literal, declares nothing.
			if (spans[headerMatch.Index] != SqlSpan.Code)
			{
				continue;
			}

			int start = headerMatch.Index + headerMatch.Length;
			int depth = 1;
			int i = start;
			while (i < sql.Length && depth > 0)
			{
				if (spans[i] == SqlSpan.Code)
				{
					if (sql[i] == '(')
					{
						depth++;
					}
					else if (sql[i] == ')')
					{
						depth--;
					}
				}

				i++;
			}

			if (depth == 0)
			{
				yield return BlankComments(sql, spans, start, i - 1);
			}
		}
	}

	/// <summary>
	/// Lexical class of each character of a SQL text, as far as the paren walk needs to
	/// care: is this character part of the statement's code, of a comment, or of a
	/// quoted run (string literal, quoted identifier, or dollar-quoted body)?
	/// </summary>
	private enum SqlSpan
	{
		Code,
		Comment,
		Quoted,
	}

	/// <summary>
	/// Classifies every character of <paramref name="sql"/> in one left-to-right pass,
	/// recognising <c>--</c> line comments, <c>/* */</c> block comments (nesting, as
	/// PostgreSQL does), <c>'...'</c> literals and <c>"..."</c> identifiers (both with
	/// the doubled-quote escape), and <c>$tag$...$tag$</c> dollar-quoted bodies. An
	/// unterminated run classifies to end of text rather than throwing -- a truncated
	/// migration should make the scan find nothing and assert, never crash mid-parse.
	/// </summary>
	private static SqlSpan[] ClassifySql(string sql)
	{
		SqlSpan[] spans = new SqlSpan[sql.Length];
		int i = 0;
		while (i < sql.Length)
		{
			char c = sql[i];

			if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
			{
				while (i < sql.Length && sql[i] != '\n')
				{
					spans[i] = SqlSpan.Comment;
					i++;
				}

				continue;
			}

			if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
			{
				int nesting = 0;
				while (i < sql.Length)
				{
					if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
					{
						nesting++;
						spans[i] = SqlSpan.Comment;
						spans[i + 1] = SqlSpan.Comment;
						i += 2;
						continue;
					}

					if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
					{
						nesting--;
						spans[i] = SqlSpan.Comment;
						spans[i + 1] = SqlSpan.Comment;
						i += 2;
						if (nesting == 0)
						{
							break;
						}

						continue;
					}

					spans[i] = SqlSpan.Comment;
					i++;
				}

				continue;
			}

			if (c == '\'' || c == '"')
			{
				spans[i] = SqlSpan.Quoted;
				i++;
				while (i < sql.Length)
				{
					if (sql[i] == c)
					{
						spans[i] = SqlSpan.Quoted;
						i++;

						// A doubled quote is an escaped quote, not the end of the run.
						if (i < sql.Length && sql[i] == c)
						{
							spans[i] = SqlSpan.Quoted;
							i++;
							continue;
						}

						break;
					}

					spans[i] = SqlSpan.Quoted;
					i++;
				}

				continue;
			}

			if (c == '$')
			{
				int afterTag = MatchDollarQuoteTag(sql, i);
				if (afterTag > i)
				{
					string tag = sql[i..afterTag];
					int close = sql.IndexOf(tag, afterTag, StringComparison.Ordinal);
					int end = close < 0 ? sql.Length : close + tag.Length;
					for (int j = i; j < end; j++)
					{
						spans[j] = SqlSpan.Quoted;
					}

					i = end;
					continue;
				}
			}

			spans[i] = SqlSpan.Code;
			i++;
		}

		return spans;
	}

	/// <summary>
	/// If a dollar-quote opening tag (<c>$$</c> or <c>$identifier$</c>) starts at
	/// <paramref name="start"/>, returns the index just past its closing <c>$</c>;
	/// otherwise returns <paramref name="start"/> (a bare <c>$</c>, e.g. a positional
	/// parameter, is ordinary code).
	/// </summary>
	private static int MatchDollarQuoteTag(string sql, int start)
	{
		int i = start + 1;
		while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
		{
			i++;
		}

		return i < sql.Length && sql[i] == '$' ? i + 1 : start;
	}

	/// <summary>
	/// Returns <c>sql[start..end]</c> with every comment character replaced by a space,
	/// so prose in a comment cannot be parsed as SQL while the surrounding text keeps
	/// its offsets and its tokens stay separated.
	/// </summary>
	private static string BlankComments(string sql, SqlSpan[] spans, int start, int end)
	{
		char[] buffer = new char[end - start];
		for (int i = start; i < end; i++)
		{
			buffer[i - start] = spans[i] == SqlSpan.Comment ? ' ' : sql[i];
		}

		return new string(buffer);
	}

	/// <summary>
	/// Returns <paramref name="sql"/> with every comment character replaced by a space,
	/// for the whole-text <c>ALTER TABLE ... ADD CONSTRAINT</c> scan: a commented-out
	/// re-declaration must not be read as the constraint the database enforces, for the
	/// same reason a commented-out CREATE TABLE is not a declaration.
	/// </summary>
	private static string BlankComments(string sql)
	{
		return BlankComments(sql, ClassifySql(sql), 0, sql.Length);
	}
}
