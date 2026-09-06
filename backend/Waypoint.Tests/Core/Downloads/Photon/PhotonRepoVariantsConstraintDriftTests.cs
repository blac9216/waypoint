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
using Waypoint.Core.Downloads.Photon;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Core.Downloads.Photon;

/// <summary>
/// Drift guard for <see cref="PhotonRepoVariants.All"/>/<see cref="PhotonArches.All"/>
/// against migration 0129's <c>photon_repo_index_variant_check</c>/
/// <c>photon_repo_index_arch_check</c>, following this repo's convention for every
/// other closed-vocabulary/CHECK pairing (<c>OciBundleStatusesConstraintDriftTests</c>,
/// <c>RunTypesConstraintDriftTests</c>): parse the authoritative value set out of the
/// embedded migration SQL and assert it equals the C# constant set (as sets --
/// PhotonRepoDiscoveryJobHandler iterates <see cref="PhotonRepoVariants.All"/> in a
/// fixed order that is a design choice, not something the CHECK constraint's
/// declaration order constrains).
/// </summary>
public sealed class PhotonRepoVariantsConstraintDriftTests
{
	[Fact]
	public void PhotonRepoVariantsAll_EqualsVariantCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckValues("photon_repo_index_variant_check", "variant");

		Assert.Equal(
			new HashSet<string>(PhotonRepoVariants.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	[Fact]
	public void PhotonArchesAll_EqualsArchCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckValues("photon_repo_index_arch_check", "arch");

		Assert.Equal(
			new HashSet<string>(PhotonArches.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	private static List<string> ParseLatestCheckValues(string constraintName, string columnName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Regex checkPattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
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
