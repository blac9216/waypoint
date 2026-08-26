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
/// Sibling of <see cref="CatalogNaturalKeyWriteGuardTests"/>, extended to
/// <c>ComponentRepository.cs</c> per issue #840 ("extend CatalogNaturalKeyWriteGuardTests
/// ... to cover ComponentRepository's natural-key writes so the class guard's scope
/// claim holds"). A dedicated sibling class rather than widening the existing one's
/// table-name regex (which is hardcoded to <c>catalog_\w+</c> and reads only
/// <c>CatalogRepository.cs</c>) -- <c>components</c>/<c>component_observations</c> are a
/// distinct source file with a distinct natural-key shape (a plain UNIQUE constraint for
/// the vendor-identity case, a COALESCE-sentinel partial unique index for the
/// no-vendor-identity case -- migration 0054), so this guard parses that migration
/// directly rather than trying to force one shared table-name pattern to match both.
///
/// Same two guarantees as the catalog guard: every <c>INSERT INTO components</c> /
/// <c>INSERT INTO component_observations</c> statement in
/// <c>ComponentRepository.cs</c> is (1) backed by a real Postgres unique
/// constraint/index parsed from the migration SQL (or, for <c>component_observations</c>,
/// explicitly allow-listed as an intentional non-deduplicated append-only provenance
/// write -- migration 0054's own header: "every write... appends one row here rather
/// than overwriting history"), and (2) an atomic <c>ON CONFLICT ... DO UPDATE</c> upsert
/// where a natural key exists.
/// </summary>
public sealed class ComponentNaturalKeyWriteGuardTests
{
	/// <summary>
	/// <c>component_observations</c> has no natural key to race on by design (migration
	/// 0054: "append-only... every write... appends one row here rather than overwriting
	/// history") -- every INSERT is a new provenance row, same reasoning
	/// <see cref="CatalogNaturalKeyWriteGuardTests"/> already applies to
	/// <c>catalog_import_reports</c>/<c>catalog_import_report_entries</c>.
	/// <c>ComponentRepository.cs</c> has exactly two such INSERT sites today (the
	/// discovered-fact observation in <c>UpsertDiscoveredAsync</c>'s per-item loop, and
	/// the absence observation in its mark-absent pass) plus one more in
	/// <c>SetConfiguredFactAsync</c> -- three total, pinned so an unexpected fourth site
	/// (or the loss of an expected one) fails loudly rather than silently passing.
	/// </summary>
	private static readonly Dictionary<string, (int ExpectedNonUpsertSiteCount, string Reason)> AllowedNonUpsertInserts = new(StringComparer.Ordinal)
	{
		["component_observations"] = (3,
			"Append-only provenance (migration 0054 header: 'every write to components." +
			"configured_fact or components.discovered_fact ... appends one row here " +
			"rather than overwriting history') -- there is no natural key to race on, " +
			"every insert is a new immutable observation row by design. Three sites: " +
			"the discovered-fact observation and the absence observation in " +
			"UpsertDiscoveredAsync, plus the configured-fact observation in " +
			"SetConfiguredFactAsync."),
	};

