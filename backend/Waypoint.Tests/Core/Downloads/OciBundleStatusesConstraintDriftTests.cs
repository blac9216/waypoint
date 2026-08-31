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
/// Drift guard for <see cref="OciBundleStatuses.All"/> against migration 0118's
/// <c>oci_bundles_status_check</c>, following this repo's convention for every other
/// closed-vocabulary/CHECK pairing (<c>RunTypesConstraintDriftTests</c>,
/// <c>InventoryItemTypesConstraintDriftTests</c>,
/// <c>ComponentResultStatusConstraintDriftTests</c>,
/// <c>SchemaMigrationTests.Migration0050/0051_...</c>): parse the authoritative value
/// set out of the embedded migration SQL and assert it equals the C# constant, in
/// order. #1413 and #1441 are both explicitly designed to drive rows through this
/// vocabulary, so a future widening or rename of the constraint that is not mirrored
/// in <see cref="OciBundleStatuses.All"/> must fail here rather than pass silently.
/// </summary>
public sealed class OciBundleStatusesConstraintDriftTests
{
	[Fact]
	public void OciBundleStatusesAll_EqualsOciBundlesStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestOciBundlesStatusCheckValues();

		Assert.Equal(OciBundleStatuses.All, constraintValues);
	}

	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>) and returns the value list of the LAST
	/// <c>oci_bundles_status_check</c> CHECK constraint declared across them -- i.e.
	/// the constraint the fully-migrated database actually enforces.
	/// </summary>
	private static List<string> ParseLatestOciBundlesStatusCheckValues()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		// Matches `status IN ( 'a', 'b', ... )` inside oci_bundles_status_check,
		// tolerant of arbitrary whitespace/newlines between values.
		Regex checkPattern = new(
			@"CONSTRAINT\s+oci_bundles_status_check\s+CHECK\s*\(\s*status\s+IN\s*\((?<values>[^)]*)\)",
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
