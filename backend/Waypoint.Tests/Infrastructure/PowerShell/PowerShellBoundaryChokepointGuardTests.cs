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

using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Class-killing guard for issue #976 (finishing #972/#975's audit): source-scans
/// every production <c>.cs</c> file under <c>backend/Waypoint.Infrastructure.Execution</c>
/// for a direct <see cref="System.Management.Automation.PSObject"/> NoteProperty read
/// (<c>.Properties[...]?.Value as T</c>, <c>.Properties[...]?.Value is T</c>, or a bare
/// <c>.Properties[...]?.Value</c> handed straight to a <c>switch</c>/cast) OUTSIDE
/// <c>PowerShellValueUnwrap.cs</c> itself. A future handler that reintroduces the
/// #972 class -- reading a nested PowerShell property without routing through the one
/// shared chokepoint -- fails this test by file/line, instead of costing another
/// live-lab validation round.
///
/// This is a source-scan test (parsing <c>.cs</c> files as text), not a reflection- or
/// IL-based check, because the defect class is a SYNTACTIC pattern at the call site
/// (which unwrap helper, if any, wraps the read) -- there is no runtime signal to
/// reflect over. <see cref="Waypoint.Tests.Api.EndpointRoleMatrixTests"/> and
/// <see cref="Waypoint.Tests.Core.ComplianceContent.SemanticImport.LayoutTableParityTests"/>
/// are this repo's two closest guard idioms (reflection-discovery and doc/text-parsing,
/// respectively); text-scanning production source for a banned call shape is the
/// direct extension of the latter.
/// </summary>
public sealed class PowerShellBoundaryChokepointGuardTests
{
	[Fact]
	public void NoDirectPSObjectPropertyValueRead_OutsideTheChokepoint()
	{
		string executionProjectDir = FindDirectoryUpward("backend/Waypoint.Infrastructure.Execution");
		List<string> offenders = [];

		foreach (string path in Directory.EnumerateFiles(executionProjectDir, "*.cs", SearchOption.AllDirectories))
		{
			string fileName = Path.GetFileName(path);
			if (fileName == "PowerShellValueUnwrap.cs")
			{
				// The chokepoint itself is the one place allowed to touch .Properties[...].Value directly.
				continue;
			}

			string[] lines = File.ReadAllLines(path);
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i];
				if (!line.Contains(".Properties[", StringComparison.Ordinal) || !line.Contains(".Value", StringComparison.Ordinal))
				{
					continue;
				}

				// Routed through the chokepoint on this same line -- allowed regardless of
				// exact shape (Unwrap/UnwrapAs/UnwrapAsStruct/UnwrapEach all wrap the read).
				if (line.Contains("PowerShellValueUnwrap.", StringComparison.Ordinal))
				{
					continue;
				}

				offenders.Add($"{Path.GetRelativePath(executionProjectDir, path)}:{i + 1}: {line.Trim()}");
			}
		}

		Assert.True(
			offenders.Count == 0,
			"Found direct PSObject.Properties[...].Value read(s) outside PowerShellValueUnwrap " +
			"(issue #972/#976's chokepoint). Route these through PowerShellValueUnwrap.Unwrap/" +
			"UnwrapAs<T>/UnwrapAsStruct<T>/UnwrapEach instead:\n" + string.Join('\n', offenders));
	}

	private static string FindDirectoryUpward(string repoRelativePath)
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(dir.FullName, repoRelativePath);
			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			dir = dir.Parent;
		}

		throw new DirectoryNotFoundException($"Could not locate {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
	}
}
