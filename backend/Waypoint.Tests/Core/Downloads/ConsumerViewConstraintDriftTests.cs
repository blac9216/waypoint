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

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #1464, migration 0131: <c>ConsumerViewRepository.TranslateUniqueViolation</c>
/// matches a Postgres 23505 by its constraint/index NAME
/// (<c>idx_consumer_views_name_unique</c> for a duplicate view name,
/// <c>idx_consumer_views_default_unique</c> for a second default) to raise the right
/// typed exception. A rename of either index in the migration, without a matching
/// update to the repository's constants, would silently fall through to an unmapped
/// 500 instead of the documented 409 -- this guard exists to fail loudly here instead.
///
/// PR #1816 round 1 finding Spec 2: the original version of this test asserted with
/// <c>Assert.Contains(name, wholeMigrationText)</c>, which is satisfied by ANY
/// occurrence of the literal -- including the header comment (migration line ~9,
/// "idx_consumer_views_default_unique") and the <c>COMMENT ON TABLE</c> body (line
/// ~91), not only the <c>CREATE UNIQUE INDEX</c>/<c>CONSTRAINT</c> DDL that actually
/// governs runtime behavior. The reviewer proved this hole by renaming ONLY the
/// <c>CREATE UNIQUE INDEX</c> DDL to a decoy name and leaving both prose mentions
/// untouched: all four tests stayed green, because the guard was reading prose, not
/// DDL. This version instead RESOLVES the governing DDL statement -- the actual
/// <c>CREATE UNIQUE INDEX ... ON consumer_views (...)</c> index declaration and the
/// actual <c>CONSTRAINT ... CHECK</c> inside <c>consumer_views</c>' own
/// <c>CREATE TABLE</c> body -- and asserts against the name IT declares, so a DDL
/// rename fails here even when every prose mention is left untouched (see the decoy
/// fixtures below, which reproduce the reviewer's exact rename shape).
/// </summary>
public sealed class ConsumerViewConstraintDriftTests
{
	private const string Table = "consumer_views";

	[Fact]
	public void Migration0131_NameUniqueIndexDdlDeclaresTheNameTheRepositoryTranslates()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		IndexDeclaration index = Assert.Single(
			ParseUniqueIndexes(migration, Table).Where(i => i.Columns == "name"));

