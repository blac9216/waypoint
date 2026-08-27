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
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Class-killing convention guard for issue #984's defect class: "PowerShell job/async
/// primitives that assume a full <c>pwsh</c> host." <c>Start-Job</c>/<c>Wait-Job</c>
/// (issue #984) and <c>Register-ObjectEvent</c>'s event-driven callbacks (found and
/// avoided during #984's own fix -- its callback only fires when PowerShell's engine
/// event queue is pumped, which a synchronous runner-hosted function never does) both
/// depend on subsystems the compliance-runner's embedded SMA runspace
/// (<c>WaypointRunspacePool</c>, <c>InitialSessionState.CreateDefault2()</c>,
/// ADR-0013/0014) never wires up -- silent, not loud: the call does not throw, it just
/// never completes or never observes output, so the runner-executed module fails closed
/// on every invocation without ever raising a PowerShell error.
///
/// This test enumerates every <c>.psm1</c>/<c>.ps1</c> file the runners actually load
/// (<c>Waypoint.Infrastructure.Execution/PowerShell/Modules/**</c> -- the tree
/// <c>PowerShellOptions.ModulePreloadPaths</c> and the readiness check point at) and
/// asserts NONE of them contain a disallowed cmdlet name. A future module hitting this
/// class fails here, at build time, instead of live in the runner (the way #984 was
/// found: 0 promoted profiles, discovered days into epic #726 live-lab validation).
///
/// Pure source-text scan -- no PowerShell host, no Postgres container -- stays fast and
/// runs everywhere the source does, following the same "parse the authoritative
/// artifact" idiom as <c>CatalogNaturalKeyWriteGuardTests</c>.
/// </summary>
public sealed class RunnerModulePowerShellJobPrimitiveGuardTests
{
	/// <summary>
	/// Disallowed cmdlets, each with why it belongs in this class. A module needing
	/// genuine background/async work in-runspace should reach for
	/// <c>System.Diagnostics.Process</c> + <c>WaitForExit(timeoutMs)</c> (host-agnostic,
	/// issue #984's own fix) or a plain .NET <c>Task</c>, never PowerShell's own
	/// job/eventing subsystems.
	/// </summary>
	private static readonly Dictionary<string, string> DisallowedCmdlets = new(StringComparer.OrdinalIgnoreCase)
	{
		["Start-Job"] = "issue #984: depends on PowerShell's background-job subsystem, never wired up in the embedded SMA runspace -- Wait-Job never observes completion, every call fail-closes on timeout.",
		["Wait-Job"] = "issue #984: see Start-Job -- the two are always used together and share the same defect.",
		["Receive-Job"] = "issue #984: only meaningful paired with Start-Job, which is itself disallowed here.",
		["Stop-Job"] = "issue #984: only meaningful paired with Start-Job, which is itself disallowed here.",
		["Remove-Job"] = "issue #984: only meaningful paired with Start-Job, which is itself disallowed here.",
		["Register-ObjectEvent"] = "issue #984 fix round: the callback only runs when PowerShell's own engine event queue is pumped (Wait-Event/Get-Event/a message loop) -- a synchronous runner-hosted function never does that, so captured output silently comes back empty with no error.",
		["Register-EngineEvent"] = "issue #984 fix round: same engine-event-queue dependency as Register-ObjectEvent.",
		["Wait-Event"] = "issue #984 class: PowerShell eventing subsystem, not proven to complete in the embedded SMA runspace; a runner module needing to wait on external work should poll or use .NET Task/Process APIs directly.",
	};

	[Fact]
	public void RunnerLoadedModules_ContainNoDisallowedPowerShellJobOrEventingPrimitives()
	{
		string modulesRoot = FindRunnerModulesRoot();
		string[] scriptFiles = Directory.GetFiles(modulesRoot, "*.psm1", SearchOption.AllDirectories)
			.Concat(Directory.GetFiles(modulesRoot, "*.ps1", SearchOption.AllDirectories))
			.ToArray();

		Assert.NotEmpty(scriptFiles);

		List<string> failures = [];
		foreach (string file in scriptFiles)
		{
			string source = File.ReadAllText(file);
			// Strip '#'-prefixed comment lines before matching -- this guard is about
			// live code paths, not prose that documents the defect class (this file
			// itself, and WaypointComplianceContent.psm1's own issue #984 explanation,
			// both mention the disallowed names by name for future readers).
			string codeOnly = string.Join('\n', source.Split('\n').Select(StripComment));

			foreach ((string cmdlet, string reason) in DisallowedCmdlets)
			{
				if (Regex.IsMatch(codeOnly, $@"\b{Regex.Escape(cmdlet)}\b"))
				{
					failures.Add($"{Path.GetFileName(file)}: uses disallowed cmdlet '{cmdlet}' -- {reason}");
				}
			}
		}

		Assert.True(failures.Count == 0, "Runner PowerShell job/eventing primitive guard failures:\n" + string.Join("\n", failures));
	}

	private static string StripComment(string line)
	{
		int hashIndex = line.IndexOf('#');
		return hashIndex >= 0 ? line[..hashIndex] : line;
	}

	/// <summary>
	/// Walks up from the test binary's output directory to the repo root (same
	/// technique as <c>CatalogNaturalKeyWriteGuardTests</c>/<c>RunnerEgressTopologyTests</c>)
	/// and locates the runner-loaded module tree by its known repo-relative path.
	/// </summary>
	private static string FindRunnerModulesRoot()
	{
		const string repoRelativePath = "backend/Waypoint.Infrastructure.Execution/PowerShell/Modules";

		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(dir.FullName, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			dir = dir.Parent;
		}

		throw new DirectoryNotFoundException(
			$"Could not locate {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
	}
}
