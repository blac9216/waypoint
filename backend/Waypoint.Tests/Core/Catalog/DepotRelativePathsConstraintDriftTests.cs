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

using System.Text.RegularExpressions;
using Waypoint.Core.Catalog;
using Xunit;

namespace Waypoint.Tests.Core.Catalog;

/// <summary>
/// Issue #1784's root cause was exactly this drift, undetected: the depot-relative
/// identity <see cref="DepotRelativePaths"/> now resolves for a connected pull
/// (<see cref="VendorProductVersionCatalogParser"/>) must stay byte-for-byte the same
/// rule <c>WaypointCatalogIndex.psm1</c>'s <c>Get-CatalogEntryDepotRelativePath</c>
/// resolves for the offline presence sweep -- the two writers converging on one row
/// per artifact depends entirely on <c>$Script:DepotRoot</c>/<c>$Script:ComponentBinariesDir</c>
/// and <see cref="DepotRelativePaths.DepotRoot"/>/<see cref="DepotRelativePaths.ComponentBinariesDir"/>
/// never diverging. Same drift-guard convention as
/// <c>DepotArtifactStatusesConstraintDriftTests</c>: parse the psm1's own module-scoped
/// variable assignments and assert they equal the C# constants.
/// </summary>
public sealed class DepotRelativePathsConstraintDriftTests
{
	private static readonly string CatalogIndexModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointCatalogIndex", "WaypointCatalogIndex.psm1");

	[Fact]
	public void DepotRelativePaths_DepotRootAndComponentBinariesDir_EqualThePsm1sScriptScopedVariables()
	{
		string fullPath = Path.GetFullPath(CatalogIndexModulePath);
		Assert.True(File.Exists(fullPath), $"expected WaypointCatalogIndex.psm1 at '{fullPath}'");
		string psm1 = File.ReadAllText(fullPath);

		Assert.Equal(DepotRelativePaths.DepotRoot, ParseScriptScopedStringVariable(psm1, "DepotRoot"));
		Assert.Equal(DepotRelativePaths.ComponentBinariesDir, ParseScriptScopedStringVariable(psm1, "ComponentBinariesDir"));
	}

	private static string ParseScriptScopedStringVariable(string psm1, string variableName)
	{
		Match match = Regex.Match(psm1, $@"\$Script:{Regex.Escape(variableName)}\s*=\s*'([^']*)'", RegexOptions.Singleline);
		Assert.True(match.Success, $"Could not locate '$Script:{variableName} = ''...''' in WaypointCatalogIndex.psm1.");
		return match.Groups[1].Value;
	}
}
