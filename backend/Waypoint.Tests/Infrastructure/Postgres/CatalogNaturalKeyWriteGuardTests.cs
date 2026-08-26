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
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Class-killing convention guard for issue #832's defect class: "check-then-insert
/// against a catalog natural key can race under concurrent import." Two instances of
/// this class have now shipped -- the NULL-parent <c>catalog_components</c> race
/// (PR #831) and the <c>catalog_execution_profiles</c> race (#832, this PR) -- so
/// rather than trust the next natural-key write path to remember the lesson, this test
/// enumerates EVERY <c>INSERT INTO catalog_*</c> statement in
/// <c>CatalogRepository.cs</c> and asserts each one is:
///
///   1. Backed by a real Postgres unique constraint/index on its natural key (parsed
///      directly out of the migration SQL resources, not hand-copied here -- a future
///      migration that drops a backing constraint fails this test, not just a
///      production concurrency bug); and
///   2. Either an atomic <c>ON CONFLICT ... DO UPDATE</c> upsert, OR an explicitly
///      allow-listed intentional throw-on-duplicate write (today: only
///      <c>CreateExecutionProfileAsync</c>, issue #728's pinned "execution profiles
///      are immutable identity to a direct caller" contract -- safe under concurrency
///      because the backing constraint still makes one of two racing INSERTs fail
///      loudly rather than duplicate, it simply does not dedupe).
///
/// This is a pure source/SQL-parsing test -- no Postgres container required (unlike
/// most of this directory) -- so it stays fast and runs everywhere the source does.
/// Follows the established drift-guard idiom of <c>SchemaMigrationTests</c>'s
/// migration-SQL-parsing tests and <c>RunnerRoleGrantDriftTests</c>'s live-role guards:
/// parse the authoritative artifacts, don't hand-maintain a parallel list that can
/// silently drift from the code it is supposed to describe.
/// </summary>
public sealed class CatalogNaturalKeyWriteGuardTests
{
	/// <summary>
	/// Insert statements this guard intentionally does not require ON CONFLICT for --
	/// each entry names the exact reason so a future addition must justify itself here
	/// rather than being silently grandfathered in. <c>ExpectedNonUpsertSiteCount</c>
	/// pins exactly how many non-upsert INSERT sites are expected for that table today:
	/// <c>catalog_execution_profiles</c> has TWO insert sites in
	/// <c>CatalogRepository.cs</c> (the throw-on-duplicate
	/// <c>CreateExecutionProfileAsync</c> and the atomic-upsert
	/// <c>UpsertExecutionProfileForPromotionAsync</c>) -- without a count pin, breaking
	/// the SECOND (upsert) site's <c>ON CONFLICT</c> clause would silently still match
	/// this allow-list entry (the table-keyed check alone cannot distinguish "the
	/// allowed non-upsert site" from "an unexpected new non-upsert site on the same
	/// table"). A count mismatch (too many OR too few non-upsert sites) fails loudly.
	/// </summary>
	private static readonly Dictionary<string, (int ExpectedNonUpsertSiteCount, string Reason)> AllowedNonUpsertInserts = new(StringComparer.Ordinal)
	{
		["catalog_execution_profiles"] = (1,
			"CreateExecutionProfileAsync is issue #728's pinned throw-on-duplicate public " +
			"contract (execution profiles are immutable identity to a direct caller, proven " +
			"by CreateExecutionProfile_DuplicateComponentAndRelease_ThrowsInsteadOfOverwriting) " +
			"-- the natural key is still constraint-backed (catalog_execution_profiles_unique), " +
			"so a race between two direct callers fails one loudly rather than duplicating; " +
			"only candidate promotion, which must tolerate a benign race, uses the separate " +
			"atomic UpsertExecutionProfileForPromotionAsync upsert path (issue #832) -- exactly " +
			"one of this table's two INSERT sites is expected to be non-upsert."),
		["catalog_import_reports"] = (1,
			"Deliberately never deduplicated at the header level (migration 0051 doc comment: " +
			"'two distinct pull attempts over the same content are two distinct provenance " +
			"events, ADR-0022 immutable source observations ... will be retained') -- there is " +
			"no natural key to race on, every insert is a new provenance row by design."),
		["catalog_import_report_entries"] = (1,
			"Same provenance-event reasoning as catalog_import_reports -- each entry belongs " +
			"to exactly one already-inserted report id and is never re-keyed/deduplicated."),
	};

