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
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>) and returns the value list of the LAST
	/// <c>&lt;column&gt; IN (...)</c> CHECK constraint on <c>vmtools_artifact_index</c>
	/// declared across them -- i.e. the constraint the fully-migrated database
	/// actually enforces.
	/// </summary>
	private static List<string> ParseCheckConstraintValues(string column)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

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
