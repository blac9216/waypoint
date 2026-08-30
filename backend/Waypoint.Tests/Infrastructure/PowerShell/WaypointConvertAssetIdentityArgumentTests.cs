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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Scans;
using Waypoint.Tests.Infrastructure.Execution;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #1068 / PR #1224 review round 1 finding 1: the behaviour this PR exists for
/// was provable ONLY by a Pester suite that CI never ran (the <c>pester</c> job's
/// <c>$config.Run.Path</c> is pinned to the shape-inventory directory), so a human had
/// to remember to type <c>Invoke-Pester</c> for the change to be tested at all. The
/// workflow file is owner-gated -- the automation account cannot push
/// <c>.github/workflows</c> edits, tracked as issue #1245 (<c>help</c>) -- so the CI
/// registration this class provides instead is <c>dotnet test</c>: these xunit facts
/// drive the SAME real <c>WaypointScan.psm1</c> <c>Invoke-WaypointConvert</c> through
/// the real in-process executor, so the argument-string contract cannot silently stop
/// being checked. The Pester suite
/// (<c>WaypointScan.ConvertAssetIdentity.Tests.ps1</c>) is kept as-is and will be
/// wired into the <c>pester</c> job when #1245 lands.
///
/// Technique is <see cref="WaypointVsphereScanNarrowingInputTests"/>'s, verbatim: the
/// sibling repo's <c>module.common.ps1</c> is replaced through the function's own
/// <c>-VmwareStigDockerCommonPath</c> parameter by an INVENTED fake script that first
/// dot-sources the REAL vendored <c>runners/compliance-runner/powershell/module.common.ps1</c>
/// -- so the <c>New-CklConvertArgs</c> under test is the genuine vendored builder, not
/// a re-implementation -- and then overrides <c>Invoke-ExternalCommand</c> to capture
/// the exact <c>saf</c> argument string and materialize the CKL output file. No real
/// <c>saf</c> binary ever runs. All fixture values are invented (AGENTS.md
/// sanitization: RFC 2606 <c>example.internal</c> names, RFC 5737 documentation IPs).
/// </summary>
public sealed class WaypointConvertAssetIdentityArgumentTests : IDisposable
{
	private sealed class DiscardLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	private const string InventedHostname = "invented-target-a";
	private const string InventedFqdn = "invented-target-a.example.internal";
	private const string InventedIp = "198.51.100.10";
	private const string InventedMac = "00:00:5E:00:53:01";

	/// <summary>
	/// The reviewer's exact payload shape: a target name that closes the vendored
	/// builder's quoted <c>--hostname "..."</c> segment and appends a second
	/// <c>-o</c>, redirecting saf's CKL write to an attacker-chosen path.
	/// </summary>
	private const string InjectionPayload = "evil\" -o \"/w/pwned.ckl";

	private readonly string _fixtureRoot;
	private WaypointRunspacePool _pool = null!;

