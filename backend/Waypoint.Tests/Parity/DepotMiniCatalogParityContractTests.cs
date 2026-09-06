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
using Waypoint.Core.Catalog;
using Waypoint.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Issue #1696 deliverable 4 (strengthened by issue #1784's fix): runs the C#
/// <see cref="VendorProductVersionCatalogParser"/> and the REAL PowerShell
/// <c>Invoke-WaypointCatalogIndex</c> sweep (issue #1503) over two independent
/// materializations of the SAME shared <c>depot-mini</c> catalog document, then
/// compares the depot-relative identity SET each consumer derives -- printing both
/// lists and a per-index diff, not just a pass/fail boolean (the PR #1629 round-2
/// divergence class this test exists to catch).
///
/// Before #1784, the two consumers were not identity-equivalent by construction:
/// <see cref="VendorProductVersionCatalogParser"/> emitted a bare catalog
/// <c>fileName</c> as its own <see cref="DepotArtifactUpsert.RelativePath"/>, while the
/// PowerShell module resolved every entry to <c>PROD/COMP/&lt;Product&gt;/&lt;fileName&gt;</c>
/// -- this test used to normalize the C# side onto the PowerShell rule before
/// comparing, proving only "given the documented resolution rule, both consumers
/// agree", not "the two consumers already emit identical strings" (exactly the gap
/// live validation caught as issue #1784: a stack that both pulled and swept wrote two
/// rows per artifact). #1784 made <see cref="VendorProductVersionCatalogParser"/>
/// itself resolve <see cref="Waypoint.Core.Catalog.DepotRelativePaths.Resolve"/> as its
/// identity, so this test now compares the RAW <see cref="DepotArtifactUpsert.RelativePath"/>
/// values with no normalization step at all -- the stronger, actually-load-bearing
/// claim.
/// </summary>
public sealed class DepotMiniCatalogParityContractTests
{
	private readonly ITestOutputHelper _output;
	private static readonly string RepoRoot = ResolveRepoRoot();

	public DepotMiniCatalogParityContractTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public void CSharpParser_And_PowerShellSweep_AgreeOnDepotRelativeCatalogIdentities()
	{
		using DepotMiniFixture fixture = new();

		SortedSet<string> csharpIdentities = new(StringComparer.OrdinalIgnoreCase);
		foreach (DepotArtifactUpsert upsert in VendorProductVersionCatalogParser.Parse(fixture.CatalogJson))
		{
			// Issue #1784: no normalization -- RelativePath IS the depot-relative
			// identity now, straight from the parser, the same string the sweep must
			// independently arrive at for the two writers to converge on one row.
			csharpIdentities.Add(upsert.RelativePath);
		}

		List<PowerShellSweepRecord> psRecords = RunPowerShellSweep();
		SortedSet<string> psCatalogIdentities = new(
			psRecords
				.Where(r => r.RecordType == "ArtifactPresence" && r.RelativePath != "PROD/metadata/upgrade_info.xml")
				.Select(r => r.RelativePath),
			StringComparer.OrdinalIgnoreCase);

		List<string> onlyInCSharp = [.. csharpIdentities.Except(psCatalogIdentities, StringComparer.OrdinalIgnoreCase)];
		List<string> onlyInPowerShell = [.. psCatalogIdentities.Except(csharpIdentities, StringComparer.OrdinalIgnoreCase)];

		_output.WriteLine($"C# parser identities ({csharpIdentities.Count}):");
		foreach (string identity in csharpIdentities)
		{
			_output.WriteLine($"  {identity}");
		}

		_output.WriteLine($"PowerShell sweep catalog identities ({psCatalogIdentities.Count}):");
		foreach (string identity in psCatalogIdentities)
		{
			_output.WriteLine($"  {identity}");
		}

		_output.WriteLine($"Only in C#: {(onlyInCSharp.Count == 0 ? "(none)" : string.Join(", ", onlyInCSharp))}");
		_output.WriteLine($"Only in PowerShell: {(onlyInPowerShell.Count == 0 ? "(none)" : string.Join(", ", onlyInPowerShell))}");

		Assert.Empty(onlyInCSharp);
		Assert.Empty(onlyInPowerShell);
		Assert.Equal(20, csharpIdentities.Count); // VCENTER 6 + NSX 1 + ESXI 1 + TKG 12 (depot-mini/README.md's catalog; NSX's two same-fileName bundles dedup to 1).
	}


	private static List<PowerShellSweepRecord> RunPowerShellSweep()
	{
		string runnerScript = Path.Combine(RepoRoot, "backend", "Waypoint.Tests", "Assets", "DepotMiniParityRunner", "Invoke-DepotMiniParitySweep.ps1");
		Assert.True(File.Exists(runnerScript), $"expected the parity runner script at '{runnerScript}'");

		string outputPath = Path.Combine(Path.GetTempPath(), $"wp-depot-mini-parity-{Guid.NewGuid():N}.json");
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

			Assert.True(process.HasExited, "PowerShell parity runner did not exit within 120s");
			Assert.True(process.ExitCode == 0, $"PowerShell parity runner failed (exit {process.ExitCode}):\n{stderr}");

			string json = File.ReadAllText(outputPath);
			return JsonSerializer.Deserialize<List<PowerShellSweepRecord>>(json)!;
		}
		finally
		{
			File.Delete(outputPath);
		}
	}

	private static string ResolveRepoRoot() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

	private sealed record PowerShellSweepRecord(string RecordType, string RelativePath, string? Status);
}
