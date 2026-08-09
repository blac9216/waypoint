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

# Invented stub for the scan full-loop integration test (issue #274). Mirrors
# Invoke-WaypointScan's real signature and output shape without touching the sibling
# repo, a real `inspec` binary, or a real vCenter -- no vendor code, no real hostnames
# or credentials, everything here is fabricated (fictional example.internal hosts only,
# per CLAUDE.md's sanitization gate).
#
# $env:WAYPOINT_SCAN_STUB_MODE drives which canned outcome this call reports:
#   'success' (default) -- writes a small invented HDF-shaped JSON report and returns
#     Success = $true.
#   'exit100' -- same as success (exit code 100 == compliant scan with failures present,
#     a predecessor-defined SUCCESS per issue #274's AC), proving the handler does not
#     conflate a non-zero InSpec exit code with a real failure.
#   'failure' -- returns Success = $false with an invented non-auth FailureReason.
#   'auth' -- returns Success = $false with a FailureReason containing an
#     AuthFailureMarkers-recognized token ("401"), so the handler's classification path
#     is exercised without a real credential ever being rejected by anything.

function Invoke-WaypointScan {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$VCenter,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[int]$TimeoutSeconds,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath
	)

	# Deliberately touches the Information stream, exactly like
	# WaypointDiscoveryStubModule -- if the decrypted password ever leaked into this
	# handler's invocation, the canary test would catch it here.
	$InformationPreference = 'Continue'
	Write-Information "Scanning stub vCenter '$VCenter' as '$Username' (password length $($Password.Length)) profile '$ProfilePath'"

	$Mode = $env:WAYPOINT_SCAN_STUB_MODE
	if (-not $Mode) { $Mode = 'success' }

	if ($Mode -eq 'failure') {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = 1
			ReportPath    = $null
			FailureReason = 'invented InSpec transport failure (stub).'
		}
	}

	if ($Mode -eq 'auth') {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = 2
			ReportPath    = $null
			FailureReason = '401 Unauthorized: invented credential rejection (stub).'
		}
	}

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	# Minimal invented HDF-shaped payload -- enough to prove the handler persists
	# whatever InSpec wrote, not a claim this is a real HDF schema.
	$ExitCode = if ($Mode -eq 'exit100') { 100 } else { 0 }
	$Hdf = [ordered]@{
		platform = [ordered]@{ name = 'vmware_vsphere'; release = 'stub' }
		profiles = @(
			[ordered]@{
				name    = 'invented-stub-profile'
				version = '0.0.0'
				controls = @()
			}
		)
		statistics = [ordered]@{ duration = 0.1 }
	}
	($Hdf | ConvertTo-Json -Depth 6) | Set-Content -Path $ReportPath -Encoding utf8

	Write-Information 'Scan complete.'

	return [pscustomobject]@{
		Success       = $true
		ExitCode      = $ExitCode
		ReportPath    = $ReportPath
		FailureReason = $null
	}
}

