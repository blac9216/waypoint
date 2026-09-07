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
/// Drift guard for <see cref="PhotonImageChannels.All"/>/<see cref="PhotonImageKinds.All"/>
/// against migration 0130's <c>photon_image_index_channel_check</c>/
/// <c>photon_image_index_kind_check</c>, mirroring
/// <c>PhotonRepoVariantsConstraintDriftTests</c>'s own convention.
/// </summary>
public sealed class PhotonImageIndexConstantsConstraintDriftTests
{
	[Fact]
	public void PhotonImageChannelsAll_EqualsChannelCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckValues("photon_image_index_channel_check", "channel");

		Assert.Equal(
			new HashSet<string>(PhotonImageChannels.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	[Fact]
	public void PhotonImageKindsAll_EqualsKindCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckValues("photon_image_index_kind_check", "image_kind");

		Assert.Equal(
			new HashSet<string>(PhotonImageKinds.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	private static List<string> ParseLatestCheckValues(string constraintName, string columnName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Regex pattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\(([^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);

		List<string>? latestValues = null;
		foreach (string resourceName in resourceNames)
		{
			using Stream? stream = assembly.GetManifestResourceStream(resourceName);
			if (stream is null)
			{
				continue;
			}

			using StreamReader reader = new(stream);
			string sql = reader.ReadToEnd();

			Match match = pattern.Match(sql);
			if (match.Success)
			{
				latestValues = [.. Regex.Matches(match.Groups[1].Value, "'([^']*)'")
					.Select(valueMatch => valueMatch.Groups[1].Value)];
			}
		}

		Assert.NotNull(latestValues);
		return latestValues!;
	}
}
