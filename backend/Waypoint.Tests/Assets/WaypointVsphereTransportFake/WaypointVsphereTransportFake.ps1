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

# Invented fake for issue #580's noninteractive regression coverage. This file stands
# in for the sibling repo's module.transport.vmware.ps1 -- it is dot-sourced by the
# REAL (unmodified except for the #580 -SkipVCSACredential switch)
# WaypointDiscovery.psm1 / WaypointCredentialTest.psm1 wrappers via
# $env:WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH, exactly as the real transport file
# would be. No vendor code, no PowerCLI, no real vCenter -- everything here is
# fabricated (fictional example.internal hosts only, per CLAUDE.md's sanitization
# gate).
#
# Its Connect-StigVIServer mirrors the real function's -SkipVCSACredential contract
# closely enough to pin the noninteractive fix at the wrapper boundary:
#   - Get-Credential always throws: if either wrapper's call ever reaches the
#     prompt fallback (missing -SkipVCSACredential, or a caller passing $false),
#     the test fails loudly instead of hanging.
#   - -SkipVCSACredential always returns a $null VCSACredential and never touches
#     Get-Credential, matching the real fix.
#   - Omitting -SkipVCSACredential (or passing $false) with no -VCSACredential
#     still prompts (falls into the throwing Get-Credential) -- this fake does not
#     weaken that path, so a regression that drops the switch from either wrapper
#     is caught the same way a real noninteractive host would catch it (loudly,
#     not silently).
#
# Issue #579: Connect-StigVIServer also calls Get-LogSplat/Write-Log exactly like the
# real transport does (see below) -- these tests only pass today because the shared
# WaypointLogging adapter module is preloaded ahead of this fake; a missing or broken
# adapter fails every test in this fixture with a missing-command error, the same
# failure mode #579 fixes for real discovery runs.
#   - A blank -VSphereCredential Username (this fake's signal for "simulate no
#     vSphere API credential supplied") makes Connect-VIServer throw an actionable,
#     precise configuration error instead of connecting -- pinning the "missing
#     vSphere credential fails fast, not hangs" half of the contract.

function Get-Credential {
	param([string]$Message, [string]$UserName)
	throw "WaypointVsphereTransportFake: Get-Credential invoked ('$Message') -- a noninteractive host can never satisfy this prompt."
}

function Connect-VIServer {
	param(
		[Parameter(Mandatory)][string]$Server,
		[switch]$AllLinked,
		[Parameter(Mandatory)][pscredential]$Credential,
		[string]$Protocol
	)

	if ([string]::IsNullOrEmpty($Credential.UserName)) {
		throw "WaypointVsphereTransportFake: no vSphere API credential supplied for '$Server'."
	}

	# Issue #1081 (round-1 review, major 3): an invented test knob so a test can pin
	# the SHIPPED module's vcenter-row emission against session shapes a real
	# appliance can produce -- a blank/absent instanceUuid, or several -AllLinked
	# sibling sessions none of which match the requested -VCenter by name. Unset (the
	# default), this behaves exactly as before: one session named after the server.
	# The default now also carries an InstanceUuid/Version/Build, because a real
	# session always does and the emission guard is only meaningful against a fake
	# that can supply one.
	if ($null -ne $Global:WaypointVsphereTransportFakeSessions) {
		return @($Global:WaypointVsphereTransportFakeSessions)
	}

	return @([pscustomobject]@{
		Name         = $Server
		InstanceUuid = 'vcenter-instance-fake-0001'
		Version      = '0.0.0-invented-unseeded'
		Build        = 'invented-build-0001'
	})
}

function Disconnect-VIServer {
	param($Server, [switch]$Confirm, $ErrorAction)
	$Script:WaypointVsphereTransportFakeDisconnectCount = ($Script:WaypointVsphereTransportFakeDisconnectCount ?? 0) + 1
}

# Invoke-WaypointDiscovery walks Get-Cluster/Get-VMHost/Get-VM after connecting;
# an empty inventory is sufficient to pin this issue's noninteractive contract (the
# fix is about what happens BEFORE/DURING connect, not what discovery enumerates).
# Issue #1081 (round-1 review, major 3): $Global:WaypointVsphereTransportFakeClusters
# lets a test put ONE ordinary cluster into the walk, so "a session that cannot supply
# an identity suppresses only the vcenter row and leaves the rest of the pass intact"
# is provable rather than vacuous against an empty inventory. Unset, unchanged.
function Get-Cluster {
	param($Server)
	if ($null -ne $Global:WaypointVsphereTransportFakeClusters) {
		return @($Global:WaypointVsphereTransportFakeClusters)
	}

	return @()
}
function Get-VMHost { param($Server, $Location); return @() }
function Get-VM { param($Server, $Location); return @() }

function Connect-StigVIServer {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$VCenter,

		[Parameter()]
		[pscredential]$VSphereCredential,

		[Parameter()]
		[pscredential]$VCSACredential,

		[Parameter()]
		[switch]$SkipVCSACredential,

		[Parameter()]
		[string]$Source = 'vSphere'
	)

	$DisconnectAtCleanup = $false

	# Issue #579 regression coverage: the real Connect-StigVIServer calls
	# Get-LogSplat/Write-Log exactly like this (build a -Source splat once, then
	# splat it into every Write-Log call) -- if the shared WaypointLogging adapter
	# were missing or broken, these two lines would throw "the term ... is not
	# recognized" the same way the real transport does, failing every test in this
	# fixture loudly instead of silently passing without ever exercising the
	# contract.
	$WriteLogParams = Get-LogSplat $Source
	Write-Log "Connecting to vCenter '$VCenter' and all linked vCenters..." -Severity 'Info' @WriteLogParams

	if (@($Global:DefaultVIServers | Where-Object { $_.Name -eq $VCenter }).Count -gt 0) {
		$VISessions = $Global:DefaultVIServers
	} else {
		if ($null -eq $VSphereCredential) {
			$VSphereCredential = Get-Credential -Message "Enter vSphere SSO credentials"
		}

		if ($SkipVCSACredential) {
			$VCSACredential = $null
		} elseif ($null -eq $VCSACredential) {
			$VCSACredential = Get-Credential -Message "Enter VCSA root SSH credentials" -UserName "root"
		}

		$VISessions = Connect-VIServer -Server $VCenter -AllLinked -Credential $VSphereCredential -Protocol https
		$DisconnectAtCleanup = $true
	}

	Write-Log "Successfully connected to '$VCenter'" -Severity 'Success' @WriteLogParams

	return [PSCustomObject]@{
		Sessions            = $VISessions
		VSphereCredential   = $VSphereCredential
		VCSACredential      = $VCSACredential
		DisconnectAtCleanup = $DisconnectAtCleanup
	}
}