		Assert.Equal("idx_consumer_views_name_unique", index.Name);
	}

	[Fact]
	public void Migration0131_DefaultUniqueIndexDdlDeclaresTheNameTheRepositoryTranslates()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		IndexDeclaration index = Assert.Single(
			ParseUniqueIndexes(migration, Table).Where(i => i.Columns == "is_default"));

		Assert.Equal("idx_consumer_views_default_unique", index.Name);
	}

	/// <summary>
	/// The default-unique index must be a PARTIAL index (<c>WHERE is_default</c>) --
	/// a plain unique index on the boolean column would forbid more than one
	/// <c>is_default = false</c> row too, breaking every other view.
	/// </summary>
	[Fact]
	public void Migration0131_DefaultUniqueIndexDdlIsPartialOnIsDefaultTrueOnly()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		IndexDeclaration index = Assert.Single(
			ParseUniqueIndexes(migration, Table).Where(i => i.Columns == "is_default"));

		Assert.NotNull(index.Where);
		Assert.Contains("is_default", index.Where, StringComparison.Ordinal);
	}

	[Fact]
	public void Migration0131_NameNotBlankCheckDdlDeclaresTheExpectedConstraintName()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		string? name = ParseTableScopedCheckConstraintName(migration, Table, "btrim(name)");

		Assert.Equal("consumer_views_name_not_blank_check", name);
	}

	/// <summary>
	/// Reproduces the reviewer's exact round-1 decoy: rename ONLY the
	/// <c>CREATE UNIQUE INDEX</c> DDL, leave the header-comment and
	/// <c>COMMENT ON TABLE</c> prose mentions of the real name untouched. The DDL
	/// resolver must report the DECOY name (proving it reads the DDL, not the prose),
	/// which is exactly what makes
	/// <see cref="Migration0131_DefaultUniqueIndexDdlDeclaresTheNameTheRepositoryTranslates"/>
	/// fail loudly against the real file when this happens for real -- the guard's own
	/// guard.
	/// </summary>
	[Fact]
	public void ParseUniqueIndexes_ResolvesTheDdlNameNotAnUnrelatedProseMentionOfTheRealName()
	{
		string decoyMigration = """
			-- header comment mentioning idx_consumer_views_default_unique as prose,
			-- exactly like the real migration's header, deliberately NOT renamed.
			CREATE TABLE IF NOT EXISTS consumer_views (
			    id UUID PRIMARY KEY,
			    is_default BOOLEAN NOT NULL DEFAULT false
			);

			CREATE UNIQUE INDEX IF NOT EXISTS idx_consumer_views_DECOY_renamed
			    ON consumer_views (is_default)
			    WHERE is_default;

			COMMENT ON TABLE consumer_views IS
			    'Exactly one row may have is_default = true (idx_consumer_views_default_unique).';
			""";

		IndexDeclaration index = Assert.Single(
			ParseUniqueIndexes(decoyMigration, Table).Where(i => i.Columns == "is_default"));

		Assert.Equal("idx_consumer_views_DECOY_renamed", index.Name);
		Assert.NotEqual("idx_consumer_views_default_unique", index.Name);
	}

	/// <summary>Same decoy shape, for the name-unique index.</summary>
	[Fact]
	public void ParseUniqueIndexes_ResolvesTheDdlNameForTheNameUniqueIndexNotProse()
	{
		string decoyMigration = """
			-- idx_consumer_views_name_unique is mentioned here in prose only.
			CREATE TABLE IF NOT EXISTS consumer_views (
			    id UUID PRIMARY KEY,
			    name TEXT NOT NULL
			);

			CREATE UNIQUE INDEX IF NOT EXISTS idx_consumer_views_DECOY_renamed ON consumer_views (name);
			""";

		IndexDeclaration index = Assert.Single(
			ParseUniqueIndexes(decoyMigration, Table).Where(i => i.Columns == "name"));

		Assert.Equal("idx_consumer_views_DECOY_renamed", index.Name);
	}

	/// <summary>
	/// Same decoy shape for the named CHECK constraint: renamed inside the
	/// <c>CREATE TABLE</c> body, with an unrelated prose mention of the real name left
	/// in a comment above it.
	/// </summary>
	[Fact]
	public void ParseTableScopedCheckConstraintName_ResolvesTheDdlNameNotProse()
	{
		string decoyMigration = """
			-- consumer_views_name_not_blank_check enforces the name is non-blank.
			CREATE TABLE IF NOT EXISTS consumer_views (
			    id UUID PRIMARY KEY,
			    name TEXT NOT NULL,
			    CONSTRAINT consumer_views_DECOY_renamed_check CHECK (btrim(name) <> '')
			);
			""";

		string? name = ParseTableScopedCheckConstraintName(decoyMigration, Table, "btrim(name)");

		Assert.Equal("consumer_views_DECOY_renamed_check", name);
	}

	/// <summary>
	/// A same-named CHECK declared on a DIFFERENT table must never be picked up in
	/// place of <c>consumer_views</c>' own -- table scoping, same convention as
	/// <c>VksConstraintDriftTests</c>.
	/// </summary>
	[Fact]
	public void ParseTableScopedCheckConstraintName_IgnoresAnIdenticallyNamedCheckOnADifferentTable()
	{
		string migration = """
			CREATE TABLE some_other_table (
			    name TEXT NOT NULL,
			    CONSTRAINT consumer_views_name_not_blank_check CHECK (btrim(name) <> '')
			);

			CREATE TABLE IF NOT EXISTS consumer_views (
			    id UUID PRIMARY KEY,
			    name TEXT NOT NULL,
			    CONSTRAINT consumer_views_name_not_blank_check_real CHECK (btrim(name) <> '')
			);
			""";

		string? name = ParseTableScopedCheckConstraintName(migration, Table, "btrim(name)");

		Assert.Equal("consumer_views_name_not_blank_check_real", name);
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

	private sealed record IndexDeclaration(string Name, string Columns, string? Where);

	/// <summary>
	/// Every <c>CREATE UNIQUE INDEX [IF NOT EXISTS] &lt;name&gt; ON &lt;table&gt;
	/// (&lt;columns&gt;) [WHERE &lt;predicate&gt;];</c> statement scoped to
	/// <paramref name="table"/> -- the actual DDL, never a comment or
	/// <c>COMMENT ON TABLE</c> mention of the same name (those are not matched by this
	/// pattern at all, since they do not start with <c>CREATE UNIQUE INDEX</c>).
	/// </summary>
	private static List<IndexDeclaration> ParseUniqueIndexes(string sql, string table)
	{
		Regex pattern = new(
			$@"CREATE\s+UNIQUE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>\w+)\s+ON\s+{Regex.Escape(table)}\s*\(\s*(?<columns>[^)]*?)\s*\)\s*(?:WHERE\s+(?<where>[^;]*?)\s*)?;",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

		return [.. pattern.Matches(sql).Select(match => new IndexDeclaration(
			match.Groups["name"].Value,
			match.Groups["columns"].Value,
			match.Groups["where"].Success ? match.Groups["where"].Value : null))];
	}

	/// <summary>
	/// The name of the CHECK constraint whose expression contains
	/// <paramref name="expressionHint"/>, declared inside <paramref name="table"/>'s
	/// own <c>CREATE TABLE</c> body only -- walks parentheses from the table header to
	/// its matching close so a nested <c>CHECK (...)</c> does not end the body early,
	/// same convention as <c>VksConstraintDriftTests.CreateTableBodies</c>.
	/// </summary>
	private static string? ParseTableScopedCheckConstraintName(string sql, string table, string expressionHint)
	{
		Regex headerPattern = new(
			$@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?{Regex.Escape(table)}\s*\(",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex checkPattern = new(
			$@"CONSTRAINT\s+(?<name>\w+)\s+CHECK\s*\([^)]*{Regex.Escape(expressionHint)}[^)]*\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

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

			int end = depth == 0 ? index - 1 : sql.Length;
			Match check = checkPattern.Match(sql[start..end]);
			if (check.Success)
			{
				return check.Groups["name"].Value;
			}
		}

		return null;
	}
}