	[Fact]
	public void EveryCatalogNaturalKeyInsert_IsBackedByAUniqueConstraint_AndIsAnAtomicUpsertOrAllowListedThrow()
	{
		string repositorySource = ReadRepositorySource();
		List<(string Table, bool HasOnConflict)> inserts = ParseInsertStatements(repositorySource);

		Assert.NotEmpty(inserts);

		HashSet<string> uniqueConstrainedTables = ParseUniqueConstrainedTables(
			ReadMigrationSource("0050_compliance_catalog.sql"),
			ReadMigrationSource("0051_catalog_import_reports.sql"));

		List<string> failures = [];
		foreach (IGrouping<string, (string Table, bool HasOnConflict)> group in inserts.GroupBy(i => i.Table))
		{
			string table = group.Key;
			bool isConstrained = uniqueConstrainedTables.Contains(table);
			bool isExplicitlyAllowed = AllowedNonUpsertInserts.TryGetValue(table, out (int ExpectedNonUpsertSiteCount, string Reason) allowance);
			int nonUpsertSiteCount = group.Count(i => !i.HasOnConflict);

			if (!isConstrained && !isExplicitlyAllowed)
			{
				failures.Add($"{table}: INSERT found with no UNIQUE constraint/index backing a natural key anywhere in migrations 0050/0051, and not in the explicit allow-list -- a concurrent duplicate is possible with no DB-level guard at all.");
				continue;
			}

			if (nonUpsertSiteCount == 0)
			{
				// Every INSERT site for this table is an atomic upsert -- exactly the
				// required shape, no allow-list entry needed (or expected).
				continue;
			}

			// At least one non-upsert INSERT site exists for this table: it must be
			// explicitly allow-listed AND the count must match exactly -- an unexpected
			// non-upsert site (e.g. an upsert's ON CONFLICT clause was accidentally
			// dropped) changes the count and fails loudly rather than silently matching
			// the table-keyed allow-list entry meant for a DIFFERENT insert site.
			if (!isExplicitlyAllowed)
			{
				failures.Add($"{table}: {nonUpsertSiteCount} INSERT site(s) without ON CONFLICT and not in the explicit allow-list -- either add an atomic upsert or justify the exception in AllowedNonUpsertInserts.");
			}
			else if (nonUpsertSiteCount != allowance.ExpectedNonUpsertSiteCount)
			{
				failures.Add($"{table}: found {nonUpsertSiteCount} non-upsert INSERT site(s) but the allow-list expects exactly {allowance.ExpectedNonUpsertSiteCount} -- a new non-upsert site appeared (or an upsert's ON CONFLICT clause was dropped); update ExpectedNonUpsertSiteCount only if the new site is itself justified.");
			}
		}

		Assert.True(failures.Count == 0, "Catalog natural-key write guard failures:\n" + string.Join("\n", failures));
	}

	/// <summary>
	/// Every table named in <see cref="AllowedNonUpsertInserts"/> must actually appear
	/// as a non-upsert INSERT target today -- an allow-list entry for a table that no
	/// longer has one is stale documentation, not an active exception, and should be
	/// removed so the list stays an accurate map of real exceptions.
	/// </summary>
	[Fact]
	public void AllowListedNonUpsertTables_StillHaveANonUpsertInsertSite()
	{
		string repositorySource = ReadRepositorySource();
		List<(string Table, bool HasOnConflict)> inserts = ParseInsertStatements(repositorySource);

		foreach (string allowedTable in AllowedNonUpsertInserts.Keys)
		{
			bool stillHasNonUpsertSite = inserts.Any(i => i.Table == allowedTable && !i.HasOnConflict);
			Assert.True(stillHasNonUpsertSite, $"{allowedTable} is allow-listed as an intentional non-upsert INSERT but no such INSERT site was found -- remove the stale allow-list entry.");
		}
	}

