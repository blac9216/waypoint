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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #1123 (found by epic #726 live validation round 12): a narrowed esxi/vm
/// vSphere scan's generated selector-scoping <c>--input-file</c> must be written
/// under the object-scoping input key name the RESOLVED FROZEN PROFILE itself
/// declares -- the VCF 9.x SRG baselines declare <c>esx_vmhostName</c>/<c>vm_Name</c>
/// while the 8.0 STIG baselines declare <c>vmhostName</c>/<c>vmName</c>, and
/// <c>Invoke-WaypointScan</c> pre-fix hardcoded the 8.0 names unconditionally, so a
/// narrowed 9.x scan's generated input file carried a key the profile never reads --
/// every control then read an empty/default value and short-circuited with "No ESX
/// hosts found by name or in target vCenter."
///
/// These tests drive the REAL <c>WaypointScan.psm1</c> through the real in-process
/// executor -- deliberately NOT the e2e stub module (<c>WaypointScanStubModule.psm1</c>),
/// whose <c>Invoke-WaypointScan</c> replacement never executes the narrowing-file
/// write, exactly the #749-seeded blind spot issue #917's NSX auth-key tests
/// document for the sibling transport. The sibling repo's own
/// <c>module.common.ps1</c> is replaced by an INVENTED fake script through the
/// function's own <c>-VmwareStigDockerCommonPath</c> parameter: the fake
/// <c>Invoke-ExternalCommand</c> captures the generated selector-scoping file (the
/// LAST <c>--input-file</c>) before the module's <c>finally</c> deletes it, so
/// assertions see the exact bytes InSpec would. All fixture values are invented
/// (AGENTS.md sanitization: <c>example.internal</c> hosts, made-up host/VM names);
/// nothing here derives from a real system.
/// </summary>
public sealed class WaypointVsphereScanNarrowingInputTests : IDisposable
{
	private sealed class DiscardLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	private const string InventedVCenter = "vcenter-01.example.internal";
	private const string InventedEsxiName = "esxi-host-07.example.internal";
	private const string InventedVmName = "invented-app-vm-03";

	private readonly string _fixtureRoot;
	private WaypointRunspacePool _pool = null!;

