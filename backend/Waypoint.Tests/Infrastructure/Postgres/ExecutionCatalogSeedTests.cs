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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #959 Option C: migration 0064 seeds the hand-curated execution catalog from
/// docs/compliance-parity.md's documented provenance-matrix rows so a fresh stack is
/// not permanently empty (before this migration, <c>catalog_products</c>/
/// <c>catalog_components</c>/<c>catalog_execution_profiles</c> had zero rows on a fresh
/// database, so no discovered component could ever link to a catalog component --
/// every component reported <c>is_compatible=false</c> "not linked to a known catalog
/// component" even after content imported cleanly).
///
/// This test proves the seed's acceptance intent directly against a real Postgres
/// instance: after a fresh migration, the identity tree exists, is idempotent on
/// re-apply, and a component keyed exactly like a real discovery result (product
/// version + component key) resolves to a linked catalog component with at least one
/// execution profile -- the exact join <c>ComponentCapabilityMatcher</c> needs to ever
/// report <c>is_compatible=true</c>.
///
/// This class shares <see cref="PostgresFixture"/>'s ONE database with every other
/// <c>[Collection("Postgres")]</c> test class (100+ classes, run serially) -- several
/// siblings (e.g. <c>CatalogRepositoryTests</c>) <c>TRUNCATE</c> the entire catalog
/// identity tree in their own <c>InitializeAsync</c> for isolation. Relying on
/// <c>NpgsqlSchemaMigrator.ApplyAsync()</c> alone is NOT sufficient here: once
/// <c>schema_migrations</c> records migration 0064 as applied, the migrator treats it
/// as a no-op forever, even after a sibling test truncates the very rows 0064 inserted.
/// Every test below therefore re-applies 0064's raw SQL directly (bypassing the
/// tracking table) immediately before asserting, which both guarantees the seed rows
/// exist regardless of truncation ordering AND doubles as this migration's own
/// re-apply-idempotency proof.
/// </summary>
[Collection("Postgres")]
public sealed class ExecutionCatalogSeedTests
{
	private readonly PostgresFixture _fixture;

	public ExecutionCatalogSeedTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact]
	public async Task FreshMigration_SeedsExecutionCatalog_AcrossEveryDocumentedShape()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Re-apply 0064's raw SQL directly: a sibling Postgres-collection test may have
		// TRUNCATEd the catalog identity tree since some earlier test's migrator run
		// already marked 0064 applied, and ApplyAsync() alone would then be a no-op.
		await ReapplySeedMigrationAsync(connection);

		// vSphere object-kind split (vmware transport, no selector_name).
		await AssertLinkableComponentAsync(connection, "vsphere", "8.0.3", "vcenter", "vmware", "vcenter");
		await AssertLinkableComponentAsync(connection, "vsphere", "8.0.3", "esxi", "vmware", "esxi");
		await AssertLinkableComponentAsync(connection, "vsphere", "8.0.3", "vm", "vmware", "vm");

		// VCSA named-service split (ssh transport, selector_name required).
		await AssertLinkableComponentAsync(connection, "vsphere", "8.0.3", "eam", "ssh", "service");

		// NSX named-function split (nsx-api transport).
		await AssertLinkableComponentAsync(connection, "nsx", "4.1.2", "manager", "nsx-api", "service");

		// Whole-appliance (ssh/target, no selector_name).
		await AssertLinkableComponentAsync(connection, "photon", "5.0", "photon", "ssh", "target");
	}

	/// <summary>
	/// Issue #959 acceptance intent: "on a fresh stack, discovery against a documented
	/// product can link components (is_compatible) once a baseline activates." This
	/// proves the identity-tree HALF of that chain end to end: given exactly the facts
	/// a real discovery pass would have (product key + exact version + component key),
	/// the seed resolves to a catalog_component with at least one execution profile --
	/// precisely the join <c>ComponentCapabilityMatcher.Match</c> requires before it can
	/// ever report <c>is_compatible=true</c> (baseline activation itself is #731's
	/// runtime state, not identity-tree seed data, and is out of this migration's
	/// scope).
	/// </summary>
	private static async Task AssertLinkableComponentAsync(
		NpgsqlConnection connection, string productKey, string versionKey, string componentKey, string expectedTransport, string expectedSelectorKind)
	{
		await using NpgsqlCommand command = new(
			"""
			SELECT cc.transport, cc.selector_kind, count(ep.id)
			FROM catalog_components cc
			JOIN catalog_product_versions pv ON pv.id = cc.product_version_id
			JOIN catalog_products p ON p.id = pv.product_id
			LEFT JOIN catalog_execution_profiles ep ON ep.component_id = cc.id
			WHERE p.product_key = $1 AND pv.version_key = $2 AND cc.component_key = $3
			GROUP BY cc.transport, cc.selector_kind
			""", connection);
		command.Parameters.AddWithValue(productKey);
		command.Parameters.AddWithValue(versionKey);
		command.Parameters.AddWithValue(componentKey);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		bool found = await reader.ReadAsync();

		Assert.True(found, $"Expected a seeded catalog_component for product '{productKey}' version '{versionKey}' component '{componentKey}'.");
		Assert.Equal(expectedTransport, reader.GetString(0));
		Assert.Equal(expectedSelectorKind, reader.GetString(1));
		Assert.True(reader.GetInt64(2) > 0, $"Expected at least one catalog_execution_profiles row for '{productKey}'/'{versionKey}'/'{componentKey}'.");
	}

	[Fact]
	public async Task ReapplyingSeedSql_Directly_IsIdempotent()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Establish a known-good baseline first (see class remarks: a sibling test may
		// have truncated the catalog tables since 0064 last actually ran), THEN measure
		// the idempotency delta across a second, immediately-following re-apply -- not
		// across whatever count happens to be in the shared database at an arbitrary
		// point in the 100+-class run order.
		await ReapplySeedMigrationAsync(connection);
		long before = await CountAsync(connection, "SELECT count(*) FROM catalog_execution_profiles");
		Assert.True(before > 0, "Expected migration 0064 to have seeded at least one catalog_execution_profiles row.");

		await ReapplySeedMigrationAsync(connection);

		long after = await CountAsync(connection, "SELECT count(*) FROM catalog_execution_profiles");
		Assert.Equal(before, after);
	}

	/// <summary>Re-applies 0064's embedded raw SQL directly against <paramref name="connection"/>, bypassing <c>schema_migrations</c> tracking entirely.</summary>
	private static async Task ReapplySeedMigrationAsync(NpgsqlConnection connection)
	{
		string sql = await ReadEmbeddedMigrationAsync("0064_execution_catalog_seed.sql");
		await using NpgsqlCommand reapply = new(sql, connection);
		await reapply.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
	{
		await using NpgsqlCommand command = new(sql, connection);
		object? result = await command.ExecuteScalarAsync();
		return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
	}

	private static async Task<string> ReadEmbeddedMigrationAsync(string fileName)
	{
		System.Reflection.Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
		await using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync();
	}
}

/// <summary>
/// Class-killing drift guard for migration 0064, mirroring
/// <c>LayoutTableParityTests</c>'s doc-is-authority idiom (issue #959): parses
/// docs/compliance-parity.md's "Sibling source-capability provenance matrix" and
/// "Priority" row directly out of the doc -- the same source a human maintainer edits --
/// and asserts the seed migration's SQL literals agree, so the doc and the shipped seed
/// cannot silently diverge the way the interpreter's family table and the doc did before
/// issue #959.
///
/// Migration 0064 deliberately seeds a representative SLICE of the provenance matrix's
/// 13 rows (its own header comment: "not a byte-for-byte transcription of all 44 sibling
/// scan components... expanding the remaining rows is additive... and does not require a
/// schema change"), so this guard does not require every matrix row to be present. It
/// requires two things instead: (1) for every row the migration DOES cover, the
/// migration's transport/purpose/output values match that row's documented values
/// exactly -- a hand-edit that quietly changes a seeded row's transport/purpose/output
/// without updating the doc (or vice versa) fails this test; and (2) the closed priority
/// vocabulary's exact six report groups/priorities are all present in the seed, since
/// every execution profile's report_group_id FK requires one to exist.
///
/// Pure source/SQL-parsing test -- no Postgres container required (same idiom as
/// <see cref="CatalogNaturalKeyWriteGuardTests"/>/<c>LayoutTableParityTests</c>), so it
/// stays fast and runs everywhere the source does.
/// </summary>
public sealed class ExecutionCatalogSeedDriftGuardTests
{
	private sealed record ProvenanceMatrixRow(string ProductVersionKey, string Kind, string Transport, string Purpose, string Output);

