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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace Waypoint.Tests.Deploy;

/// <summary>
/// Issue #613 (live-verified): <c>WaypointComplianceContent</c> shipped in the
/// compliance-runner image but was never added to that service's
/// <c>PowerShell__ModulePreloadPaths__*</c> in <c>deploy/docker-compose.yml</c>, so
/// <see cref="Waypoint.Infrastructure.PowerShell.WaypointRunspacePool.CreateRunspace"/>
/// never imported it and <c>Invoke-WaypointComplianceContentPull</c> was undefined at
/// invocation time -- content-pull/content-import always failed with "term ... is not
/// recognized", and nothing caught the gap before live end-to-end validation.
///
/// This is the systemic guard issue #613's "Possible Fixes" Option B asks for: every
/// module a registered job handler invokes a command from, for a given runner, must
/// appear in that runner's own <c>ModulePreloadPaths</c> entries in the committed
/// compose file. A future handler whose module ships in the image but is never added
/// to its runner's preload list now fails this test instead of only surfacing at
/// first live invocation.
///
/// Parses <c>deploy/docker-compose.yml</c> directly with YamlDotNet (same technique as
/// <see cref="RunnerEgressTopologyTests"/>) -- no Docker daemon required.
/// </summary>
public sealed class ModulePreloadCompletenessTests
{
	// The full set of Waypoint-owned shim modules baked into either runner image
	// (Waypoint.Infrastructure.Execution.csproj's CopyToPublishDirectory), each
	// paired with the runner service that must preload it. WaypointLogging is
	// deliberately excluded: it is a support module (Get-LogSplat/Write-Log) other
	// shims dot-source, not something a job handler invokes by command name -- see
	// deploy/docker-compose.yml's own preload-order comment on compliance-runner.
	private static readonly (string Module, string Runner)[] ExpectedModuleRunnerPairs =
	[
		("WaypointDiscovery", "compliance-runner"),
		("WaypointScan", "compliance-runner"),
		("WaypointCredentialTest", "compliance-runner"),
		("WaypointComplianceContent", "compliance-runner"),
		("WaypointCatalogIndex", "download-runner"),
		("WaypointDownload", "download-runner"),
	];

	private static readonly YamlMappingNode Compose = LoadCompose();
	private static readonly YamlMappingNode Services = Child(Compose, "services");

	public static IEnumerable<object[]> ModuleRunnerPairs()
		=> ExpectedModuleRunnerPairs.Select(pair => new object[] { pair.Module, pair.Runner });

	[Theory]
	[MemberData(nameof(ModuleRunnerPairs))]
	public void Every_handler_invoked_module_is_in_its_runners_preload_paths(string module, string runner)
	{
		List<string> preloadPaths = ModulePreloadPaths(runner);
		Assert.Contains(
			preloadPaths,
			path => path.EndsWith("/" + module, StringComparison.Ordinal));
	}

	/// <summary>Every module baked into the image (per the shared source tree) is
	/// accounted for above, on one runner or the other -- catches a module added to
	/// disk that this test's own pairing table forgot to list, not just the reverse.</summary>
	[Fact]
	public void Every_shipped_module_directory_is_covered_by_the_pairing_table()
	{
		string[] shippedModules = ShippedModuleDirectories();
		string[] coveredOrSupport = ExpectedModuleRunnerPairs.Select(p => p.Module)
			.Append("WaypointLogging")
			.ToArray();

		Assert.All(shippedModules, module => Assert.Contains(module, coveredOrSupport));
	}

	private static string[] ShippedModuleDirectories()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(
				dir.FullName, "backend", "Waypoint.Infrastructure.Execution", "PowerShell", "Modules");
			if (Directory.Exists(candidate))
			{
				return Directory.GetDirectories(candidate).Select(Path.GetFileName).ToArray()!;
			}

			dir = dir.Parent;
		}

		throw new DirectoryNotFoundException(
			"Could not locate backend/Waypoint.Infrastructure.Execution/PowerShell/Modules by walking up from "
			+ AppContext.BaseDirectory);
	}

	private static List<string> ModulePreloadPaths(string service)
	{
		YamlMappingNode svc = (YamlMappingNode)Services.Children[new YamlScalarNode(service)];
		YamlMappingNode env = Child(svc, "environment");

		return env.Children
			.Where(kv => ((YamlScalarNode)kv.Key).Value!.StartsWith("PowerShell__ModulePreloadPaths__", StringComparison.Ordinal))
			.Select(kv => ((YamlScalarNode)kv.Value).Value!)
			.ToList();
	}

	private static YamlMappingNode Child(YamlMappingNode parent, string key)
	{
		return (YamlMappingNode)parent.Children[new YamlScalarNode(key)];
	}

	private static YamlMappingNode LoadCompose()
	{
		string path = ResolveComposePath();
		using var reader = new StreamReader(path);
		var yaml = new YamlStream();
		yaml.Load(reader);
		return (YamlMappingNode)yaml.Documents[0].RootNode;
	}

	// Walk up from the test assembly's location until deploy/docker-compose.yml
	// is found. Robust to the bin/<config>/<tfm>/ build layout locally and in
	// CI alike, and independent of the process working directory.
	private static string ResolveComposePath()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(dir.FullName, "deploy", "docker-compose.yml");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			dir = dir.Parent;
		}

		throw new FileNotFoundException(
			"Could not locate deploy/docker-compose.yml by walking up from "
			+ AppContext.BaseDirectory);
	}
}
