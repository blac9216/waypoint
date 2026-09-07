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
/// <c>DepotArtifactStatusesConstraintDriftTests</c> convention: a test parsing the SQL
/// itself, scoped by the named constraint rather than by column name alone, so an
/// unrelated CHECK on the same column name in another table (0036/0037/0052/0054 all
/// declare an unnamed <c>source IN (...)</c> CHECK) can never be picked up in its
/// place (#1795).
/// </summary>
public sealed class VksConstraintDriftTests
{
	[Fact]
	public void VksItemSourcesAll_IsInLockstepWithSourceCheckConstraintValueSet()
	{
		Assert.Equal(VksItemSources.All, ParseCheckConstraintValues("vks_library_items_source_check", "source"));
	}

	[Fact]
	public void VksReleaseLinesAll_IsInLockstepWithReleaseLineCheckConstraintValueSet()
	{
		Assert.Equal(VksReleaseLines.All, ParseCheckConstraintValues("vks_library_items_release_line_check", "release_line"));
	}

	[Fact]
	public void VksNamingErasAll_IsInLockstepWithNamingEraCheckConstraintValueSet()
	{
		Assert.Equal(VksNamingEras.All, ParseCheckConstraintValues("vks_library_items_naming_era_check", "naming_era"));
	}

	[Fact]
	public void VksParseStatusesAll_IsInLockstepWithParseStatusCheckConstraintValueSet()
	{
		Assert.Equal(VksParseStatuses.All, ParseCheckConstraintValues("vks_library_items_parse_status_check", "parse_status"));
	}

	/// <summary>
	/// A decoy <c>source IN (...)</c> CHECK on an unrelated table, appearing after
	/// <c>vks_library_items_source_check</c> in ordinal migration order, must never
	/// change what this guard reads -- the exact drift #1795 found: four migrations
	/// (0036, 0037, 0052, 0054) already declare <c>CHECK (source IN (...))</c> on
	/// other tables, and the guard passed only because 0111 happened to sort last
	/// ordinally among them. This proves the fix is scoped by constraint name, not
	/// by scan order.
	/// </summary>
	[Fact]
	public void ParseCheckConstraintValues_IgnoresADecoyCheckOnAnUnrelatedTableNamedIdenticallyToTheRealOne()
	{
		const string sql = """
			CREATE TABLE decoy_table (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('bogus'))
			);
			""";

		List<string> values = ParseCheckConstraintValuesFromSql(sql, "vks_library_items_source_check", "source");

		Assert.Equal(["bogus"], values);
	}

	/// <summary>
	/// A widened real <c>vks_library_items_source_check</c> constraint value set must
	/// be picked up (i.e. no longer equal <see cref="VksItemSources.All"/>) -- the
	/// mutation-test half of #1795's acceptance criteria, proving the guard actually
	/// fails when the vocabulary genuinely diverges.
	/// </summary>
	[Fact]
	public void ParseCheckConstraintValues_DetectsAGenuinelyWidenedRealConstraint()
	{
		const string sql = """
			CREATE TABLE vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public', 'widened'))
			);
			""";

		List<string> values = ParseCheckConstraintValuesFromSql(sql, "vks_library_items_source_check", "source");

		Assert.NotEqual(VksItemSources.All, values);
	}

	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>) and returns the value list of the LAST
	/// <paramref name="constraintName"/> CHECK constraint declared across them -- i.e.
	/// the constraint the fully-migrated database actually enforces. Scoped by the
	/// named constraint (this repo's <c>DepotArtifactStatusesConstraintDriftTests</c>
	/// convention), never by column name alone: several other migrations
	/// (0036/0037/0052/0054) declare an unrelated, unnamed <c>source IN (...)</c>
	/// CHECK on other tables, and a column-only scan would silently repoint itself at
	/// whichever of those sorts last (#1795).
	/// </summary>
	private static List<string> ParseCheckConstraintValues(string constraintName, string column)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		List<string>? latest = null;
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			string sql = reader.ReadToEnd();

			List<string>? found = TryParseCheckConstraintValues(sql, constraintName, column);
			if (found is not null)
			{
				latest = found;
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}

	private static List<string> ParseCheckConstraintValuesFromSql(string sql, string constraintName, string column)
	{
		List<string>? found = TryParseCheckConstraintValues(sql, constraintName, column);
		Assert.NotNull(found);
		return found!;
	}

	private static List<string>? TryParseCheckConstraintValues(string sql, string constraintName, string column)
	{
		Regex checkPattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(column)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (Match match in checkPattern.Matches(sql))
		{
			latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
		}

		return latest;
	}
}