	private static List<(string Table, bool HasOnConflict)> ParseInsertStatements(string source)
	{
		List<(string, bool)> results = [];

		// Split on raw C# string literal boundaries the repository's own SQL blocks use
		// (triple-quoted raw strings): find every occurrence of "INSERT INTO catalog_"
		// and capture the table name plus whether "ON CONFLICT" appears before the next
		// occurrence of a raw-string terminator ("""), which is how every SQL command
		// text in this file is written.
		MatchCollection tableMatches = Regex.Matches(source, @"INSERT INTO (catalog_\w+)");
		foreach (Match tableMatch in tableMatches)
		{
			string table = tableMatch.Groups[1].Value;
			int statementStart = tableMatch.Index;
			int rawStringEnd = source.IndexOf("\"\"\"", statementStart, StringComparison.Ordinal);
			int windowEnd = rawStringEnd >= 0 ? rawStringEnd : Math.Min(source.Length, statementStart + 2000);
			string window = source[statementStart..windowEnd];
			bool hasOnConflict = window.Contains("ON CONFLICT", StringComparison.Ordinal);
			results.Add((table, hasOnConflict));
		}

		return results;
	}

	/// <summary>
	/// Parses every table with a UNIQUE constraint or unique index (plain or partial)
	/// declared anywhere in the given migration SQL texts -- covers both
	/// <c>CONSTRAINT ... UNIQUE (...)</c> / inline <c>UNIQUE</c> column modifiers and
	/// <c>CREATE UNIQUE INDEX ... ON &lt;table&gt;</c> (the partial-index shape 0051 uses
	/// for the NULL-parent case).
	/// </summary>
	private static HashSet<string> ParseUniqueConstrainedTables(params string[] migrationTexts)
	{
		HashSet<string> tables = new(StringComparer.Ordinal);

		foreach (string sql in migrationTexts)
		{
			// CREATE TABLE ... (... UNIQUE ... or CONSTRAINT ... UNIQUE ...) -- scan each
			// CREATE TABLE block for the table name and whether it contains "UNIQUE"
			// anywhere in its column/constraint list.
			foreach (Match tableBlock in Regex.Matches(sql, @"CREATE TABLE IF NOT EXISTS (catalog_\w+)\s*\((?<body>.*?)\n\);", RegexOptions.Singleline))
			{
				string table = tableBlock.Groups[1].Value;
				string body = tableBlock.Groups["body"].Value;
				if (body.Contains("UNIQUE", StringComparison.Ordinal))
				{
					tables.Add(table);
				}
			}

			// CREATE UNIQUE INDEX ... ON <table> (...) [WHERE ...] -- catches the
			// migration-0051-style partial unique index that is NOT inside the
			// CREATE TABLE block at all.
			foreach (Match indexMatch in Regex.Matches(sql, @"CREATE UNIQUE INDEX[^;]*?\bON\s+(catalog_\w+)\b"))
			{
				tables.Add(indexMatch.Groups[1].Value);
			}
		}

		return tables;
	}

	private static string ReadRepositorySource() => ReadSourceFile(
		Path.Combine("backend", "Waypoint.Infrastructure", "ComplianceContent", "CatalogRepository.cs"));

	private static string ReadMigrationSource(string fileName) => ReadSourceFile(
		Path.Combine("backend", "Waypoint.Infrastructure", "Data", "Migrations", fileName));

	/// <summary>
	/// Walks up from the test binary's output directory to the repo root (same
	/// technique as <c>ModulePreloadCompletenessTests</c>/<c>RunnerEgressTopologyTests</c>)
	/// and reads the given repo-relative source file as text.
	/// </summary>
	private static string ReadSourceFile(string repoRelativePath)
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(dir.FullName, repoRelativePath);
			if (File.Exists(candidate))
			{
				return File.ReadAllText(candidate);
			}

			dir = dir.Parent;
		}

		throw new FileNotFoundException(
			$"Could not locate {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
	}
}
