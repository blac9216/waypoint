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

Export-ModuleMember -Function Invoke-WaypointScan
