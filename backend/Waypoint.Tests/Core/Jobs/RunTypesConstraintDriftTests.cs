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
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Core.Jobs;

/// <summary>
/// Drift guard for the class of bug PR #712's review caught: <see cref="RunTypes.All"/>
/// had gone stale against <c>runs_run_type_check</c> (it listed 0001's 11 values while
/// 0042 had grown the constraint to 14). The <c>GET /runs/history</c> <c>run_type</c>
/// filter validates against <see cref="RunTypes.All"/>, so any divergence silently
/// 400s a legitimate history run type. This parses the authoritative
/// <c>runs_run_type_check</c> value set out of the embedded migration SQL (the same
/// resource stream <see cref="SchemaMigrationTests"/> reads) and asserts it is exactly
/// <see cref="RunTypes.All"/> -- no live database required, so it runs in the fast unit
/// pass and fails the instant a migration changes the constraint without the constant
/// being updated in lockstep (and vice versa).
/// </summary>
public sealed class RunTypesConstraintDriftTests
{
	[Fact]
	public void RunTypesAll_EqualsRunsRunTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestRunsRunTypeCheckValues();

		// Ordinal, order-sensitive equality: the constant is documented as matching the
		// constraint "verbatim", so drift in either the set or the order (which is how
		// the API and the frontend NON_COMPLIANCE_RUN_TYPES mirror read it) is a failure.
		Assert.Equal(RunTypes.All, constraintValues);
	}

	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order and
	/// returns the value list of the LAST <c>runs_run_type_check</c> CHECK constraint
	/// declared across them -- i.e. the constraint the fully-migrated database actually
	/// enforces (0001 declared it; 0042 redeclared it via
	/// <c>DROP CONSTRAINT IF EXISTS ... ADD CONSTRAINT</c>). Ordering matches
	/// <see cref="NpgsqlSchemaMigrator"/> (ordinal on the zero-padded filename prefix).
	/// </summary>
	private static List<string> ParseLatestRunsRunTypeCheckValues()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		// Matches `run_type IN ( 'a', 'b', ... )` inside runs_run_type_check, tolerant of
		// arbitrary whitespace/newlines between values.
		Regex checkPattern = new(
			@"CONSTRAINT\s+runs_run_type_check\s+CHECK\s*\(\s*run_type\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			string sql = reader.ReadToEnd();

			foreach (Match match in checkPattern.Matches(sql))
			{
				latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}
}
