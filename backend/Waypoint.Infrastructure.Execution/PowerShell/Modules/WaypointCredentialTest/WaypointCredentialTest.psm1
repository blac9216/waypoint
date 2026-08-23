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

# Waypoint-owned shim (issue #245, epic #13), following WaypointDiscovery/WaypointScan's
# pattern (issues #21/#274/#308): this is the thin, Waypoint-authored seam that
# dot-sources the project-owned sibling repository's unmodified vmware-stig-docker
# transport files to open a real, read-only session
# per credential type and close it again -- no InSpec run, no scan, no state-changing
# call against the target.
#
#   vcenter -> Connect-StigVIServer / Disconnect-VIServer (module.transport.vmware.ps1),
#              the exact pair WaypointDiscovery already drives for discovery.
#   nsx     -> Get-NsxSessionToken (module.transport.nsxapi.ps1), the exact sibling-repo
#              function WaypointScan's Invoke-WaypointNsxScan already dot-sources for
#              the NSX scan path -- a session-token request IS the connectivity probe;
#              NSX's REST session has no separate "disconnect" call to make.
#   ssh     -> Test-TargetReachable (module.common.ps1): a TCP connect/close probe on
#              port 22. The sibling repo has no project primitive that opens an
#              authenticated SSH session on its own (SSH auth for SRG products is
#              delegated entirely to InSpec's own `ssh` transport at scan time) -- this
#              is deliberately honest about the boundary: a passing ssh test here proves
#              the host is reachable on port 22, not that the credential authenticates.
#              That limitation is documented on CredentialTestJobHandler and surfaced in
#              the job's success note.
#
# The two Get-LogSplat/Write-Log shims below are the same generic, non-secret-carrying
# helpers WaypointScan.psm1 defines for the identical reason: Connect-StigVIServer and
# Get-NsxSessionToken both call into the sibling repo's module.logging.ps1, which pulls
# in machinery (LogQueue thread, Write-LogDirect) this single-call test does not need,
# and that repo carries no LICENSE (CLAUDE.md's Borrowing Policy bars unlicensed code)
# -- so Waypoint provides tiny generic stand-ins rather than copying the sibling repo's
# logging stack, letting its connect functions run dot-sourced and unmodified.
if (-not (Get-Command -Name 'Get-LogSplat' -ErrorAction SilentlyContinue)) {
	function Get-LogSplat {
		param([Parameter(Position = 0)][string]$Source = 'CredentialTest')
		return @{ Source = $Source }
	}
}
if (-not (Get-Command -Name 'Write-Log' -ErrorAction SilentlyContinue)) {
	function Write-Log {
		[CmdletBinding()]
		param(
			[Parameter(Mandatory, Position = 0)][string]$Message,
			[Parameter()][string]$Severity = 'Info',
			[Parameter()][object]$LogQueue = $null,
			[Parameter()][string]$Source,
			[Parameter()][datetime]$Timestamp = (Get-Date)
		)
		Write-Verbose $Message
	}
}

$Script:VmwareStigDockerVmwareTransportPath = $env:WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH
$Script:VmwareStigDockerNsxApiModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_NSXAPI_PATH
$Script:VmwareStigDockerCommonModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH

