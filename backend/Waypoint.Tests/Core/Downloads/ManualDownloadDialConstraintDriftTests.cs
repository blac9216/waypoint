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
/// Drift guard for the two closed vocabularies migration 0107 introduced (issue
/// #1406) alongside <c>ManualDownloadRetentionDialResolver.Parse</c> (issue #1440),
/// following this repo's convention for every other closed-vocabulary/CHECK pairing
/// (<c>OciBundleStatusesConstraintDriftTests</c>, <c>RunTypesConstraintDriftTests</c>,
/// <c>InventoryItemTypesConstraintDriftTests</c>,
/// <c>ComponentResultStatusConstraintDriftTests</c>): parse the authoritative value
/// set straight out of the embedded migration SQL (no live database) and assert the
/// application-side constant matches exactly, in order. Issue #1686: neither
/// <see cref="ManualDownloadDialOptions"/> nor <see cref="RetainedContentStates"/> had
/// this guard despite <c>Parse</c> throwing on any value outside the three/five
/// constants -- a migration that widens or renames either CHECK without mirroring the
/// C# side previously produced only a hard runtime <see cref="ArgumentException"/> in
/// the retention path, with nothing in CI failing.
///
/// Resolution is scoped by BOTH the owning table and the constraint name (PR #1821
/// review round 1, F2 -- the exact shape #1795 fixed for
/// <c>VksConstraintDriftTests</c> in PR #1782, and that issue #1814, open, exists to
/// remove from the remaining name-only helpers): a CHECK is only read when it is
/// declared inside the owning table's own <c>CREATE TABLE</c> body or added to that
/// table by a later <c>ALTER TABLE ... ADD CONSTRAINT</c>, so an identically- OR
/// differently-named <c>... IN (...)</c> CHECK on some other table can never be picked
/// up in its place. Round-1 review proved a name-only scan wrong by mutation: a
/// scratch migration declaring a brand-new <c>reviewer_probe_table</c> with a CHECK
/// constraint carrying the REAL constraint's own name
/// (<c>download_retention_policies_dial_check</c>) repointed the old, name-only guard
/// at the decoy's <c>('decoy-only')</c> value set (dropped, observed red, removed,
/// tree restored byte-identical -- see PR #1821's evidence). The two "ignores a decoy
/// on a different table" tests below keep that proof permanent.
///
/// Issue #1660 note: this follows the same "latest declaration wins" resolution the
/// four column-only exemplars above and <c>VksConstraintDriftTests</c> already use
/// (last CHECK match across migration-ordered resources); if #1660 changes that
/// repo-wide convention, this file should follow suit rather than keep its own copy.
/// </summary>
public sealed class ManualDownloadDialConstraintDriftTests
{
	private const string RetentionPoliciesTable = "download_retention_policies";
	private const string RetainedContentStateTable = "download_retained_content_state";

	[Fact]
	public void ManualDownloadDialOptionsAll_EqualsDownloadRetentionPoliciesDialCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckAcrossMigrations(
			ReadEmbeddedMigrations(), RetentionPoliciesTable, "download_retention_policies_dial_check", "manual_download_dial_default");

		Assert.Equal(ManualDownloadDialOptions.All, constraintValues);
	}

	[Fact]
	public void RetainedContentStatesAll_EqualsDownloadRetainedContentStateStateCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckAcrossMigrations(
			ReadEmbeddedMigrations(), RetainedContentStateTable, "download_retained_content_state_state_check", "state");

		Assert.Equal(RetainedContentStates.All, constraintValues);
	}

	/// <summary>
	/// Issue #1686 AC3: <c>ManualDownloadRetentionDialResolver.ToWireValue</c>'s
	/// <c>default</c> arm throws for any <see cref="ManualDownloadDial"/> member it
	/// does not explicitly handle -- iterating every current member here turns a
	/// future member added without updating <c>Parse</c>/<c>ToWireValue</c> into a
	/// failing test the moment it is added, rather than a defect that only surfaces
	/// as a runtime throw the first time that value is actually resolved. The
	/// round-trip (wire value back to the same enum member) also pins
	/// <see cref="ManualDownloadRetentionDialResolver.Parse"/> against the same set.
	/// </summary>
	[Fact]
	public void ManualDownloadDial_EveryEnumMember_RoundTripsThroughToWireValueAndParse()
	{
		foreach (ManualDownloadDial dial in Enum.GetValues<ManualDownloadDial>())
		{
			string wireValue = ManualDownloadRetentionDialResolver.ToWireValue(dial);

			Assert.Contains(wireValue, ManualDownloadDialOptions.All);
			Assert.Equal(dial, ManualDownloadRetentionDialResolver.Parse(wireValue));
		}
	}

	/// <summary>
	/// PR #1821 review round 1, F2 -- the round-1 reviewer's own probe, reproduced
	/// here permanently: a CHECK on an unrelated table carrying the REAL dial
	/// constraint's own name must never be read in its place. Table scoping is what
	/// makes the real <c>['auto-prune', 'keep', 'review']</c> still resolve.
	/// </summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_IgnoresAnIdenticallyNamedDialCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS download_retention_policies (
			    manual_download_dial_default TEXT NOT NULL CONSTRAINT download_retention_policies_dial_check CHECK (manual_download_dial_default IN ('auto-prune', 'keep', 'review'))
			);
			""",
			"""
			CREATE TABLE reviewer_probe_table (
			    manual_download_dial_default TEXT NOT NULL CONSTRAINT download_retention_policies_dial_check CHECK (manual_download_dial_default IN ('decoy-only'))
			);
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(
			migrations, RetentionPoliciesTable, "download_retention_policies_dial_check", "manual_download_dial_default");

		Assert.Equal(["auto-prune", "keep", "review"], values);
	}

	/// <summary>Same proof as above for the state vocabulary/table pairing, so both halves of this guard carry the same discrimination coverage.</summary>
	[Fact]
	public void ParseLatestCheckAcrossMigrations_IgnoresAnIdenticallyNamedStateCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS download_retained_content_state (
			    state TEXT NOT NULL CONSTRAINT download_retained_content_state_state_check CHECK (state IN ('tracked', 'grace', 'pinned', 'pending-purge', 'purged'))
			);
			""",
			"""
			CREATE TABLE reviewer_probe_table_2 (
			    state TEXT NOT NULL CONSTRAINT download_retained_content_state_state_check CHECK (state IN ('decoy-only'))
			);
			""",
		];

		List<string> values = ParseLatestCheckAcrossMigrations(
			migrations, RetainedContentStateTable, "download_retained_content_state_state_check", "state");

		Assert.Equal(["tracked", "grace", "pinned", "pending-purge", "purged"], values);
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
	/// TABLE scope is what keeps an identically- or differently-named CHECK on
	/// another table invisible here, and the NAME scope keeps sibling CHECKs on the
	/// same table apart.
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
	/// Every declaration of <paramref name="constraintName"/> on <paramref name="table"/>
	/// within one migration text, in the order it appears. Two shapes count, and only
	/// these two: a <c>CONSTRAINT &lt;name&gt; CHECK (...)</c> inside that table's own
	/// <c>CREATE TABLE</c> body (inline on a column or as a table-level constraint),
	/// and an <c>ALTER TABLE &lt;table&gt; ADD CONSTRAINT &lt;name&gt; CHECK (...)</c>
	/// re-declaration. Anything declared inside another table's body -- however it is
	/// named -- is not a declaration on this table and is skipped.
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
