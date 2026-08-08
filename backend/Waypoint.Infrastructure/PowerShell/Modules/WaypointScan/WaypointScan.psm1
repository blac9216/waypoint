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

# Waypoint-owned shim (issue #274, second slice of the #23 split), following
# WaypointDiscovery's pattern (issue #21): this module is NOT vendor code -- it is the
# thin, Waypoint-authored seam that dot-sources the UNMODIFIED vmware-stig-docker
# module.common.ps1 (CLAUDE.md: "the sibling repos' vendor scripts run as unmodified
# vendor code -- Waypoint orchestrates them, it does not fork them") to reuse its
# Invoke-ExternalCommand process-capture helper, then drives `inspec exec` against a
# single vSphere target the same way module.transport.vmware.ps1's
# Build-VsphereTransportTargets does (VISERVER/VISERVER_USERNAME/VISERVER_PASSWORD env,
# --reporter=json:<path>, --enhanced-outcomes, AllowedExitCodes 0/100/101).
#
# This is deliberately a single-target, vSphere-only invocation -- the vendor's
# Get-ScanScriptBlock is coupled to its parallel engine's $Target object (built by
# Build-VsphereTransportTargets from a whole-site connect) and to multi-transport
# branching (ssh/vcsa-component/srg) this M1 slice does not need. Re-driving `inspec`
# directly here, the same way WaypointDiscovery re-drives PowerCLI directly instead of
# forcing Get-StigTargets' scan-target shape, keeps this module honest about exactly
# what it needs from the vendor code: one process-capture helper.

$Script:VmwareStigDockerCommonModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH

function Invoke-WaypointScan {
	<#
	.SYNOPSIS
	    Runs `inspec exec` against a single vSphere target and returns the HDF report
	    path plus outcome.

	.PARAMETER VCenter
	    FQDN or IP of the vCenter Server (target.connection.host).

	.PARAMETER Username
	    vSphere SSO username, decrypted credential username half.

	.PARAMETER Password
	    vSphere SSO password, bound as a typed parameter -- never interpolated into
	    script text (security.md controls 1/2).

	.PARAMETER ProfilePath
	    Path to the InSpec profile (compliance content) to execute.

	.PARAMETER ReportPath
	    Where InSpec writes its JSON (HDF) report.

	.PARAMETER VmwareStigDockerCommonPath
	    Path to the sibling repo's module.common.ps1 (unmodified vendor code),
	    dot-sourced to bring Invoke-ExternalCommand into scope.

	.OUTPUTS
	    One [pscustomobject]: Success (bool), ExitCode (int), ReportPath (string),
	    FailureReason (string, only set when Success is $false).
	#>
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
		[int]$TimeoutSeconds = 1800,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	# Dot-source the unmodified vendor script to bring Invoke-ExternalCommand into scope.
	. $VmwareStigDockerCommonPath

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	# Same env-export shape module.scan.ps1 uses for vmware:// transport targets: InSpec
	# (via train-vmware) reads these three, never argv, so the password never touches
	# the process table (issue #142's constraint, honored here too).
	$EnvVars = @{
		'NO_COLOR'          = $true
		'VISERVER'          = $VCenter
		'VISERVER_USERNAME' = $Username
		'VISERVER_PASSWORD' = $Password
	}

	$InspecArguments = "`"$ProfilePath`" -t vmware:// --reporter=json:`"$ReportPath`" --show-progress --enhanced-outcomes"

	try {
		# AllowedExitCodes 0/100/101 mirrors module.scan.ps1's own call: InSpec's exit
		# codes 100 (compliance failures present) and 101 (skipped controls present) are
		# BOTH a completed, reportable scan, not a tool failure -- only a code outside
		# this set (crash, argument error, transport failure) is InSpec itself failing.
		$null = Invoke-ExternalCommand -Executable 'inspec' -Arguments "exec $InspecArguments" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName "InSpec scan for $VCenter" `
			-EnvironmentVars $EnvVars -AllowedExitCodes @(0, 100, 101) -Source 'vsphere' -SurfaceOutputOnFailure

		if (-not (Test-Path -Path $ReportPath -PathType Leaf)) {
			return [pscustomobject]@{
				Success       = $false
				ExitCode      = $null
				ReportPath    = $null
				FailureReason = "InSpec scan completed but report file not found at $ReportPath."
			}
		}

		return [pscustomobject]@{
			Success       = $true
			ExitCode      = 0
			ReportPath    = $ReportPath
			FailureReason = $null
		}
	} catch {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "InSpec scan failed for $VCenter`: $($_.Exception.Message)"
		}
	}
}

Export-ModuleMember -Function Invoke-WaypointScan
