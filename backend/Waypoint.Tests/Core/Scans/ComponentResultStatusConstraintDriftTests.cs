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
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Drift guard for migration 0063's three closed-vocabulary CHECK constraints, same
/// mechanism as <c>RunTypesConstraintDriftTests</c>: parse the authoritative value set
/// straight out of the embedded migration SQL (no live database) and assert the
/// application-side constant matches exactly, in order.
/// </summary>
public sealed class ComponentResultStatusConstraintDriftTests
{
	[Fact]
	public void ComponentResultStatuses_All_EqualsComponentResultsStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckValues("component_results_status_check", "status");
		Assert.Equal(ComponentResultStatuses.All, constraintValues);
	}

	[Fact]
	public void ComponentFindingStatuses_All_EqualsComponentResultFindingsStatusCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckValues("component_result_findings_status_check", "status");
		Assert.Equal(ComponentFindingStatuses.All, constraintValues);
	}

	[Fact]
	public void ComponentFindingSeverities_All_EqualsComponentResultFindingsSeverityCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckValues("component_result_findings_severity_check", "severity");
		Assert.Equal(ComponentFindingSeverities.All, constraintValues);
	}

	[Fact]
	public void ComponentResultArtifactKinds_All_EqualsComponentResultArtifactsKindCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckValues("component_result_artifacts_kind_check", "kind");
		Assert.Equal(ComponentResultArtifactKinds.All, constraintValues);
	}

	private static List<string> ParseCheckValues(string constraintName, string columnName)
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
