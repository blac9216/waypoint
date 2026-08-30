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
}