# $env:WAYPOINT_ATTEST_STUB_MODE: 'success' (default, applies when a template path is
# given) -- writes an invented "attested" HDF and returns AttestApplied = $true when
# AttestTemplatePath is set, or the pass-through shape when it is not (mirrors the real
# module's no-attestation-docs path). 'failure' -- Success = $false.
#
# Issue #304 canary: before doing anything else, if $AttestTemplatePath is set this stub
# stats the file ScanJobHandler wrote (created 0600 before the resolved attestation body
# was written, per #304's fix) and writes its Unix permission bits to
# $env:WAYPOINT_ATTEST_TEMPFILE_MODE_PATH, a scratch file the test reads AFTER this call
# returns but BEFORE the handler's `finally` block deletes the temp file -- proving the
# file is owner-only (0600) on disk at the moment the "saf attest apply" step would read
# it, not just eventually.
function Invoke-WaypointAttest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string]$ReportPath,
		[Parameter()] [AllowNull()] [AllowEmptyString()] [string]$AttestTemplatePath,
		[Parameter()] [string]$AttestedReportPath,
		[Parameter()] [int]$TimeoutSeconds,
		[Parameter()] [string]$VmwareStigDockerCommonPath
	)

	if ($AttestTemplatePath -and $env:WAYPOINT_ATTEST_TEMPFILE_MODE_PATH -and -not $IsWindows) {
		$ModeValue = (Get-Item -Path $AttestTemplatePath).UnixFileMode
		Set-Content -Path $env:WAYPOINT_ATTEST_TEMPFILE_MODE_PATH -Value $ModeValue.ToString() -NoNewline
	}

	if ((Get-Content -Path $ReportPath -Raw) -match '\bWaypoint-Canary\b') {
		Write-Information 'Attest stub observed the HDF report.'
	}

	$Mode = $env:WAYPOINT_ATTEST_STUB_MODE
	if (-not $Mode) { $Mode = 'success' }

	if ($Mode -eq 'failure') {
		return [pscustomobject]@{ Success = $false; AttestApplied = $false; ReportPath = $null; FailureReason = 'invented SAF attestation failure (stub).' }
	}

	if ([string]::IsNullOrWhiteSpace($AttestTemplatePath)) {
		return [pscustomobject]@{ Success = $true; AttestApplied = $false; ReportPath = $ReportPath; FailureReason = $null }
	}

	'{"attested":true}' | Set-Content -Path $AttestedReportPath -Encoding utf8
	return [pscustomobject]@{ Success = $true; AttestApplied = $true; ReportPath = $AttestedReportPath; FailureReason = $null }
}

# $env:WAYPOINT_CONVERT_STUB_MODE: 'success' (default) -- writes an invented CKL file
# and returns Success = $true. 'failure' -- Success = $false, no CKL written.
function Invoke-WaypointConvert {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string]$ConvertInputPath,
		[Parameter(Mandatory)] [string]$CklOutputPath,
		[Parameter()] [AllowNull()] [string]$BenchmarkId,
		[Parameter()] [AllowNull()] [string]$Title,
		[Parameter()] [AllowNull()] [string]$ReleaseInfo,
		[Parameter()] [AllowNull()] [string]$Version,
		[Parameter()] [int]$TimeoutSeconds,
		[Parameter()] [string]$VmwareStigDockerCommonPath
	)

	$Mode = $env:WAYPOINT_CONVERT_STUB_MODE
	if (-not $Mode) { $Mode = 'success' }

	if ($Mode -eq 'failure') {
		return [pscustomobject]@{ Success = $false; CklPath = $null; MetadataApplied = $false; FailureReason = 'invented SAF conversion failure (stub).' }
	}

	$CklDirectory = Split-Path -Path $CklOutputPath -Parent
	if ($CklDirectory -and -not (Test-Path -Path $CklDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $CklDirectory -Force | Out-Null
	}

	"<CHECKLIST><!-- invented stub CKL, benchmark=$BenchmarkId --></CHECKLIST>" | Set-Content -Path $CklOutputPath -Encoding utf8
	return [pscustomobject]@{ Success = $true; CklPath = $CklOutputPath; MetadataApplied = [bool]$BenchmarkId; FailureReason = $null }
}