	public WaypointVsphereScanNarrowingInputTests()
	{
		_fixtureRoot = Path.Combine(Path.GetTempPath(), $"waypoint-vsphere-narrowing-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_fixtureRoot);
	}

	public void Dispose()
	{
		_pool?.Dispose();
		try
		{
			Directory.Delete(_fixtureRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort temp cleanup only.
		}
	}

	private PowerShellExecutor CreateExecutor()
	{
		PowerShellOptions options = new()
		{
			MaxRunspaces = 1,
			DefaultInvocationTimeout = TimeSpan.FromMinutes(2),
			StopGracePeriod = TimeSpan.FromSeconds(2)
		};
		options.ModulePreloadPaths.Add(FindRepoFile(
			"backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointScan/WaypointScan.psm1"));
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, new DiscardLogBuffer(), wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	private static string FindRepoFile(string relativePath)
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, relativePath);
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException($"Could not locate '{relativePath}' by walking up from AppContext.BaseDirectory");
	}

	/// <summary>
	/// Writes the invented fake sibling script into the per-test fixture dir. The
	/// fake's <c>Invoke-ExternalCommand</c> records the full argument string and
	/// captures the LAST generated <c>--input-file</c> (the platform selector-scoping
	/// file, appended last per issue #911) before the real module's <c>finally</c>
	/// deletes it.
	/// </summary>
	private string WriteFakeCommonScript()
	{
		string capture = _fixtureRoot;
		string commonPath = Path.Combine(_fixtureRoot, "fake.module.common.ps1");
		File.WriteAllText(commonPath, $$"""
			function Invoke-ExternalCommand {
				[CmdletBinding()]
				param(
					[Parameter(Mandatory)][string]$Executable,
					[Parameter(Mandatory)][string]$Arguments,
					[int]$TimeoutMilliseconds,
					[string]$ProcessName,
					[int[]]$AllowedExitCodes,
					[string]$Source,
					[switch]$SurfaceOutputOnFailure,
					[hashtable]$EnvironmentVars
				)
				Add-Content -Path '{{capture}}/inspec-args.txt' -Value $Arguments
				$InputFileMatches = [regex]::Matches($Arguments, '--input-file "([^"]+)"')
				if ($InputFileMatches.Count -gt 0) {
					$SelectorFilePath = $InputFileMatches[$InputFileMatches.Count - 1].Groups[1].Value
					Copy-Item -Path $SelectorFilePath -Destination '{{capture}}/captured-selector-file.yml' -Force
				}
				$ReporterMatch = [regex]::Match($Arguments, '--reporter=json:"([^"]+)"')
				if ($ReporterMatch.Success) {
					Set-Content -Path $ReporterMatch.Groups[1].Value -Value '{"profiles":[]}'
				}
				return 0
			}
			""");
		return commonPath;
	}

	/// <summary>
	/// Invented frozen-profile directory whose <c>inspec.yml</c> declares the given
	/// input names, in the indented style (matches most shipped manifests; #1071's
	/// column-0/comment/CRLF shapes are already covered by
	/// <c>WaypointNsxScanAuthInputTests</c> against the SAME shared parsing helper,
	/// so they are not duplicated here).
	/// </summary>
	private string WriteProfileFixture(string dirName, params string[] declaredInputNames)
	{
		string profileDir = Path.Combine(_fixtureRoot, dirName);
		Directory.CreateDirectory(profileDir);
		string inputsBlock = declaredInputNames.Length == 0
			? string.Empty
			: "inputs:\n" + string.Concat(declaredInputNames.Select(name =>
				$"  - name: {name}\n    value: ''\n    description: invented fixture input\n"));
		File.WriteAllText(Path.Combine(profileDir, "inspec.yml"), $"""
			name: invented-vsphere-fixture-profile
			title: Invented vSphere baseline (test fixture)
			version: 1.0.0
			depends:
			  - name: invented-shared-helper
			    path: ../invented-shared
			{inputsBlock}
			""");
		return profileDir;
	}

	private async Task<(System.Management.Automation.PSObject Result, string? SelectorFile)> RunNarrowedScanAsync(
		string profileDir, string selectorKind, string? selectorName)
	{
		string commonPath = WriteFakeCommonScript();
		string reportPath = Path.Combine(_fixtureRoot, "reports", "vsphere-report.json");

		PowerShellExecutor executor = CreateExecutor();
		Dictionary<string, object?> parameters = new()
		{
			["VCenter"] = InventedVCenter,
			["Username"] = "invented-auditor",
			["Password"] = "invented-password",
			["ProfilePath"] = profileDir,
			["ReportPath"] = reportPath,
			["TimeoutSeconds"] = 60,
			["SelectorKind"] = selectorKind,
			["VmwareStigDockerCommonPath"] = commonPath,
		};
		if (selectorName is not null)
		{
			parameters["SelectorName"] = selectorName;
		}

		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-WaypointScan", Parameters: parameters),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);
		System.Management.Automation.PSObject outcome =
			System.Management.Automation.PSObject.AsPSObject(Assert.Single(result.Output)!);

		string capturedPath = Path.Combine(_fixtureRoot, "captured-selector-file.yml");
		string? selectorFile = File.Exists(capturedPath) ? await File.ReadAllTextAsync(capturedPath) : null;
		return (outcome, selectorFile);
	}

	private static bool OutcomeSucceeded(System.Management.Automation.PSObject outcome) =>
		System.Management.Automation.LanguagePrimitives.ConvertTo<bool>(outcome.Properties["Success"].Value);

	private static string[] SelectorFileLines(string? selectorFile)
	{
		Assert.NotNull(selectorFile);
		return selectorFile!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>
	/// The #1123 bug pin (esxi): a frozen profile declaring the VCF 9.x SRG
	/// object-scoping name (<c>esx_vmhostName</c>) must receive the host name under
	/// THAT key -- on the pre-fix module the key is hardcoded to the 8.0 name
	/// (<c>vmhostName</c>), the 9.x profile's <c>esx_vmhostName</c> input stays at its
	/// declared empty default, and a live 9.x scan evaluates zero controls.
	/// </summary>
	[Fact]
	public async Task NinePointXEsxProfile_NarrowingInput_UsesTheProfileDeclaredEsxKey()
	{
		string profileDir = WriteProfileFixture(
			"profile-9x-esx", "esx_vmhostName", "esx_cluster", "esx_allHosts");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "esxi", InventedEsxiName);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = SelectorFileLines(selectorFile);
		Assert.Contains($"esx_vmhostName: '{InventedEsxiName}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("vmhostName:", StringComparison.Ordinal));
	}

	/// <summary>
	/// Regression guard (esxi): an 8.0-era profile (declares the legacy name) keeps
	/// the legacy key byte-for-byte -- per-profile resolution must not disturb the
	/// already-working 8.0 path.
	/// </summary>
	[Fact]
	public async Task EightPointZeroEsxiProfile_NarrowingInput_KeepsTheLegacyEsxiKey()
	{
		string profileDir = WriteProfileFixture(
			"profile-80-esxi", "vmhostName", "cluster", "allesxi");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "esxi", InventedEsxiName);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = SelectorFileLines(selectorFile);
		Assert.Contains($"vmhostName: '{InventedEsxiName}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("esx_vmhostName:", StringComparison.Ordinal));
	}

	/// <summary>
	/// The #1123 bug pin (vm): the VCF 9.x SRG VM baseline declares <c>vm_Name</c>
	/// (NOT an <c>esx_</c>-prefixed name -- the two content generations do not share a
	/// prefix scheme between selector kinds, which is exactly why the fix derives the
	/// key from the profile rather than templating a version-conditional prefix).
	/// </summary>
	[Fact]
	public async Task NinePointXVmProfile_NarrowingInput_UsesTheProfileDeclaredVmKey()
	{
		string profileDir = WriteProfileFixture(
			"profile-9x-vm", "vm_Name", "vm_cluster", "vm_allvms");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "vm", InventedVmName);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = SelectorFileLines(selectorFile);
		Assert.Contains($"vm_Name: '{InventedVmName}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("vmName:", StringComparison.Ordinal));
	}

	/// <summary>Regression guard (vm): an 8.0-era profile keeps the legacy <c>vmName</c> key.</summary>
	[Fact]
	public async Task EightPointZeroVmProfile_NarrowingInput_KeepsTheLegacyVmKey()
	{
		string profileDir = WriteProfileFixture("profile-80-vm", "vmName", "allvms");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "vm", InventedVmName);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = SelectorFileLines(selectorFile);
		Assert.Contains($"vmName: '{InventedVmName}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("vm_Name:", StringComparison.Ordinal));
	}

	/// <summary>
	/// Fail-closed guard (epic #726 §3 "never guess"): a profile declaring NEITHER
	/// known esxi key must never silently emit an unscoped/mis-scoped narrowing file
	/// -- unlike the NSX auth-key helper's per-slot legacy default, the vSphere
	/// selector-key resolution has no safe default to fall back to (a wrong guess
	/// here either scopes to nothing, per #1123, or scopes to the wrong object), so
	/// the scan must fail with a diagnosable reason instead. The InSpec invocation
	/// must never run: no exec occurred, and the generated selector file was never
	/// materialized.
	/// </summary>
	[Fact]
	public async Task ProfileDeclaringNeitherKnownEsxiKey_FailsClosed_NeverGuessesAName()
	{
		string profileDir = WriteProfileFixture("profile-unrecognized-esxi", "invented_unrelated_input");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "esxi", InventedEsxiName);

		Assert.False(OutcomeSucceeded(outcome));
		string? failureReason = outcome.Properties["FailureReason"].Value?.ToString();
		Assert.NotNull(failureReason);
		Assert.Contains("esxi", failureReason, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(profileDir, failureReason, StringComparison.Ordinal);

		Assert.Null(selectorFile);
		Assert.False(File.Exists(Path.Combine(_fixtureRoot, "inspec-args.txt")),
			"Invoke-ExternalCommand (InSpec) must never run when the narrowing key cannot be resolved.");
	}

	/// <summary>
	/// Same fail-closed guard, vm selector: a profile declaring only the esxi-family
	/// keys (never a vm key) must fail closed for a vm-selector narrowing, not fall
	/// back across selector kinds.
	/// </summary>
	[Fact]
	public async Task ProfileDeclaringNeitherKnownVmKey_FailsClosed_NeverGuessesAName()
	{
		string profileDir = WriteProfileFixture("profile-unrecognized-vm", "esx_vmhostName");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "vm", InventedVmName);

		Assert.False(OutcomeSucceeded(outcome));
		string? failureReason = outcome.Properties["FailureReason"].Value?.ToString();
		Assert.NotNull(failureReason);
		Assert.Contains("vm", failureReason, StringComparison.OrdinalIgnoreCase);
		Assert.Null(selectorFile);
	}

	/// <summary>
	/// A vcenter selector carries no object name, so it never needs a resolved
	/// narrowing key -- a profile that declares neither esxi nor vm key must still
	/// succeed for a whole-vCenter narrowed scan (only <c>vsphereSelectorKind</c> is
	/// written).
	/// </summary>
	[Fact]
	public async Task VcenterSelector_NoSelectorName_NeedsNoResolvedKey()
	{
		string profileDir = WriteProfileFixture("profile-vcenter-only");

		(System.Management.Automation.PSObject outcome, string? selectorFile) =
			await RunNarrowedScanAsync(profileDir, "vcenter", selectorName: null);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = SelectorFileLines(selectorFile);
		Assert.Contains("vsphereSelectorKind: 'vcenter'", lines);
	}
}
