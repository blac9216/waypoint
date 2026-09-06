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
/// against migration 0109's <c>vmtools_artifact_index_platform_check</c> and
/// <c>vmtools_artifact_index_file_type_check</c> constraints, following this repo's
/// convention for every other closed-vocabulary/CHECK pairing (named "lockstep" per
/// this repo's most-repeated review finding: a test parsing the SQL, not just
/// asserting the C# side in isolation).
/// </summary>
public sealed class VmToolsConstraintDriftTests
{
	[Fact]
	public void VmToolsPlatformsAll_IsInLockstepWithPlatformCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckConstraintValues("platform");
		Assert.Equal(VmToolsPlatforms.All, constraintValues);
	}

	[Fact]
	public void VmToolsFileTypesAll_IsInLockstepWithFileTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckConstraintValues("file_type");
		Assert.Equal(VmToolsFileTypes.All, constraintValues);
	}

	/// <summary>
	/// The table this drift guard is scoped to. A CHECK with the same column name on a
	/// different table must never satisfy this guard -- table scope is enforced by
	/// locating the <c>CREATE TABLE</c> block below and only searching for CHECK
	/// constraints inside it, not by matching <c>CHECK (&lt;column&gt; IN (...))</c>
	/// anywhere in the migration text.
	/// </summary>
	private const string TableName = "vmtools_artifact_index";

	/// <summary>
	/// Mutation check for the table-scoping fix: a CHECK constraint of the same column
	/// name declared on a DIFFERENT table must never be picked up as though it belonged
	/// to <see cref="TableName"/>. Before this guard was table-scoped, a
	/// column-anywhere-in-file regex would have wrongly matched the decoy table's
	/// values here.
	/// </summary>
	[Fact]
	public void ExtractTableBlocks_IgnoresCheckConstraintOnADifferentTable()
	{
		const string Sql = """
			CREATE TABLE other_thing (
			    platform TEXT NOT NULL CHECK (platform IN ('bogus'))
			);

			CREATE TABLE IF NOT EXISTS vmtools_artifact_index (
			    platform TEXT NOT NULL CHECK (platform IN ('windows', 'linux'))
			);
			""";

		Regex createTablePattern = new(
			$@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?{Regex.Escape(TableName)}\s*\(",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

		List<string> blocks = [.. ExtractTableBlocks(Sql, createTablePattern)];

		string block = Assert.Single(blocks);
		Assert.Contains("windows", block, StringComparison.Ordinal);
		Assert.DoesNotContain("bogus", block, StringComparison.Ordinal);
	}

	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>), locates each file's <see cref="TableName"/>
	/// <c>CREATE TABLE</c> block (if any), and returns the value list of the LAST
	/// <c>&lt;column&gt; IN (...)</c> CHECK constraint found INSIDE that block across
	/// all migrations -- i.e. the constraint the fully-migrated database actually
	/// enforces on <see cref="TableName"/> specifically, never a same-named CHECK on
	/// some other table.
	/// </summary>
	private static List<string> ParseCheckConstraintValues(string column)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Regex createTablePattern = new(
			$@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?{Regex.Escape(TableName)}\s*\(",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex checkPattern = new(
			$@"CHECK\s*\(\s*{Regex.Escape(column)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			string sql = reader.ReadToEnd();

			foreach (string tableBlock in ExtractTableBlocks(sql, createTablePattern))
			{
				foreach (Match match in checkPattern.Matches(tableBlock))
				{
					latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
				}
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}

	/// <summary>
	/// Yields the substring of each <c>CREATE TABLE ... (</c> match through its matching
	/// closing paren (tracked by depth, since column/CHECK definitions nest their own
	/// parens), so a CHECK constraint search over the returned text can never wander
	/// into a sibling table's definition later in the same file.
	/// </summary>
	private static IEnumerable<string> ExtractTableBlocks(string sql, Regex createTablePattern)
	{
		foreach (Match createMatch in createTablePattern.Matches(sql))
		{
			int depth = 1;
			int i = createMatch.Index + createMatch.Length;
			int start = i;
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
