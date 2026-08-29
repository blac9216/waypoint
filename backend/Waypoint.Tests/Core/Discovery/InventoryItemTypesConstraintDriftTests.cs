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
using Waypoint.Core.Discovery;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Core.Discovery;

/// <summary>
/// Drift guard for migration 0077's widened <c>inventory_items_type_check</c> (issue
/// #1081 added the <c>vcenter</c> value alongside 0011's original
/// <c>cluster</c>/<c>host</c>/<c>vm</c>), same mechanism as
/// <c>RunTypesConstraintDriftTests</c>/<c>ComponentResultStatusConstraintDriftTests</c>:
/// parse the authoritative closed value set straight out of the embedded migration
/// SQL (the LAST declaration across every migration, since 0077 redeclares 0011's
/// constraint via <c>DROP CONSTRAINT IF EXISTS ... ADD CONSTRAINT</c> rather than
/// replacing it in place) and assert <see cref="InventoryItemTypes.All"/> matches it
/// exactly, in order -- no live database required.
/// </summary>
public sealed class InventoryItemTypesConstraintDriftTests
{
	[Fact]
	public void InventoryItemTypesAll_EqualsInventoryItemsTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestInventoryItemsTypeCheckValues();

		Assert.Equal(InventoryItemTypes.All, constraintValues);
	}

	private static List<string> ParseLatestInventoryItemsTypeCheckValues()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Regex checkPattern = new(
			@"CONSTRAINT\s+inventory_items_type_check\s+CHECK\s*\(\s*type\s+IN\s*\((?<values>[^)]*)\)",
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
