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
/// Issue #1696 deliverable 4: runs the C# <see cref="VendorProductVersionCatalogParser"/>
/// and the REAL PowerShell <c>Invoke-WaypointCatalogIndex</c> sweep (issue #1503) over
/// two independent materializations of the SAME shared <c>depot-mini</c> catalog
/// document, then compares the depot-relative identity SET each consumer derives --
/// printing both lists and a per-index diff, not just a pass/fail boolean (the PR
/// #1629 round-2 divergence class this test exists to catch).
///
/// The two consumers are not identity-equivalent by construction:
/// <see cref="VendorProductVersionCatalogParser"/> emits a bare catalog
/// <c>fileName</c> as its own <see cref="DepotArtifactUpsert.RelativePath"/> (its own
/// doc comment: "presence-sweep behavior (#1503), out of this slice's scope"), while
/// the PowerShell module resolves every entry to <c>PROD/COMP/&lt;Product&gt;/&lt;fileName&gt;</c>.
/// This test normalizes the C# side using the SAME rule
/// (<see cref="DepotRelativePath"/>) before comparing -- the contract this proves is
/// "given the documented resolution rule, both consumers agree on every catalog
/// entry's identity", not "the two consumers already emit identical strings".
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
			csharpIdentities.Add(DepotRelativePath(upsert));
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

	/// <summary>Depot-relative identity a catalog entry resolves to, per the documented rule <c>PROD/COMP/&lt;Product&gt;/&lt;fileName&gt;</c> (#1027; <c>Get-CatalogEntryDepotRelativePath</c>).</summary>
	private static string DepotRelativePath(DepotArtifactUpsert upsert)
	{
		using JsonDocument metadata = JsonDocument.Parse(upsert.MetadataJson);
		string product = metadata.RootElement.GetProperty("product").GetString()!;
		return $"PROD/COMP/{product}/{upsert.RelativePath}";
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
