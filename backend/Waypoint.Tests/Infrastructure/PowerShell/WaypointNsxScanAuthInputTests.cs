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
/// Issue #917: <c>Invoke-WaypointNsxScan</c> must emit its generated session
/// auth-block inputs file under the key names the RESOLVED FROZEN PROFILE declares in
/// its own <c>inspec.yml</c> -- the NSX 4.x STIG profiles read
/// <c>nsxManager</c>/<c>sessionToken</c>/<c>sessionCookieId</c> while the VCF 9.x NSX
/// SRG profiles read <c>nsx_managerAddress</c>/<c>nsx_sessionToken</c>/
/// <c>nsx_sessionCookieId</c> -- and must probe TCP 443 reachability before the
/// session-token call (the sibling nsxapi transport's own probe, consistent with the
/// ssh-side <c>Test-TargetReachable</c> adoption).
///
/// These tests drive the REAL <c>WaypointScan.psm1</c> through the real in-process
/// executor -- deliberately NOT the e2e stub module, whose Invoke-WaypointNsxScan
/// replacement never executes the auth-block write (the exact blind spot that let the
/// hardcoded 4.x key names survive PR #916's e2e coverage, per the issue's #749-seeded
/// lesson). The sibling-repository seams (<c>module.common.ps1</c> /
/// <c>module.transport.nsxapi.ps1</c>) are replaced by INVENTED fake scripts through
/// the function's own path parameters: the fake Invoke-ExternalCommand copies the
/// generated auth-block file (the LAST <c>--input-file</c>) before the module's
/// <c>finally</c> deletes it, so the assertions see the exact bytes InSpec would.
/// All fixture values are invented (AGENTS.md sanitization: <c>example.internal</c>
/// hosts, made-up tokens); nothing here derives from a real system.
/// </summary>
public sealed class WaypointNsxScanAuthInputTests : IDisposable
{
	private sealed class DiscardLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	private const string InventedManager = "nsx-mgr-01.example.internal";
	private const string InventedToken = "invented-session-token";
	private const string InventedCookie = "JSESSIONID=invented-cookie-id";

	private readonly string _fixtureRoot;
	private WaypointRunspacePool _pool = null!;