	public WaypointConvertAssetIdentityArgumentTests()
	{
		_fixtureRoot = Path.Combine(Path.GetTempPath(), $"waypoint-convert-asset-{Guid.NewGuid():N}");
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

	/// <summary>
	/// Invented fake sibling script: dot-sources the REAL vendored common module (so
	/// <c>New-CklConvertArgs</c> is genuine), then replaces <c>Invoke-ExternalCommand</c>
	/// with a capture-only fake that appends the argument string to
	/// <c>saf-args.txt</c> and writes a placeholder CKL at the FIRST <c>-o</c> path, so
	/// <c>Invoke-WaypointConvert</c>'s own <c>Test-Path</c> success check passes.
	/// Writing at the first <c>-o</c> is deliberate: an injected second <c>-o</c> must
	/// not be what satisfies the check, or the injection test could pass for the wrong
	/// reason.
	/// </summary>
	private string WriteFakeCommonScript()
	{
		string realCommonPath = FindRepoFile("runners/compliance-runner/powershell/module.common.ps1");
		string capture = _fixtureRoot;
		string commonPath = Path.Combine(_fixtureRoot, "fake.module.common.ps1");
		File.WriteAllText(commonPath, $$"""
			. '{{realCommonPath}}'

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
				Add-Content -Path '{{capture}}/saf-args.txt' -Value $Arguments
				$OutMatch = [regex]::Match($Arguments, '-o "([^"]+)"')
				if ($OutMatch.Success) {
					$OutPath = $OutMatch.Groups[1].Value
					$OutDir = Split-Path -Path $OutPath -Parent
					if ($OutDir -and -not (Test-Path -Path $OutDir -PathType Container)) {
						New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
					}
					Set-Content -Path $OutPath -Value '<CHECKLIST></CHECKLIST>' -Encoding utf8
				}
				return 0
			}
			""");
		return commonPath;
	}

	/// <summary>
	/// Runs the real <c>Invoke-WaypointConvert</c> with the given asset facts (any of
	/// which may be null = "the plan item does not hold this fact") and returns the
	/// outcome plus the exact argument string handed to <c>saf</c>.
	/// </summary>
	private async Task<(bool Success, string SafArguments)> ConvertAsync(
		string caseName, string? hostname = null, string? fqdn = null, string? ip = null, string? mac = null)
	{
		string commonPath = WriteFakeCommonScript();
		string convertInput = Path.Combine(_fixtureRoot, $"{caseName}-input.json");
		await File.WriteAllTextAsync(convertInput, "{\"profiles\":[]}");
		string cklPath = Path.Combine(_fixtureRoot, "out", $"{caseName}.ckl");

		PowerShellExecutor executor = CreateExecutor();
		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["ConvertInputPath"] = convertInput,
			["CklOutputPath"] = cklPath,
			["TimeoutSeconds"] = 60,
			["VmwareStigDockerCommonPath"] = commonPath,
		};
		if (hostname is not null)
		{
			parameters["Hostname"] = hostname;
		}

		if (fqdn is not null)
		{
			parameters["Fqdn"] = fqdn;
		}

		if (ip is not null)
		{
			parameters["Ip"] = ip;
		}

		if (mac is not null)
		{
			parameters["Mac"] = mac;
		}

		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-WaypointConvert", Parameters: parameters),
			CancellationToken.None);
		Assert.True(result.Succeeded, result.FailureReason);

		System.Management.Automation.PSObject outcome =
			System.Management.Automation.PSObject.AsPSObject(Assert.Single(result.Output)!);
		bool success = System.Management.Automation.LanguagePrimitives
			.ConvertTo<bool>(outcome.Properties["Success"].Value);