	[Fact]
	public void SeededReportGroups_MatchTheDocumentedClosedPriorityVocabularyExactly()
	{
		string doc = ReadRepoFile("docs", "compliance-parity.md");
		string migration = ReadRepoFile("backend", "Waypoint.Infrastructure", "Data", "Migrations", "0064_execution_catalog_seed.sql");

		// docs/compliance-parity.md "Priority" row: "NSX STIG 1; VCSA STIG 2; vCenter STIG
		// 3; ESXi STIG 4; VM STIG 5; every SRG 6" -- parsed as (label, priority) pairs.
		Match priorityRowMatch = Regex.Match(doc, @"\| Priority \| (?<row>[^|]+) \|");
		Assert.True(priorityRowMatch.Success, "docs/compliance-parity.md is missing the 'Priority' row this guard parses.");

		Dictionary<string, int> documentedPriorities = new(StringComparer.OrdinalIgnoreCase);
		foreach (Match entry in Regex.Matches(priorityRowMatch.Groups["row"].Value, @"([A-Za-z][A-Za-z ]*?)\s+(\d+)"))
		{
			// Strip a leading "every" qualifier ("every SRG 6" documents the closed
			// vocabulary's catch-all row, but the seeded display_name is plain "SRG").
			string label = Regex.Replace(entry.Groups[1].Value.Trim(), @"^every\s+", string.Empty, RegexOptions.IgnoreCase);
			documentedPriorities[label] = int.Parse(entry.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
		}

		Assert.Equal(6, documentedPriorities.Count);

		// catalog_report_groups seed VALUES rows: ('group_key', 'display_name', priority).
		Dictionary<string, int> seededPriorities = new(StringComparer.OrdinalIgnoreCase);
		foreach (Match row in Regex.Matches(migration, @"\('(?<key>[a-z-]+)', '(?<name>[^']+)', (?<priority>\d)\)"))
		{
			seededPriorities[row.Groups["name"].Value.Trim()] = int.Parse(row.Groups["priority"].Value, System.Globalization.CultureInfo.InvariantCulture);
		}

		List<string> failures = [];
		foreach ((string label, int priority) in documentedPriorities)
		{
			if (!seededPriorities.TryGetValue(label, out int seededPriority))
			{
				failures.Add($"Documented priority '{label}' ({priority}) has no matching seeded catalog_report_groups row.");
			}
			else if (seededPriority != priority)
			{
				failures.Add($"'{label}': documented priority {priority} but seed inserts {seededPriority}.");
			}
		}

		Assert.True(failures.Count == 0, "Report-group priority drift:\n" + string.Join("\n", failures));
	}

	/// <summary>
	/// For every provenance-matrix row the seed migration actually covers (vSphere 8-0
	/// STIG vmware/object-kind row, vSphere 8-0 STIG VCSA named-service row, NSX 4-x STIG
	/// named-function row, Photon OS SRG row), the migration's transport/purpose/output
	/// literals must agree with the doc exactly.
	/// </summary>
	[Fact]
	public void SeededExecutionProfiles_MatchTheirDocumentedProvenanceMatrixRow()
	{
		List<ProvenanceMatrixRow> rows = ParseProvenanceMatrixRows();
		Assert.NotEmpty(rows);

		string migration = ReadRepoFile("backend", "Waypoint.Infrastructure", "Data", "Migrations", "0064_execution_catalog_seed.sql");

		AssertRowCoveredCorrectly(rows, migration, "vSphere `8-0`", "STIG", "vmware",
			seededComponentKeys: ["vcenter", "esxi", "vm"], expectedOutputKind: "hdf_ckl");
		AssertRowCoveredCorrectly(rows, migration, "vSphere `8-0`", "STIG", "ssh",
			seededComponentKeys: ["eam", "lookup", "postgresql", "vami"], expectedOutputKind: "hdf_ckl");
		AssertRowCoveredCorrectly(rows, migration, "NSX `4-x`", "STIG", "nsx-api",
			seededComponentKeys: ["manager", "distributed-firewall"], expectedOutputKind: "hdf_ckl");
		AssertRowCoveredCorrectly(rows, migration, "Photon OS `5-0`", "SRG", "ssh",
			seededComponentKeys: ["photon"], expectedOutputKind: "hdf");
	}

	private static void AssertRowCoveredCorrectly(
		List<ProvenanceMatrixRow> rows, string migration, string productVersionKey, string kind, string transport,
		string[] seededComponentKeys, string expectedOutputKind)
	{
		ProvenanceMatrixRow? row = rows.FirstOrDefault(r =>
			string.Equals(r.ProductVersionKey, productVersionKey, StringComparison.Ordinal) &&
			string.Equals(r.Kind, kind, StringComparison.Ordinal) &&
			string.Equals(r.Transport, transport, StringComparison.Ordinal));
		Assert.True(row is not null, $"docs/compliance-parity.md has no provenance-matrix row for '{productVersionKey}' / '{kind}' / '{transport}' -- the seed migration's doc-comment claims to cover it.");

		// Every catalog_execution_profiles VALUES tuple ends with (..., component_key,
		// release_key, report_group_key, profile_version, output_kind) -- find the tuple
		// for each seeded component key covered by this doc row and check its output_kind.
		foreach (string componentKey in seededComponentKeys)
		{
			Match tuple = Regex.Match(migration, $@"'{Regex.Escape(componentKey)}', '[^']+', '[^']+', '[^']+', '(?<output>hdf(?:_ckl)?)'\)");
			Assert.True(tuple.Success, $"Migration 0064 has no catalog_execution_profiles VALUES tuple for component '{componentKey}' -- expected one covering documented row '{productVersionKey}'/'{kind}'.");
			Assert.Equal(expectedOutputKind, tuple.Groups["output"].Value);
			Assert.Equal(row!.Output.Contains("CKL", StringComparison.OrdinalIgnoreCase) ? "hdf_ckl" : "hdf", tuple.Groups["output"].Value);
		}
	}

	/// <summary>
	/// Parses docs/compliance-parity.md's "Sibling source-capability provenance matrix"
	/// table. Row shape: <c>| product/version key | key form | kind / release | components
	/// | transport / selector | purpose | output |</c>.
	/// </summary>
	private static List<ProvenanceMatrixRow> ParseProvenanceMatrixRows()
	{
		string doc = ReadRepoFile("docs", "compliance-parity.md");
		int sectionStart = doc.IndexOf("## Sibling source-capability provenance matrix", StringComparison.Ordinal);
		Assert.True(sectionStart >= 0, "docs/compliance-parity.md is missing the provenance matrix section this guard parses.");
		int sectionEnd = doc.IndexOf("\n## ", sectionStart + 1, StringComparison.Ordinal);
		string section = sectionEnd >= 0 ? doc[sectionStart..sectionEnd] : doc[sectionStart..];

		List<ProvenanceMatrixRow> rows = [];
		foreach (Match rowMatch in Regex.Matches(
			section,
			@"^\| ([^|]+?) \| (exact|family) \| (STIG|SRG) / `[^`]+` \| [^|]+ \| `([a-z-]+)`[^|]*\| ([^|]+) \| ([^|]+) \|$",
			RegexOptions.Multiline))
		{
			rows.Add(new ProvenanceMatrixRow(
				ProductVersionKey: rowMatch.Groups[1].Value.Trim(),
				Kind: rowMatch.Groups[3].Value.Trim(),
				Transport: rowMatch.Groups[4].Value.Trim(),
				Purpose: rowMatch.Groups[5].Value.Trim(),
				Output: rowMatch.Groups[6].Value.Trim()));
		}

		return rows;
	}

	private static string ReadRepoFile(params string[] repoRelativeParts)
	{
		string repoRelativePath = Path.Combine(repoRelativeParts);
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

		throw new FileNotFoundException($"Could not locate {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
	}
}