	public WaypointNsxScanAuthInputTests()
	{
		_fixtureRoot = Path.Combine(Path.GetTempPath(), $"waypoint-nsx-auth-{Guid.NewGuid():N}");
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
	/// Writes the invented fake sibling scripts into the per-test fixture dir. The
	/// fakes record probe/token calls and capture the generated auth-block file so a
	/// test can assert on the exact key names written -- the real module deletes that
	/// file in its <c>finally</c>, so capture must happen inside the fake
	/// Invoke-ExternalCommand while the file still exists.
	/// </summary>
	private (string CommonPath, string NsxApiPath) WriteFakeSiblingScripts()
	{
		string capture = _fixtureRoot;
		string commonPath = Path.Combine(_fixtureRoot, "fake.module.common.ps1");
		File.WriteAllText(commonPath, $$"""
			function Test-TargetReachable {
				[CmdletBinding()]
				param(
					[Parameter(Mandatory)][string]$TargetHost,
					[Parameter(Mandatory)][int]$Port,
					[int]$TimeoutSeconds = 5,
					[string]$Source = 'Precheck'
				)
				Add-Content -Path '{{capture}}/probe-calls.txt' -Value "$TargetHost|$Port|$Source"
				return -not (Test-Path -Path '{{capture}}/unreachable.flag')
			}

			function Invoke-ExternalCommand {
				[CmdletBinding()]
				param(
					[Parameter(Mandatory)][string]$Executable,
					[Parameter(Mandatory)][string]$Arguments,
					[int]$TimeoutMilliseconds,
					[string]$ProcessName,
					[int[]]$AllowedExitCodes,
					[string]$Source,
					[switch]$SurfaceOutputOnFailure
				)
				Add-Content -Path '{{capture}}/inspec-args.txt' -Value $Arguments
				$InputFileMatches = [regex]::Matches($Arguments, '--input-file "([^"]+)"')
				if ($InputFileMatches.Count -gt 0) {
					$AuthBlockPath = $InputFileMatches[$InputFileMatches.Count - 1].Groups[1].Value
					Copy-Item -Path $AuthBlockPath -Destination '{{capture}}/captured-auth-block.yml' -Force
				}
				$ReporterMatch = [regex]::Match($Arguments, '--reporter=json:"([^"]+)"')
				if ($ReporterMatch.Success) {
					Set-Content -Path $ReporterMatch.Groups[1].Value -Value '{"profiles":[]}'
				}
				return 0
			}
			""");

		string nsxApiPath = Path.Combine(_fixtureRoot, "fake.module.transport.nsxapi.ps1");
		File.WriteAllText(nsxApiPath, $$"""
			function Get-NsxSessionToken {
				[CmdletBinding()]
				param(
					[Parameter(Mandatory)][string]$Manager,
					[Parameter(Mandatory)][pscredential]$Credential,
					[string]$Source = 'nsx'
				)
				Add-Content -Path '{{capture}}/token-calls.txt' -Value $Manager
				return [pscustomobject]@{ Token = '{{InventedToken}}'; Cookie = '{{InventedCookie}}' }
			}
			""");

		return (commonPath, nsxApiPath);
	}

	/// <summary>
	/// Invented frozen-profile directory whose <c>inspec.yml</c> declares the given
	/// input names. The <c>depends:</c> block carries its own <c>- name:</c> entry to
	/// prove input-name discovery is scoped to the top-level <c>inputs:</c> block, not
	/// any list-of-named-things in the manifest.
	/// </summary>
	private string WriteProfileFixture(string dirName, params string[] declaredInputNames)
	{
		string profileDir = Path.Combine(_fixtureRoot, dirName);
		Directory.CreateDirectory(profileDir);
		string inputsBlock = declaredInputNames.Length == 0
			? string.Empty
			: "inputs:\n" + string.Concat(declaredInputNames.Select(name =>
				$"  - name: {name}\n    description: invented fixture input\n    sensitive: true\n"));
		File.WriteAllText(Path.Combine(profileDir, "inspec.yml"), $"""
			name: invented-nsx-fixture-profile
			title: Invented NSX baseline (test fixture)
			version: 1.0.0
			depends:
			  - name: invented-shared-helper
			    path: ../invented-shared
			{inputsBlock}
			""");
		return profileDir;
	}

	/// <summary>
	/// Issue #1071: writes an <c>inspec.yml</c> whose body is given verbatim rather
	/// than templated, so a test can exercise a specific manifest SHAPE (column-0
	/// sequence entries, a column-0 comment inside the <c>inputs:</c> block, a
	/// trailing comment on a <c>- name:</c> line, or a nested <c>name:</c> key under
	/// an input's <c>value:</c> mapping) instead of the indented style
	/// <see cref="WriteProfileFixture"/> always emits. All content is invented.
	/// </summary>
	private string WriteProfileFixtureRaw(string dirName, string inspecYmlContent)
	{
		string profileDir = Path.Combine(_fixtureRoot, dirName);
		Directory.CreateDirectory(profileDir);
		File.WriteAllText(Path.Combine(profileDir, "inspec.yml"), inspecYmlContent);
		return profileDir;
	}

	/// <summary>
	/// Issue #1071 (round-1 review, re #1077): the raw-verbatim writer above inherits
	/// this source file's LF line endings, so nothing pins the CRLF case. This variant
	/// rewrites the same invented body with CRLF endings before writing it verbatim --
	/// vendor manifests are not guaranteed LF, and a helper that scoped by a regex
	/// leaving a stray CR on the captured name would silently miss every slot.
	/// </summary>
	private string WriteProfileFixtureRawCrlf(string dirName, string inspecYmlContent)
	{
		string crlf = inspecYmlContent.Replace("\r\n", "\n").Replace("\n", "\r\n");
		return WriteProfileFixtureRaw(dirName, crlf);
	}

	private async Task<(System.Management.Automation.PSObject Result, string? AuthBlock)> RunNsxScanAsync(string profileDir)
	{
		(string commonPath, string nsxApiPath) = WriteFakeSiblingScripts();
		string reportPath = Path.Combine(_fixtureRoot, "reports", "nsx-report.json");

		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-WaypointNsxScan", Parameters: new Dictionary<string, object?>
			{
				["Manager"] = InventedManager,
				["Username"] = "invented-auditor",
				["Password"] = "invented-password",
				["ProfilePath"] = profileDir,
				["ReportPath"] = reportPath,
				["TimeoutSeconds"] = 60,
				["VmwareStigDockerCommonPath"] = commonPath,
				["VmwareStigDockerNsxApiPath"] = nsxApiPath,
			}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);
		System.Management.Automation.PSObject outcome =
			System.Management.Automation.PSObject.AsPSObject(Assert.Single(result.Output)!);

		string capturedPath = Path.Combine(_fixtureRoot, "captured-auth-block.yml");
		string? authBlock = File.Exists(capturedPath) ? await File.ReadAllTextAsync(capturedPath) : null;
		return (outcome, authBlock);
	}

