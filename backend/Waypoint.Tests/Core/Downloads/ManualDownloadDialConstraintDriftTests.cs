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
/// Drift guard for the two closed vocabularies migration 0107 introduced (issue
/// #1406) alongside <c>ManualDownloadRetentionDialResolver.Parse</c> (issue #1440),
/// following this repo's convention for every other closed-vocabulary/CHECK pairing
/// (<c>OciBundleStatusesConstraintDriftTests</c>, <c>RunTypesConstraintDriftTests</c>,
/// <c>InventoryItemTypesConstraintDriftTests</c>,
/// <c>ComponentResultStatusConstraintDriftTests</c>): parse the authoritative value
/// set straight out of the embedded migration SQL (no live database) and assert the
/// application-side constant matches exactly, in order. Issue #1686: neither
/// <see cref="ManualDownloadDialOptions"/> nor <see cref="RetainedContentStates"/> had
/// this guard despite <c>Parse</c> throwing on any value outside the three/five
/// constants -- a migration that widens or renames either CHECK without mirroring the
/// C# side previously produced only a hard runtime <see cref="ArgumentException"/> in
/// the retention path, with nothing in CI failing.
///
/// Issue #1660 note: this follows the same "latest declaration wins" resolution the
/// four exemplars above already use (last CHECK match across migration-ordered
/// resources); if #1660 changes that repo-wide convention, this file should follow
/// suit rather than keep its own copy.
/// </summary>
public sealed class ManualDownloadDialConstraintDriftTests
{
	[Fact]
	public void ManualDownloadDialOptionsAll_EqualsDownloadRetentionPoliciesDialCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckValues("download_retention_policies_dial_check", "manual_download_dial_default");

		Assert.Equal(ManualDownloadDialOptions.All, constraintValues);
	}

	[Fact]
	public void RetainedContentStatesAll_EqualsDownloadRetainedContentStateStateCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseCheckValues("download_retained_content_state_state_check", "state");

		Assert.Equal(RetainedContentStates.All, constraintValues);
	}

	/// <summary>
	/// Issue #1686 AC3: <c>ManualDownloadRetentionDialResolver.ToWireValue</c>'s
	/// <c>default</c> arm throws for any <see cref="ManualDownloadDial"/> member it
	/// does not explicitly handle -- iterating every current member here turns a
	/// future member added without updating <c>Parse</c>/<c>ToWireValue</c> into a
	/// failing test the moment it is added, rather than a defect that only surfaces
	/// as a runtime throw the first time that value is actually resolved. The
	/// round-trip (wire value back to the same enum member) also pins
	/// <see cref="ManualDownloadRetentionDialResolver.Parse"/> against the same set.
	/// </summary>
	[Fact]
	public void ManualDownloadDial_EveryEnumMember_RoundTripsThroughToWireValueAndParse()
	{
		foreach (ManualDownloadDial dial in Enum.GetValues<ManualDownloadDial>())
		{
			string wireValue = ManualDownloadRetentionDialResolver.ToWireValue(dial);

			Assert.Contains(wireValue, ManualDownloadDialOptions.All);
			Assert.Equal(dial, ManualDownloadRetentionDialResolver.Parse(wireValue));
		}
	}

	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>) and returns the value list of the LAST
	/// <paramref name="constraintName"/> CHECK constraint declared across them -- i.e.
	/// the constraint the fully-migrated database actually enforces.
	/// </summary>
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