# Invented stub for Invoke-WaypointNsxScan (issue #308). Same $env:WAYPOINT_SCAN_STUB_MODE
# canned outcomes as Invoke-WaypointScan above -- 'success' (default), 'exit100',
# 'failure', 'auth' -- kept on the SAME env var so a mixed vsphere+nsx run can be driven
# by one setting per test, plus its own Information-stream canary (password length) so
# the same non-leakage assertion covers this path too.
function Invoke-WaypointNsxScan {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Manager,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[int]$TimeoutSeconds
	)

	$InformationPreference = 'Continue'
	Write-Information "Scanning stub NSX manager '$Manager' as '$Username' (password length $($Password.Length)) profile '$ProfilePath'"

	$Mode = $env:WAYPOINT_SCAN_STUB_MODE
	if (-not $Mode) { $Mode = 'success' }

	if ($Mode -eq 'failure') {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = 1
			ReportPath    = $null
			FailureReason = 'invented NSX InSpec transport failure (stub).'
		}
	}

	if ($Mode -eq 'auth') {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = 2
			ReportPath    = $null
			FailureReason = '401 Unauthorized: invented NSX session token rejection (stub).'
		}
	}

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	$ExitCode = if ($Mode -eq 'exit100') { 100 } else { 0 }
	$Hdf = [ordered]@{
		platform = [ordered]@{ name = 'nsx'; release = 'stub' }
		profiles = @(
			[ordered]@{
				name    = 'invented-stub-nsx-profile'
				version = '0.0.0'
				controls = @()
			}
		)
		statistics = [ordered]@{ duration = 0.1 }
	}
	($Hdf | ConvertTo-Json -Depth 6) | Set-Content -Path $ReportPath -Encoding utf8

	Write-Information 'NSX scan complete.'

	return [pscustomobject]@{
		Success       = $true
		ExitCode      = $ExitCode
		ReportPath    = $ReportPath
		FailureReason = $null
	}
}

# Invented stub for Invoke-WaypointSrgScan (issue #309). Same $env:WAYPOINT_SCAN_STUB_MODE
# canned outcomes as the other transports -- 'success' (default), 'exit100', 'failure',
# 'auth' -- plus a Sudo/SudoRequiresPassword-observing Information line so a test can
# assert the handler passed sudo_enabled through correctly, and an
# inspec.lock-touching side effect so a test can assert stale-lock cleanup ran (the
# stub mirrors the real function's Remove-Item, marking cleanup by removing any lock it
# finds -- assertable by seeding one and observing it gone afterward).
function Invoke-WaypointSrgScan {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$SshHost,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[int]$TimeoutSeconds,

		[Parameter()]
		[bool]$Sudo = $false,

		[Parameter()]
		[bool]$SudoRequiresPassword = $true,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath
	)

	$InformationPreference = 'Continue'
	Write-Information "Scanning stub SRG host '$SshHost' as '$Username' (password length $($Password.Length)) profile '$ProfilePath' sudo=$Sudo sudoRequiresPassword=$SudoRequiresPassword"

	# Predecessor behavior: remove any stale inspec.lock next to the profile before
	# "running" -- mirrors the real function's cleanup so a test can seed a lock file and
	# assert it is gone after this stub returns.
	Remove-Item -Path (Join-Path $ProfilePath 'inspec.lock') -Force -ErrorAction SilentlyContinue

	$Mode = $env:WAYPOINT_SCAN_STUB_MODE
	if (-not $Mode) { $Mode = 'success' }

	if ($Mode -eq 'failure') {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = 1
			ReportPath    = $null
			FailureReason = 'invented SRG InSpec transport failure (stub).'
		}
	}

	if ($Mode -eq 'auth') {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = 2
			ReportPath    = $null
			FailureReason = '401 Unauthorized: invented SRG ssh credential rejection (stub).'
		}
	}

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	$ExitCode = if ($Mode -eq 'exit100') { 100 } else { 0 }
	$Hdf = [ordered]@{
		platform = [ordered]@{ name = 'srg'; release = 'stub' }
		profiles = @(
			[ordered]@{
				name    = 'invented-stub-srg-profile'
				version = '0.0.0'
				controls = @()
			}
		)
		statistics = [ordered]@{ duration = 0.1 }
	}
	($Hdf | ConvertTo-Json -Depth 6) | Set-Content -Path $ReportPath -Encoding utf8

	Write-Information 'SRG scan complete.'

	return [pscustomobject]@{
		Success       = $true
		ExitCode      = $ExitCode
		ReportPath    = $ReportPath
		FailureReason = $null
	}
}

Export-ModuleMember -Function Invoke-WaypointScan, Invoke-WaypointAttest, Invoke-WaypointConvert, Invoke-WaypointNsxScan, Invoke-WaypointSrgScan