function Invoke-WaypointVCenterCredentialTest {
	<#
	.SYNOPSIS
	    Opens a vCenter session (AllLinked) and closes it again -- no inventory walk,
	    no InSpec, nothing state-changing.

	.OUTPUTS
	    [pscustomobject]: Success (bool), FailureReason (string, only when Success is $false).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$VCenter,
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Username,
		[Parameter(Mandatory)][AllowEmptyString()][string]$Password,
		[Parameter()][string]$VmwareStigDockerTransportPath = $Script:VmwareStigDockerVmwareTransportPath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerTransportPath)) {
		throw 'WaypointCredentialTest: no module.transport.vmware.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH).'
	}
	if (-not (Test-Path -Path $VmwareStigDockerTransportPath -PathType Leaf)) {
		throw "WaypointCredentialTest: module.transport.vmware.ps1 not found at '$VmwareStigDockerTransportPath'."
	}

	. $VmwareStigDockerTransportPath

	$SecurePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
	$Credential = [System.Management.Automation.PSCredential]::new($Username, $SecurePassword)

	try {
		# Issue #580: testing a vSphere API credential is an API-only operation -- it
		# never opens a VCSA SSH session, so it must never resolve or prompt for a
		# VCSA credential. -SkipVCSACredential tells the shared Connect-StigVIServer
		# (module.transport.vmware.ps1, which carries this one small parameterization
		# -- see its README/NOTICE) to skip that resolution/prompt entirely rather than
		# falling into Get-Credential, which can never succeed in the noninteractive
		# compliance runner -- and a successful API connection must mark this test
		# successful regardless of any VCSA SSH binding's existence.
		$Connection = Connect-StigVIServer -VCenter $VCenter -VSphereCredential $Credential -SkipVCSACredential -Source 'CredentialTest'
		if ($Connection.DisconnectAtCleanup) {
			Disconnect-VIServer -Server $Connection.Sessions -Confirm:$false -ErrorAction SilentlyContinue
		}
		return [pscustomobject]@{ Success = $true; FailureReason = $null }
	} catch {
		return [pscustomobject]@{ Success = $false; FailureReason = $_.Exception.Message }
	}
}

function Invoke-WaypointNsxCredentialTest {
	<#
	.SYNOPSIS
	    Requests an NSX Manager session token -- the connectivity probe itself; NSX's
	    REST session has no separate close call.

	.OUTPUTS
	    [pscustomobject]: Success (bool), FailureReason (string, only when Success is $false).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Manager,
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Username,
		[Parameter(Mandatory)][AllowEmptyString()][string]$Password,
		[Parameter()][string]$VmwareStigDockerNsxApiPath = $Script:VmwareStigDockerNsxApiModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerNsxApiPath)) {
		throw 'WaypointCredentialTest: no module.transport.nsxapi.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_NSXAPI_PATH).'
	}
	if (-not (Test-Path -Path $VmwareStigDockerNsxApiPath -PathType Leaf)) {
		throw "WaypointCredentialTest: module.transport.nsxapi.ps1 not found at '$VmwareStigDockerNsxApiPath'."
	}

	. $VmwareStigDockerNsxApiPath

	$SecurePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
	$Credential = [System.Management.Automation.PSCredential]::new($Username, $SecurePassword)

	try {
		# The token/cookie returned here are never written to disk or a log stream --
		# this call only proves the credential authenticates; nothing downstream needs
		# the session, so it is discarded once Get-NsxSessionToken returns.
		$null = Get-NsxSessionToken -Manager $Manager -Credential $Credential -Source 'CredentialTest'
		return [pscustomobject]@{ Success = $true; FailureReason = $null }
	} catch {
		return [pscustomobject]@{ Success = $false; FailureReason = $_.Exception.Message }
	}
}

function Invoke-WaypointSshCredentialTest {
	<#
	.SYNOPSIS
	    TCP reachability probe on port 22 via the sibling repo's unmodified Test-TargetReachable
	    (module.common.ps1). Documented limitation: this proves the host is reachable,
	    not that the credential authenticates -- the sibling repo has no standalone
	    SSH-auth primitive of its own; SSH auth is delegated to InSpec's ssh transport
	    at actual scan time.

	.OUTPUTS
	    [pscustomobject]: Success (bool), FailureReason (string, only when Success is $false).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SshHost,
		[Parameter()][int]$Port = 22,
		[Parameter()][string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointCredentialTest: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH).'
	}
	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointCredentialTest: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	. $VmwareStigDockerCommonPath

	if (Test-TargetReachable -TargetHost $SshHost -Port $Port -Source 'CredentialTest') {
		return [pscustomobject]@{ Success = $true; FailureReason = $null }
	}

	return [pscustomobject]@{ Success = $false; FailureReason = "TCP connect to '${SshHost}:${Port}' failed or timed out." }
}

Export-ModuleMember -Function Invoke-WaypointVCenterCredentialTest, Invoke-WaypointNsxCredentialTest, Invoke-WaypointSshCredentialTest