		string argsPath = Path.Combine(_fixtureRoot, "saf-args.txt");
		Assert.True(File.Exists(argsPath), "Invoke-ExternalCommand (saf) was never called.");
		string safArguments = (await File.ReadAllTextAsync(argsPath)).Trim();
		File.Delete(argsPath);
		return (success, safArguments);
	}

	/// <summary>
	/// AC1, all-facts branch: every fact the plan item holds is stamped, each under its
	/// own flag, by the genuine vendored builder.
	/// </summary>
	[Fact]
	public async Task AllFactsPresent_StampsEveryAssetIdentityFlag()
	{
		(bool success, string safArguments) = await ConvertAsync(
			"all", InventedHostname, InventedFqdn, InventedIp, InventedMac);

		Assert.True(success);
		Assert.Contains($"--hostname \"{InventedHostname}\"", safArguments, StringComparison.Ordinal);
		Assert.Contains($"--fqdn \"{InventedFqdn}\"", safArguments, StringComparison.Ordinal);
		Assert.Contains($"--ip \"{InventedIp}\"", safArguments, StringComparison.Ordinal);
		Assert.Contains($"--mac \"{InventedMac}\"", safArguments, StringComparison.Ordinal);
	}

	/// <summary>
	/// The legacy/unmapped path: no fact supplied means no flag emitted -- the
	/// pre-#1068 argument string byte-for-byte. A missing fact is never invented.
	/// </summary>
	[Fact]
	public async Task NoFactsPresent_EmitsNoAssetIdentityFlagAtAll()
	{
		(bool success, string safArguments) = await ConvertAsync("none");

		Assert.True(success);
		Assert.DoesNotContain("--hostname", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--fqdn", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--ip", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--mac", safArguments, StringComparison.Ordinal);
	}

	/// <summary>
	/// The shape Waypoint actually produces today: Hostname always, exactly one of
	/// Fqdn/Ip, never a Mac (no MAC source in the domain model -- issue #1227). Pins
	/// that a partially-known asset stamps only what is known.
	/// </summary>
	[Fact]
	public async Task PartialFacts_StampsOnlyTheKnownOnes()
	{
		(bool success, string safArguments) = await ConvertAsync("partial", InventedHostname, ip: InventedIp);

		Assert.True(success);
		Assert.Contains($"--hostname \"{InventedHostname}\"", safArguments, StringComparison.Ordinal);
		Assert.Contains($"--ip \"{InventedIp}\"", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--fqdn", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--mac", safArguments, StringComparison.Ordinal);
	}

	/// <summary>
	/// AC3 at the argument-string layer: two same-profile targets produce different
	/// asset identity, which is the whole point of restoring these flags (pre-#1068
	/// both CKLs carried identical, benchmark-only identity and collided in STIG
	/// Manager). The per-target end-to-end derivation is pinned separately by
	/// <c>ScanJobHandlerEndToEndTests</c>.
	/// </summary>
	[Fact]
	public async Task TwoSameProfileTargets_ProduceDistinguishableAssetIdentity()
	{
		(bool successOne, string argsOne) = await ConvertAsync(
			"target-one", "invented-target-one", "invented-target-one.example.internal");
		(bool successTwo, string argsTwo) = await ConvertAsync(
			"target-two", "invented-target-two", "invented-target-two.example.internal");

		Assert.True(successOne);
		Assert.True(successTwo);
		Assert.NotEqual(argsOne, argsTwo);
		Assert.Contains("--hostname \"invented-target-one\"", argsOne, StringComparison.Ordinal);
		Assert.Contains("--hostname \"invented-target-two\"", argsTwo, StringComparison.Ordinal);
	}

	/// <summary>
	/// Review round 1 finding 2, the injection pin: the reviewer's exact payload
	/// (<c>evil" -o "/w/pwned.ckl</c>) as a target name must never reach the vendored
	/// builder. The argument string must carry exactly ONE <c>-o</c> -- the CKL path
	/// this handler chose -- must not mention the attacker's path, and must omit the
	/// <c>--hostname</c> flag entirely rather than stamping a stripped/escaped rewrite
	/// (a mangled asset name in an eMASS-visible CKL still looks authoritative).
	/// </summary>
	[Fact]
	public async Task HostnameCarryingAnInjectedFlag_IsRejected_NotStampedAndNotInterpolated()
	{
		(bool success, string safArguments) = await ConvertAsync("injection", InjectionPayload);

		Assert.True(success);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(safArguments, "-o "));
		Assert.DoesNotContain("/w/pwned.ckl", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("evil", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--hostname", safArguments, StringComparison.Ordinal);
	}

	/// <summary>
	/// Same guard on the two connection-derived fields, and on a value saf would parse
	/// as a flag of its own: neither an embedded quote in an Fqdn nor a leading
	/// <c>-</c> in an Ip may produce a flag.
	/// </summary>
	[Fact]
	public async Task FqdnWithQuote_AndIpBeginningWithADash_AreBothRejected()
	{
		(bool success, string safArguments) = await ConvertAsync(
			"injection-conn", InventedHostname, fqdn: "bad\"host.example.internal", ip: "-o");

		Assert.True(success);
		Assert.Contains($"--hostname \"{InventedHostname}\"", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--fqdn", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--ip", safArguments, StringComparison.Ordinal);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(safArguments, "-o "));
	}

	/// <summary>
	/// Review round 2's argv-echo harness. The injection is invisible in the argument
	/// STRING -- round 1's test counted `-o ` substrings and passed on a payload that
	/// still produced a second `-o` -- so this writes a tiny shell script that prints
	/// each argument it receives on its own line, hands the captured saf argument
	/// string to a REAL <see cref="ProcessStartInfo"/> exactly as
	/// <c>Invoke-ExternalCommand</c> would (<c>UseShellExecute = false</c>), and returns
	/// the argv the child process actually saw. It is launched through <c>/bin/sh</c>
	/// so no chmod is needed; <c>sh</c> passes its positional parameters to the script
	/// untouched, so the only parsing that happens is .NET's own -- which is the parser
	/// under test.
	/// </summary>
	private string[] ParseArgv(string safArguments)
	{
		string scriptPath = Path.Combine(_fixtureRoot, "argv-echo.sh");
		File.WriteAllText(scriptPath, "for a in \"$@\"; do printf '%s\\n' \"$a\"; done\n");

		ProcessStartInfo startInfo = new()
		{
			FileName = "/bin/sh",
			Arguments = $"{scriptPath} {safArguments}",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			CreateNoWindow = true,
		};

		using Process process = Process.Start(startInfo)!;
		string stdout = process.StandardOutput.ReadToEnd();
		process.WaitForExit(30_000);
		return stdout.TrimEnd('\n').Split('\n', StringSplitOptions.None);
	}

	/// <summary>
	/// Review round 2 blocker, reproduced end to end and then pinned. The reviewer's
	/// payload -- target name <c>target-a\</c> plus connection host
	/// <c>x -o /w/pwned.ckl</c> -- carries no quote, no control character and no
	/// leading dash, so round 1's deny list passed it; the trailing backslash then
	/// escaped the vendored builder's closing quote and .NET's argument parser handed
	/// saf a SECOND <c>-o</c>.
	///
	/// The assertion is on the parsed argv, counting TOKENS equal to <c>-o</c>, not on
	/// substrings of the argument string: the string is not where the injection becomes
	/// visible, which is precisely why round 1's test missed it. Exactly one argv token
	/// may be <c>-o</c>, no token may name the attacker's path, and neither field may
	/// be stamped at all (the guard rejects, it does not sanitize).
	/// </summary>
	[Fact]
	public async Task ReviewRound2TrailingBackslashPayload_ProducesExactlyOneOutputFlagInArgv()
	{
		(bool success, string safArguments) = await ConvertAsync(
			"argv-round2",
			CklAssetIdentityCaseTable.TrailingBackslashHostname,
			fqdn: CklAssetIdentityCaseTable.SecondOutputFlagHost);

		Assert.True(success);

		// The load-bearing assertion, asserted FIRST: count argv TOKENS equal to "-o".
		string[] argv = ParseArgv(safArguments);
		Assert.Equal(1, argv.Count(token => token == "-o"));
		Assert.DoesNotContain(argv, token => token.Contains("pwned", StringComparison.Ordinal));
		Assert.DoesNotContain(argv, token => token.Contains(@"target-a\", StringComparison.Ordinal));

		// And the fields are omitted outright rather than stamped in a mangled form.
		Assert.DoesNotContain("--hostname", safArguments, StringComparison.Ordinal);
		Assert.DoesNotContain("--fqdn", safArguments, StringComparison.Ordinal);
	}

	/// <summary>
	/// The same argv-level assertion for round 1's quote payload, and for a legitimate
	/// all-facts conversion: whatever the input, saf sees exactly one output flag, and
	/// the legitimate facts each arrive as a SINGLE argv token (a value with internal
	/// spaces must not fragment).
	/// </summary>
	[Fact]
	public async Task LegitimateAndRound1Payloads_BothYieldExactlyOneOutputFlagInArgv()
	{
		(bool injectionSuccess, string injectionArguments) = await ConvertAsync(
			"argv-round1", InjectionPayload);
		Assert.True(injectionSuccess);
		string[] injectionArgv = ParseArgv(injectionArguments);
		Assert.Equal(1, injectionArgv.Count(token => token == "-o"));
		Assert.DoesNotContain(injectionArgv, token => token.Contains("pwned", StringComparison.Ordinal));

		(bool legitimateSuccess, string legitimateArguments) = await ConvertAsync(
			"argv-legit", "invented host 07", InventedFqdn, InventedIp, InventedMac);
		Assert.True(legitimateSuccess);
		string[] legitimateArgv = ParseArgv(legitimateArguments);
		Assert.Equal(1, legitimateArgv.Count(token => token == "-o"));
		Assert.Contains("invented host 07", legitimateArgv);
		Assert.Contains(InventedFqdn, legitimateArgv);
		Assert.Contains(InventedIp, legitimateArgv);
		Assert.Contains(InventedMac, legitimateArgv);
	}

	/// <summary>
	/// Calls the module-internal <c>Get-WaypointSafeCklAssetValue</c> in the real
	/// <c>WaypointScan.psm1</c> without widening the module's exported surface: the
	/// script block runs IN the module's own session state (<c>&amp; (Get-Module ...)</c>),
	/// so the function under test is the genuine one the module's own
	/// <c>Invoke-WaypointConvert</c> calls. The candidate value is a BOUND parameter
	/// (the script's own <c>param($Value, ...)</c> block), never spliced into the script text -- a test for an injection
	/// guard must not itself concatenate operator-controlled text into code.
	/// </summary>
	private const string MirrorProbeScript =
		"param($Value, $FieldName) " +
		"& (Get-Module WaypointScan) { param($ProbeValue, $ProbeField) " +
		"Get-WaypointSafeCklAssetValue -Value $ProbeValue -FieldName $ProbeField -WarningAction SilentlyContinue } " +
		"$Value $FieldName";

	/// <summary>
	/// Review round 2 finding 2: the two guards, pinned to each other. Every case in
	/// <see cref="CklAssetIdentityCaseTable"/> is put through the REAL
	/// <c>Get-WaypointSafeCklAssetValue</c> in <c>WaypointScan.psm1</c> (loaded by the
	/// in-process executor, no re-implementation) and its verdict compared with
	/// <c>CklAssetIdentity.TryAccept</c>'s. A disagreement in either direction fails --
	/// including the C1 control characters and the backslash that made the round-1
	/// mirror the weaker half exactly where it is the only half. Comments claiming the
	/// two rules match are not evidence; this is.
	/// </summary>
	[Fact]
	public async Task PowerShellMirror_AgreesWithCSharpGuard_OnEveryTableCase()
	{
		PowerShellExecutor executor = CreateExecutor();
		List<string> disagreements = [];

		foreach (CklAssetIdentityCaseTable.GuardCase testCase in CklAssetIdentityCaseTable.Cases)
		{
			PowerShellExecutionResult result = await executor.ExecuteAsync(
				new PowerShellRequest(
					MirrorProbeScript,
					Kind: PowerShellRequestKind.Script,
					Parameters: new Dictionary<string, object?>(StringComparer.Ordinal)
					{
						["Value"] = testCase.Value,
						["FieldName"] = "Hostname",
					}),
				CancellationToken.None);
			Assert.True(result.Succeeded, result.FailureReason);

			string powerShellResult = result.Output.Count == 0
				? string.Empty
				: System.Management.Automation.LanguagePrimitives.ConvertTo<string>(result.Output[0]) ?? string.Empty;
			bool powerShellAccepted = powerShellResult.Length > 0;

			bool csharpAccepted = CklAssetIdentity.TryAccept(testCase.Value, out string csharpResult);

			if (powerShellAccepted != testCase.Accepted || csharpAccepted != testCase.Accepted)
			{
				disagreements.Add(
					$"'{testCase.Label}': expected accepted={testCase.Accepted}, C#={csharpAccepted}, PowerShell={powerShellAccepted}");
				continue;
			}

			if (testCase.Accepted && !string.Equals(powerShellResult, csharpResult, StringComparison.Ordinal))
			{
				disagreements.Add($"'{testCase.Label}': accepted by both but the returned values differ");
			}
		}

		Assert.Empty(disagreements);
	}
}
