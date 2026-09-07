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
using Waypoint.Core.Secrets;
using Waypoint.Core.Subscriptions;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1421, migration 0104: this repo's real class-killing drift guard (the
/// <see cref="RepoCredentialBindingConstraintDriftTests"/> convention) for two
/// vocabularies migration 0104 hardcodes as CHECK constraints -- parses the
/// authoritative value set out of the embedded migration SQL and asserts it equals
/// the C# side, in order, so adding/removing a value on either side without the other
/// fails here rather than at runtime.
/// </summary>
public sealed class SubscriptionsConstraintDriftTests
{
	[Fact]
	public void SubscriptionLineGranularityValuesAll_EqualsSubscriptionsLineGranularityCheckConstraintValueSet()
	{
		string migration0104 = ReadMigrationSql("0104_subscriptions_presets.sql");

		Assert.Equal(SubscriptionLineGranularityValues.All, ParseCheckInList(migration0104, "subscriptions_line_granularity_check"));
	}

	[Fact]
	public void SubscriptionLineGranularityValuesAll_EqualsPresetsLineGranularityCheckConstraintValueSet()
	{
		string migration0104 = ReadMigrationSql("0104_subscriptions_presets.sql");

		Assert.Equal(SubscriptionLineGranularityValues.All, ParseCheckInList(migration0104, "presets_line_granularity_check"));
	}

	[Fact]
	public void RepoStoresAll_EqualsSubscriptionsLaneCheckConstraintValueSet()
	{
		string migration0104 = ReadMigrationSql("0104_subscriptions_presets.sql");

		// subscriptions.lane reuses the RepoStores.All acquisition-lane vocabulary
		// (migration 0104's own header comment) rather than a new one.
		Assert.Equal(RepoStores.All, ParseCheckInList(migration0104, "subscriptions_lane_check"));
	}

	/// <summary>The raw text of one embedded migration resource, matched by its filename suffix.</summary>
	private static string ReadMigrationSql(string fileName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith(fileName, StringComparison.Ordinal)));
		using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	/// <summary>Extracts the single-quoted value list of a named <c>CONSTRAINT ... CHECK (col IN ('a', 'b', ...))</c>, in file order.</summary>
	private static List<string> ParseCheckInList(string sql, string constraintName)
	{
		Match constraint = Regex.Match(
			sql,
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\([^)]*\bIN\s*\(([^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Assert.True(constraint.Success, $"Could not locate an IN-list CHECK named '{constraintName}'.");

		MatchCollection values = Regex.Matches(constraint.Groups[1].Value, "'([^']*)'");
		Assert.NotEmpty(values);
		return [.. values.Select(m => m.Groups[1].Value)];
	}
}