	private static bool OutcomeSucceeded(System.Management.Automation.PSObject outcome) =>
		System.Management.Automation.LanguagePrimitives.ConvertTo<bool>(outcome.Properties["Success"].Value);

	private static string[] AuthBlockLines(string? authBlock)
	{
		Assert.NotNull(authBlock);
		return authBlock!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>
	/// The #917 bug pin: a frozen profile declaring the VCF 9.x NSX SRG auth-input
	/// names must receive its session under THOSE names -- on the pre-fix module the
	/// auth block is hardcoded to the 4.x names, the 9.x profile's http() resource
	/// reads none of them, and a live 9.x scan authenticates with no session.
	/// </summary>
	[Fact]
	public async Task NinePointXProfile_AuthBlock_UsesTheProfileDeclaredNsxKeys()
	{
		string profileDir = WriteProfileFixture(
			"profile-9x", "nsx_managerAddress", "nsx_sessionToken", "nsx_sessionCookieId", "invented_syslog_servers");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);

		// No 4.x legacy key may appear alongside -- exactly one occurrence of each
		// auth concept, under the profile's own name (line-anchored: 'sessionToken:'
		// is a substring of 'nsx_sessionToken:', so a Contains check would lie).
		Assert.DoesNotContain(lines, line => line.StartsWith("nsxManager:", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.StartsWith("sessionToken:", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.StartsWith("sessionCookieId:", StringComparison.Ordinal));

		// The reachability probe ran, against 443, before the session-token call.
		string[] probeCalls = await File.ReadAllLinesAsync(Path.Combine(_fixtureRoot, "probe-calls.txt"));
		Assert.Contains($"{InventedManager}|443|nsx", probeCalls);
	}

	/// <summary>
	/// Regression guard: a 4.x STIG-era profile (declares the legacy names) keeps the
	/// legacy auth block byte-for-byte -- per-profile resolution must not disturb the
	/// already-working 4.x path.
	/// </summary>
	[Fact]
	public async Task FourPointXProfile_AuthBlock_KeepsTheLegacyKeys()
	{
		string profileDir = WriteProfileFixture(
			"profile-4x", "nsxManager", "sessionToken", "sessionCookieId");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsxManager: '{InventedManager}'", lines);
		Assert.Contains($"sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"sessionCookieId: '{InventedCookie}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("nsx_", StringComparison.Ordinal));
	}

	/// <summary>
	/// A profile declaring NO recognized auth-input names (or carrying no inputs at
	/// all) falls back to the 4.x legacy names -- the same absent-signal default the
	/// sibling transport's <c>authInputKeys</c> resolution uses, so a legacy job shape
	/// with a bare profile keeps the pre-#917 behavior exactly.
	/// </summary>
	[Fact]
	public async Task ProfileWithoutDeclaredAuthInputs_DefaultsToTheLegacyKeys()
	{
		string profileDir = WriteProfileFixture("profile-bare");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsxManager: '{InventedManager}'", lines);
		Assert.Contains($"sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"sessionCookieId: '{InventedCookie}'", lines);
	}

	/// <summary>
	/// The folded-in reachability gate: an unreachable manager (probe fails) must
	/// classify as a crisp reachability failure BEFORE any session-token HTTP call --
	/// unreachable-vs-auth-failure must never blur (the ssh-side probe's own
	/// discipline, PR #1065).
	/// </summary>
	[Fact]
	public async Task UnreachableManager_FailsFast_BeforeTheSessionTokenCall()
	{
		string profileDir = WriteProfileFixture(
			"profile-9x-unreachable", "nsx_managerAddress", "nsx_sessionToken", "nsx_sessionCookieId");
		File.WriteAllText(Path.Combine(_fixtureRoot, "unreachable.flag"), "probe returns false");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.False(OutcomeSucceeded(outcome));
		string? failureReason = outcome.Properties["FailureReason"].Value?.ToString();
		Assert.NotNull(failureReason);
		Assert.Contains("443", failureReason);
		Assert.Contains(InventedManager, failureReason);

		// Fail-fast means fail-BEFORE: no session-token call, no generated auth block.
		Assert.False(File.Exists(Path.Combine(_fixtureRoot, "token-calls.txt")),
			"Get-NsxSessionToken must not be called for an unreachable manager");
		Assert.Null(authBlock);
	}

	/// <summary>
	/// Issue #1071 shape 1: every shipped NSX 4.x/3.x manifest declares its
	/// <c>inputs:</c> entries as column-0 <c>- name:</c> sequence items (no leading
	/// indent), not the indented style <see cref="WriteProfileFixture"/> emits. The
	/// pre-fix helper scoped the block by "next column-0 line", so these entries were
	/// all silently missed and the profile resolved to the 4.x legacy defaults instead
	/// of its own declared 9.x names.
	/// </summary>
	[Fact]
	public async Task ColumnZeroSequenceEntries_AreDiscovered()
	{
		string profileDir = WriteProfileFixtureRaw("profile-column0-sequence", """
			name: invented-nsx-fixture-profile
			title: Invented NSX baseline (test fixture)
			version: 1.0.0
			depends:
			- name: invented-shared-helper
			  path: ../invented-shared
			inputs:
			- name: nsx_managerAddress
			  description: invented fixture input
			  sensitive: true
			- name: nsx_sessionToken
			  description: invented fixture input
			  sensitive: true
			- name: nsx_sessionCookieId
			  description: invented fixture input
			  sensitive: true
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);
	}

	/// <summary>
	/// Issue #1071 shape 2: a column-0 <c>#</c> comment inside the <c>inputs:</c>
	/// block must not terminate discovery -- the pre-fix helper treated ANY column-0
	/// line as closing the block, comments included, so entries declared after the
	/// comment were silently missed.
	/// </summary>
	[Fact]
	public async Task ColumnZeroCommentInsideInputsBlock_DoesNotTerminateDiscovery()
	{
		string profileDir = WriteProfileFixtureRaw("profile-column0-comment", """
			name: invented-nsx-fixture-profile
			title: Invented NSX baseline (test fixture)
			version: 1.0.0
			depends:
			  - name: invented-shared-helper
			    path: ../invented-shared
			inputs:
			  - name: nsx_managerAddress
			    description: invented fixture input
			    sensitive: true
			# an invented column-0 comment inside the inputs block
			  - name: nsx_sessionToken
			    description: invented fixture input
			    sensitive: true
			  - name: nsx_sessionCookieId
			    description: invented fixture input
			    sensitive: true
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);
	}

	/// <summary>
	/// Issue #1071 shape 3: a trailing comment on the name line itself
	/// (<c>- name: nsx_managerAddress  # ...</c>) must not fold into the captured
	/// name -- the pre-fix helper captured the comment text as part of the value, so
	/// the name never matched either known key and the slot was silently missed.
	/// </summary>
	[Fact]
	public async Task TrailingCommentOnNameLine_DoesNotLoseTheSlot()
	{
		string profileDir = WriteProfileFixtureRaw("profile-trailing-comment", """
			name: invented-nsx-fixture-profile
			title: Invented NSX baseline (test fixture)
			version: 1.0.0
			depends:
			  - name: invented-shared-helper
			    path: ../invented-shared
			inputs:
			  - name: nsx_managerAddress  # invented trailing comment
			    description: invented fixture input
			    sensitive: true
			  - name: nsx_sessionToken  # another invented trailing comment
			    description: invented fixture input
			    sensitive: true
			  - name: nsx_sessionCookieId
			    description: invented fixture input
			    sensitive: true
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);
	}

	/// <summary>
	/// Issue #1071 shape 4: a nested <c>name:</c> key under an input's own
	/// <c>value:</c> mapping must NOT be treated as a sequence-entry name -- the
	/// pre-fix helper matched <c>name:</c> anywhere inside the open block regardless
	/// of nesting, so this nested key was a false positive that could silently steer
	/// the resolved manager key away from the profile's actually-declared name.
	/// The top-level declared name (<c>invented_other_input</c>) is not a recognized
	/// auth key, so a correct helper resolves the manager slot to null and the caller
	/// falls back to the 4.x legacy <c>nsxManager</c> name; a false positive would
	/// instead emit <c>nsx_managerAddress</c> (the nested value).
	/// </summary>
	[Fact]
	public async Task NestedNameKeyUnderValueMapping_IsNotFalsePositive()
	{
		string profileDir = WriteProfileFixtureRaw("profile-nested-name", """
			name: invented-nsx-fixture-profile
			title: Invented NSX baseline (test fixture)
			version: 1.0.0
			depends:
			  - name: invented-shared-helper
			    path: ../invented-shared
			inputs:
			  - name: invented_other_input
			    description: invented fixture input whose value mapping nests a name key
			    value:
			      name: nsx_managerAddress
			      kind: invented
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsxManager: '{InventedManager}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("nsx_managerAddress:", StringComparison.Ordinal));
	}

	/// <summary>
	/// Issue #1071 round-1 review finding 1 (the blocker): a sequence entry whose
	/// <c>name:</c> is NOT the entry's first key -- here <c>description:</c> comes
	/// first and <c>name:</c> follows on the next line, a perfectly ordinary mapping
	/// since YAML mapping keys are unordered. The first fix attempt decided
	/// "entry level" by the literal <c>-</c> prefix on the line, so this shape
	/// resolved to all-null and the 9.x profile silently fell back to the 4.x legacy
	/// names -- issue #917's exact defect (a scan that authenticates with no session),
	/// newly introduced. The accept rule must be structural: <c>name:</c> at the
	/// ENTRY'S OWN KEY COLUMN counts, whatever its position within the entry.
	/// </summary>
	[Fact]
	public async Task NameKeyAfterAnotherEntryKey_IsDiscovered()
	{
		string profileDir = WriteProfileFixtureRaw("profile-description-first", """
			name: invented-nsx-fixture-profile
			title: Invented NSX baseline (test fixture)
			version: 1.0.0
			depends:
			  - name: invented-shared-helper
			    path: ../invented-shared
			inputs:
			  - description: invented NSX manager address input
			    name: nsx_managerAddress
			    sensitive: true
			  - description: invented session token input
			    name: nsx_sessionToken
			  - description: invented session cookie input
			    name: nsx_sessionCookieId
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("nsxManager:", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.StartsWith("sessionToken:", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.StartsWith("sessionCookieId:", StringComparison.Ordinal));
	}

	/// <summary>
	/// Issue #1071 round-1 review finding 1, column-0 variant: the same
	/// not-the-first-key shape written in the column-0 sequence style every shipped
	/// NSX 4.x/3.x manifest uses -- the two hazards this issue is about, combined.
	/// Missed by BOTH the pre-#1071 helper and the first fix attempt.
	/// </summary>
	[Fact]
	public async Task ColumnZeroEntry_WithNameKeyAfterAnotherEntryKey_IsDiscovered()
	{
		string profileDir = WriteProfileFixtureRaw("profile-column0-description-first", """
			name: invented-nsx-fixture-profile
			version: 1.0.0
			depends:
			- name: invented-shared-helper
			  path: ../invented-shared
			inputs:
			- description: invented NSX manager address input
			  name: nsx_managerAddress
			- description: invented session token input
			  name: nsx_sessionToken
			- description: invented session cookie input
			  name: nsx_sessionCookieId
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);
	}

	/// <summary>
	/// Issue #1071 round-1 review finding 1, CRLF coverage (the #1077 note): the raw
	/// fixture writer inherits LF, so this rewrites the entry shapes above with CRLF
	/// endings. A manifest is vendor content and its line endings are not this
	/// repository's to assume; a captured name carrying a stray CR matches neither
	/// known key and silently loses the slot.
	/// </summary>
	[Fact]
	public async Task CrlfManifest_EntryShapes_AreDiscovered()
	{
		string profileDir = WriteProfileFixtureRawCrlf("profile-crlf-entry-shapes", """
			name: invented-nsx-fixture-profile
			version: 1.0.0
			depends:
			- name: invented-shared-helper
			  path: ../invented-shared
			inputs:
			- description: invented NSX manager address input
			  name: nsx_managerAddress
			- name: nsx_sessionToken  # invented trailing comment
			- name: 'nsx_sessionCookieId'
			""");

		// Guard the fixture itself: the shape under test IS the line endings, so a
		// writer that normalized them would make this test silently vacuous.
		string raw = await File.ReadAllTextAsync(Path.Combine(profileDir, "inspec.yml"));
		Assert.Contains("\r\n", raw, StringComparison.Ordinal);
		Assert.DoesNotContain("\n", raw.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"nsx_sessionToken: '{InventedToken}'", lines);
		Assert.Contains($"nsx_sessionCookieId: '{InventedCookie}'", lines);
	}

	/// <summary>
	/// Issue #1071 round-1 review finding 2: AC 4's false-positive class in its
	/// SEQUENCE form -- an input whose <c>value:</c> holds a LIST of mappings, one of
	/// which carries a <c>name:</c> key. That nested <c>- name:</c> is not an input
	/// name; only entries at the input sequence's own dash column are. A false
	/// positive would emit <c>nsx_managerAddress</c> here, steering the manager slot
	/// away from what the profile actually declares.
	/// </summary>
	[Fact]
	public async Task NestedNameKeyUnderValueSequence_IsNotFalsePositive()
	{
		string profileDir = WriteProfileFixtureRaw("profile-nested-name-sequence", """
			name: invented-nsx-fixture-profile
			version: 1.0.0
			inputs:
			  - name: invented_other_input
			    description: invented input whose value is a list of mappings
			    value:
			      - name: nsx_managerAddress
			        kind: invented
			      - name: nsx_sessionToken
			        kind: invented
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsxManager: '{InventedManager}'", lines);
		Assert.Contains($"sessionToken: '{InventedToken}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("nsx_", StringComparison.Ordinal));
	}

	/// <summary>
	/// Issue #1071 round-1 review finding 3: a column-0 <c>---</c> document marker is
	/// a document boundary, not a sequence entry, so it must CLOSE an open
	/// <c>inputs:</c> block. Entries after it belong to a different document and must
	/// not be discovered -- here the manager slot (declared before the marker)
	/// resolves while the token slot (after it) falls back to the 4.x legacy name.
	/// </summary>
	[Fact]
	public async Task DocumentMarker_ClosesTheInputsBlock()
	{
		string profileDir = WriteProfileFixtureRaw("profile-doc-marker", """
			name: invented-nsx-fixture-profile
			version: 1.0.0
			inputs:
			  - name: nsx_managerAddress
			    description: invented fixture input
			---
			  - name: nsx_sessionToken
			    description: invented input in a second document
			""");

		(System.Management.Automation.PSObject outcome, string? authBlock) = await RunNsxScanAsync(profileDir);

		Assert.True(OutcomeSucceeded(outcome), outcome.Properties["FailureReason"].Value?.ToString());
		string[] lines = AuthBlockLines(authBlock);
		Assert.Contains($"nsx_managerAddress: '{InventedManager}'", lines);
		Assert.Contains($"sessionToken: '{InventedToken}'", lines);
		Assert.DoesNotContain(lines, line => line.StartsWith("nsx_sessionToken:", StringComparison.Ordinal));
	}
}
