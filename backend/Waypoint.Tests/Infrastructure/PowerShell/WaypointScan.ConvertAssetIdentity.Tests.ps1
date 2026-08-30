#Requires -Modules Pester

# Copyright 2026 Justin Black
#
# Licensed under the Apache License, Version 2.0 (the "License").
# You may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

# Issue #1068: Invoke-WaypointConvert dropped the sibling's --hostname/--fqdn/--ip/--mac
# CKL asset-identity flags entirely. These tests drive the REAL WaypointScan.psm1
# Invoke-WaypointConvert through a fake -VmwareStigDockerCommonPath (same technique as
# WaypointVsphereScanNarrowingInputTests.cs): the fake script dot-sources the REAL,
# vendored runners/compliance-runner/powershell/module.common.ps1 (so New-CklConvertArgs
# under test is the genuine vendored function, not a re-implementation), then replaces
# Invoke-ExternalCommand with an invented fake that captures the built `saf` argument
# string and materializes the CKL output file (no real `saf`/InSpec binary is invoked).
#
# All fixture values (hostnames, IPs, MACs) are invented (AGENTS.md sanitization).

BeforeAll {
	$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
	$ScanModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointScan/WaypointScan.psm1'
	$RealCommonPath = Join-Path $RepoRoot 'runners/compliance-runner/powershell/module.common.ps1'

	$script:ScanModule = Import-Module $ScanModulePath -Force -PassThru

	# Fake module.common.ps1: dot-sources the REAL vendored common module (so
	# New-CklConvertArgs is genuine), then overrides Invoke-ExternalCommand with an
	# invented fake that captures the `saf` argument string into $Global:WaypointTest_LastSafArgs
	# and touches the CKL output path so Invoke-WaypointConvert's own
	# Test-Path/Success check passes -- no real `saf`/InSpec process ever runs.
	$script:FakeCommonPath = Join-Path $TestDrive 'fake.module.common.ps1'
	@"
. '$RealCommonPath'

function Invoke-ExternalCommand {
	param(
		[string]`$Executable,
		[string]`$Arguments,
		[int]`$TimeoutMilliseconds,
		[string]`$ProcessName,
		[int[]]`$AllowedExitCodes,
		[string]`$Source,
		[switch]`$SurfaceOutputOnFailure
	)

	`$Global:WaypointTest_LastSafArgs = `$Arguments

	if (`$Arguments -match '-o\s+"([^"]+)"') {
		`$OutPath = `$Matches[1]
		`$OutDir = Split-Path -Path `$OutPath -Parent
		if (`$OutDir -and -not (Test-Path -Path `$OutDir -PathType Container)) {
			New-Item -ItemType Directory -Path `$OutDir -Force | Out-Null
		}
		Set-Content -Path `$OutPath -Value '<CHECKLIST></CHECKLIST>' -Encoding utf8
	}

	return `$null
}
"@ | Set-Content -Path $script:FakeCommonPath -Encoding utf8
}

Describe 'Invoke-WaypointConvert asset-identity flags (issue #1068)' {

	BeforeEach {
		$Global:WaypointTest_LastSafArgs = $null
	}

	AfterAll {
		Remove-Item -Path Variable:\Global:WaypointTest_LastSafArgs -ErrorAction SilentlyContinue
	}

	It 'passes --hostname/--fqdn/--ip when the plan item supplies all three facts' {
		$CklOutputPath = Join-Path $TestDrive 'target-a.ckl'

		$Result = Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-a.json') -CklOutputPath $CklOutputPath `
			-Hostname 'target-a' -Fqdn 'target-a.example.internal' -Ip '198.51.100.10' `
			-VmwareStigDockerCommonPath $script:FakeCommonPath

		$Result.Success | Should -BeTrue
		$Global:WaypointTest_LastSafArgs | Should -Match '--hostname\s+"target-a"'
		$Global:WaypointTest_LastSafArgs | Should -Match '--fqdn\s+"target-a\.example\.internal"'
		$Global:WaypointTest_LastSafArgs | Should -Match '--ip\s+"198\.51\.100\.10"'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--mac'
	}

	It 'omits every flag when no target facts are supplied (legacy/unmapped path)' {
		$CklOutputPath = Join-Path $TestDrive 'target-legacy.ckl'

		$Result = Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-legacy.json') -CklOutputPath $CklOutputPath `
			-VmwareStigDockerCommonPath $script:FakeCommonPath

		$Result.Success | Should -BeTrue
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--hostname'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--fqdn'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--ip'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--mac'
	}

	It 'omits only the missing facts, keeping the ones supplied' {
		$CklOutputPath = Join-Path $TestDrive 'target-partial.ckl'

		$Result = Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-partial.json') -CklOutputPath $CklOutputPath `
			-Hostname 'target-partial' `
			-VmwareStigDockerCommonPath $script:FakeCommonPath

		$Result.Success | Should -BeTrue
		$Global:WaypointTest_LastSafArgs | Should -Match '--hostname\s+"target-partial"'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--fqdn'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--ip'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--mac'
	}

	It 'produces distinguishable asset identity for two same-profile targets' {
		$CklOutputPathOne = Join-Path $TestDrive 'target-one.ckl'
		$CklOutputPathTwo = Join-Path $TestDrive 'target-two.ckl'

		Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-one.json') -CklOutputPath $CklOutputPathOne `
			-Hostname 'target-one' -Fqdn 'target-one.example.internal' -VmwareStigDockerCommonPath $script:FakeCommonPath | Out-Null
		$ArgsOne = $Global:WaypointTest_LastSafArgs

		Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-two.json') -CklOutputPath $CklOutputPathTwo `
			-Hostname 'target-two' -Fqdn 'target-two.example.internal' -VmwareStigDockerCommonPath $script:FakeCommonPath | Out-Null
		$ArgsTwo = $Global:WaypointTest_LastSafArgs

		$ArgsOne | Should -Not -Be $ArgsTwo
		$ArgsOne | Should -Match '--hostname\s+"target-one"'
		$ArgsTwo | Should -Match '--hostname\s+"target-two"'
	}

	# PR #1224 review round 1 finding 2 (argument injection): the vendored
	# New-CklConvertArgs interpolates each fact into a double-quoted segment with no
	# escaping, so an operator-authored target name carrying a double quote could close
	# the segment and append a SECOND -o, redirecting saf's CKL write. The reviewer's
	# exact payload must be rejected by Get-WaypointSafeCklAssetValue before the builder
	# ever sees it -- omitted entirely, never stripped into a mangled-but-authoritative
	# asset name.
	It 'rejects an injected -o in a target name rather than interpolating it' {
		$CklOutputPath = Join-Path $TestDrive 'target-injection.ckl'

		$Result = Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-injection.json') -CklOutputPath $CklOutputPath `
			-Hostname 'evil" -o "/w/pwned.ckl' `
			-VmwareStigDockerCommonPath $script:FakeCommonPath -WarningAction SilentlyContinue

		$Result.Success | Should -BeTrue
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--hostname'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match 'pwned\.ckl'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match 'evil'
		([regex]::Matches($Global:WaypointTest_LastSafArgs, '-o\s+"')).Count | Should -Be 1
	}

	It 'rejects a fqdn containing a double quote and an ip beginning with a dash' {
		$CklOutputPath = Join-Path $TestDrive 'target-injection-conn.ckl'

		$Result = Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-injection-conn.json') -CklOutputPath $CklOutputPath `
			-Hostname 'target-safe' -Fqdn 'bad"host.example.internal' -Ip '-o' `
			-VmwareStigDockerCommonPath $script:FakeCommonPath -WarningAction SilentlyContinue

		$Result.Success | Should -BeTrue
		$Global:WaypointTest_LastSafArgs | Should -Match '--hostname\s+"target-safe"'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--fqdn'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--ip'
		([regex]::Matches($Global:WaypointTest_LastSafArgs, '-o\s+"')).Count | Should -Be 1
	}

	# PR #1224 review round 2 blocker: the round-1 deny list passed a value ENDING in a
	# backslash (`target-a\`), and .NET's ProcessStartInfo argument parser then treated
	# the vendored builder's closing quote as escaped -- realigning the quoting so the
	# NEXT field's contents (`x -o /w/pwned.ckl`) fell out as separate argv tokens, a
	# second -o reaching saf. The guard is now an allow-list, and the assertion below is
	# on the argv a REAL process receives, not on substrings of the argument string:
	# the string is not where this injection is visible.
	It 'rejects a trailing-backslash target name and leaves exactly one -o in real argv' {
		$CklOutputPath = Join-Path $TestDrive 'target-backslash.ckl'

		$Result = Invoke-WaypointConvert -ConvertInputPath (Join-Path $TestDrive 'input-backslash.json') -CklOutputPath $CklOutputPath `
			-Hostname 'target-a\' -Fqdn 'x -o /w/pwned.ckl' `
			-VmwareStigDockerCommonPath $script:FakeCommonPath -WarningAction SilentlyContinue

		$Result.Success | Should -BeTrue
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--hostname'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match '--fqdn'
		$Global:WaypointTest_LastSafArgs | Should -Not -Match 'pwned'

		# argv round trip: /bin/sh hands its positional parameters to the script
		# untouched, so the only parsing applied is .NET's own -- the parser under test.
		$EchoScript = Join-Path $TestDrive 'argv-echo.sh'
		Set-Content -Path $EchoScript -Value 'for a in "$@"; do printf ''%s\n'' "$a"; done' -Encoding utf8
		$StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
		$StartInfo.FileName = '/bin/sh'
		$StartInfo.Arguments = "$EchoScript $Global:WaypointTest_LastSafArgs"
		$StartInfo.UseShellExecute = $false
		$StartInfo.RedirectStandardOutput = $true
		$Process = [System.Diagnostics.Process]::Start($StartInfo)
		$Argv = @($Process.StandardOutput.ReadToEnd().TrimEnd("`n") -split "`n")
		$null = $Process.WaitForExit(30000)

		@($Argv | Where-Object { $_ -eq '-o' }).Count | Should -Be 1
		@($Argv | Where-Object { $_ -like '*pwned*' }).Count | Should -Be 0
	}

	# The allow-list itself, at the PowerShell chokepoint. The authoritative pinning of
	# this rule against the C# guard is the xunit test
	# WaypointConvertAssetIdentityArgumentTests.PowerShellMirror_AgreesWithCSharpGuard_OnEveryTableCase,
	# which drives one shared case table through both; these two cases are the
	# characters a reader of this file most needs to see rejected.
	It 'rejects values outside the accepted character class' -ForEach @(
		@{ Value = 'host`name' }
		@{ Value = 'host$name' }
		@{ Value = 'domain\host' }
		@{ Value = "host`u{0085}name" }
		@{ Value = 'hote-invente' + [char]0x00E9 }
	) {
		# The guard is module-internal (not exported): invoke it in the module's own
		# session state so the function under test is the genuine one.
		& $script:ScanModule { param($ProbeValue) Get-WaypointSafeCklAssetValue -Value $ProbeValue -FieldName 'Hostname' -WarningAction SilentlyContinue } $Value |
			Should -BeNullOrEmpty
	}

	It 'passes a legitimate asset fact through unchanged' -ForEach @(
		@{ Value = 'invented-target-a' }
		@{ Value = 'invented-target-a.example.internal' }
		@{ Value = '198.51.100.10' }
		@{ Value = '2001:db8::1' }
		@{ Value = '00:00:5E:00:53:01' }
		@{ Value = 'invented host 07' }
	) {
		& $script:ScanModule { param($ProbeValue) Get-WaypointSafeCklAssetValue -Value $ProbeValue -FieldName 'Hostname' } $Value |
			Should -BeExactly $Value
	}
}
