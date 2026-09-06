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

using System.Diagnostics;
using System.Text.Json;
using Waypoint.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Waypoint.Tests.Parity;

/// <summary>
/// PR #1742 review round-1 note 1: <see cref="DepotMiniFixture"/> (C#) and
/// <c>New-DepotMiniFixture.ps1</c> (PowerShell) must materialize the SAME set of
/// depot-relative files from the shared checked-in <c>Fixtures/depot-mini/</c> tree --
/// <see cref="DepotMiniFixture"/>'s own doc comment claims both loaders stage the same
/// file list, and this test is what makes that claim checkable instead
/// of asserted-then-ignored. Compares the depot-relative file list (not content) from
/// each loader's own independent materialization; a future loader-only artefact
/// (a new script, a new README) that only one side excludes fails this test instead
/// of silently reaching whichever consumer is the first to enumerate unknown files.
/// </summary>
public sealed class DepotMiniLoaderParityTests
{
	private readonly ITestOutputHelper _output;
	private static readonly string RepoRoot = ResolveRepoRoot();

	public DepotMiniLoaderParityTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void CSharpLoader_And_PowerShellLoader_MaterializeTheSameFileList()
	{
		using DepotMiniFixture fixture = new();
		SortedSet<string> csharpFiles = new(StringComparer.OrdinalIgnoreCase);
		foreach (string file in Directory.GetFiles(fixture.RootPath, "*", SearchOption.AllDirectories))
		{
			csharpFiles.Add(Path.GetRelativePath(fixture.RootPath, file).Replace(Path.DirectorySeparatorChar, '/'));
		}

		SortedSet<string> powerShellFiles = new(RunPowerShellFileList(), StringComparer.OrdinalIgnoreCase);

		List<string> onlyInCSharp = [.. csharpFiles.Except(powerShellFiles, StringComparer.OrdinalIgnoreCase)];
		List<string> onlyInPowerShell = [.. powerShellFiles.Except(csharpFiles, StringComparer.OrdinalIgnoreCase)];

		_output.WriteLine($"C# loader files ({csharpFiles.Count}):");
		foreach (string file in csharpFiles)
		{
			_output.WriteLine($"  {file}");
		}

		_output.WriteLine($"PowerShell loader files ({powerShellFiles.Count}):");
		foreach (string file in powerShellFiles)
		{
			_output.WriteLine($"  {file}");
		}

		_output.WriteLine($"Only in C#: {(onlyInCSharp.Count == 0 ? "(none)" : string.Join(", ", onlyInCSharp))}");
		_output.WriteLine($"Only in PowerShell: {(onlyInPowerShell.Count == 0 ? "(none)" : string.Join(", ", onlyInPowerShell))}");

		Assert.Empty(onlyInCSharp);
		Assert.Empty(onlyInPowerShell);
	}

	private static List<string> RunPowerShellFileList()
	{
		string runnerScript = Path.Combine(RepoRoot, "backend", "Waypoint.Tests", "Assets", "DepotMiniParityRunner", "Get-DepotMiniMaterializedFileList.ps1");
		Assert.True(File.Exists(runnerScript), $"expected the loader-parity runner script at '{runnerScript}'");

		string outputPath = Path.Combine(Path.GetTempPath(), $"wp-depot-mini-loader-parity-{Guid.NewGuid():N}.json");
		try
		{
			ProcessStartInfo startInfo = new("pwsh")
			{
				ArgumentList = { "-NoProfile", "-File", runnerScript, "-RepoRoot", RepoRoot, "-OutputPath", outputPath },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			using Process process = Process.Start(startInfo)!;
			string stderr = process.StandardError.ReadToEnd();
			process.WaitForExit(120_000);

			Assert.True(process.HasExited, "PowerShell loader-parity runner did not exit within 120s");
			Assert.True(process.ExitCode == 0, $"PowerShell loader-parity runner failed (exit {process.ExitCode}):\n{stderr}");

			string json = File.ReadAllText(outputPath);
			return JsonSerializer.Deserialize<List<string>>(json)!;
		}
		finally
		{
			File.Delete(outputPath);
		}
	}

	private static string ResolveRepoRoot() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