	[Fact]
	public void EveryComponentsNaturalKeyInsert_IsBackedByAUniqueConstraint_AndIsAnAtomicUpsertOrAllowListedThrow()
	{
		string repositorySource = ReadRepositorySource();
		List<(string Table, bool HasOnConflict)> inserts = ParseInsertStatements(repositorySource);

		Assert.NotEmpty(inserts);

		HashSet<string> uniqueConstrainedTables = ParseUniqueConstrainedTables(ReadMigrationSource("0054_components.sql"));

		List<string> failures = [];
		foreach (IGrouping<string, (string Table, bool HasOnConflict)> group in inserts.GroupBy(i => i.Table))
		{
			string table = group.Key;
			bool isConstrained = uniqueConstrainedTables.Contains(table);
			bool isExplicitlyAllowed = AllowedNonUpsertInserts.TryGetValue(table, out (int ExpectedNonUpsertSiteCount, string Reason) allowance);
			int nonUpsertSiteCount = group.Count(i => !i.HasOnConflict);

			if (!isConstrained && !isExplicitlyAllowed)
			{
				failures.Add($"{table}: INSERT found with no UNIQUE constraint/index backing a natural key anywhere in migration 0054, and not in the explicit allow-list -- a concurrent duplicate is possible with no DB-level guard at all.");
				continue;
			}

			if (nonUpsertSiteCount == 0)
			{
				continue;
			}

			if (!isExplicitlyAllowed)
			{
				failures.Add($"{table}: {nonUpsertSiteCount} INSERT site(s) without ON CONFLICT and not in the explicit allow-list -- either add an atomic upsert or justify the exception in AllowedNonUpsertInserts.");
			}
			else if (nonUpsertSiteCount != allowance.ExpectedNonUpsertSiteCount)
			{
				failures.Add($"{table}: found {nonUpsertSiteCount} non-upsert INSERT site(s) but the allow-list expects exactly {allowance.ExpectedNonUpsertSiteCount} -- a new non-upsert site appeared (or an upsert's ON CONFLICT clause was dropped); update ExpectedNonUpsertSiteCount only if the new site is itself justified.");
			}
		}

		Assert.True(failures.Count == 0, "Component natural-key write guard failures:\n" + string.Join("\n", failures));
	}

	/// <summary>
	/// Issue #840's core assertion, spelled out explicitly rather than only implied by
	/// the table-driven check above: BOTH of migration 0054's unique identity
	/// constraints/indexes (the vendor-identity case and the no-vendor-identity partial
	/// index) must each be named as an ON CONFLICT target somewhere in
	/// <c>UpsertDiscoveredAsync</c> -- proving the atomicity fix covers both branches of
	/// the identity rule, not just one.
	/// </summary>
	[Fact]
	public void UpsertDiscoveredAsync_BindsOnConflictToBothComponents0054UniqueTargets()
	{
		string source = ReadRepositorySource();

		Assert.Contains("ON CONFLICT (parent_target_id, catalog_component_key, vendor_identity)", source);
		Assert.Contains(
			"ON CONFLICT (parent_target_id, COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid), catalog_component_key)",
			source);
		Assert.Contains("WHERE vendor_identity IS NULL", source);
		Assert.Matches(new Regex(@"ON CONFLICT \(parent_target_id, COALESCE\(parent_component_id.*?\)\s*\n\s*WHERE vendor_identity IS NULL\s*\n\s*DO UPDATE SET", RegexOptions.Singleline), source);
	}

	/// <summary>Mirrors <see cref="CatalogNaturalKeyWriteGuardTests.AllowListedNonUpsertTables_StillHaveANonUpsertInsertSite"/>.</summary>
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

		MatchCollection tableMatches = Regex.Matches(source, @"INSERT INTO (components|component_observations)\b");
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

	private static HashSet<string> ParseUniqueConstrainedTables(params string[] migrationTexts)
	{
		HashSet<string> tables = new(StringComparer.Ordinal);

		foreach (string sql in migrationTexts)
		{
			foreach (Match tableBlock in Regex.Matches(sql, @"CREATE TABLE IF NOT EXISTS (components|component_observations)\s*\((?<body>.*?)\n\);", RegexOptions.Singleline))
			{
				string table = tableBlock.Groups[1].Value;
				string body = tableBlock.Groups["body"].Value;
				if (body.Contains("UNIQUE", StringComparison.Ordinal))
				{
					tables.Add(table);
				}
			}

			foreach (Match indexMatch in Regex.Matches(sql, @"CREATE UNIQUE INDEX[^;]*?\bON\s+(components|component_observations)\b"))
			{
				tables.Add(indexMatch.Groups[1].Value);
			}
		}

		return tables;
	}

	private static string ReadRepositorySource() => ReadSourceFile(
		Path.Combine("backend", "Waypoint.Infrastructure", "Components", "ComponentRepository.cs"));

	private static string ReadMigrationSource(string fileName) => ReadSourceFile(
		Path.Combine("backend", "Waypoint.Infrastructure", "Data", "Migrations", fileName));

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
