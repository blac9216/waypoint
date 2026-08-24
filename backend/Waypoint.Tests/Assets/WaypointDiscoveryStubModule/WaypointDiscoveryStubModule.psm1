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

# Invented stub for the discover full-loop integration test (issue #21). Mirrors
# Invoke-WaypointDiscovery's real signature and output shape without touching the
# sibling repo, PowerCLI, or a real vCenter -- no vendor code, no real hostnames or
# credentials, everything here is fabricated (fictional example.internal hosts only,
# per CLAUDE.md's sanitization gate).
#
# $Script:WaypointDiscoveryStubState lets the test drive two successive discovery
# passes with different results (a removal-detection scenario: pass 2 omits an item
# pass 1 reported) by setting $env:WAYPOINT_DISCOVERY_STUB_PASS before each
# invocation -- '1' (default) or '2'.
#
# Issue #618 adds two more passes for the fail-closed-on-malformed-output contract:
#   'empty'     -- a vCenter with genuinely zero inventory: emits nothing at all.
#                  This must still be a clean success (0 items, 0 removed), the
#                  legitimate case ParseDiscoveredItems' malformed-row check must not
#                  punish.
#   'malformed' -- simulates the executor's output-capture path losing a
#                  pscustomobject's NoteProperties (the live-vCenter reproduction in
#                  #618): emits PSObjects with no Type/MoRef/Name at all. This must
#                  fail the job instead of reporting "Discovered 0 item(s)".

function Invoke-WaypointDiscovery {
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

		[Parameter()]
		[string]$VmwareStigDockerTransportPath
	)

	# Deliberately touches the Information stream, exactly like
	# WaypointCatalogIndexStubModule -- if the decrypted password ever leaked into
	# this handler's invocation, the canary test would catch it here.
	$InformationPreference = 'Continue'
	Write-Information "Connecting to stub vCenter '$VCenter' as '$Username' (password length $($Password.Length))"

	$Pass = $env:WAYPOINT_DISCOVERY_STUB_PASS
	if (-not $Pass) { $Pass = '1' }

	if ($Pass -eq 'empty') {
		Write-Information 'Discovery complete.'
		return
	}

	if ($Pass -eq 'malformed') {
		# Stands in for the executor losing a pscustomobject's NoteProperties: these
		# rows arrive as PSObjects but read back with none of Type/MoRef/Name set, the
		# exact shape DiscoverJobHandler.TryParseItem must reject as unparseable rather
		# than silently drop.
		[pscustomobject]@{}
		[pscustomobject]@{}
		Write-Information 'Discovery complete.'
		return
	}

	[pscustomobject]@{
		Type = 'cluster'; MoRef = 'domain-c1'; Name = 'stub-cluster-01'
		ParentMoRef = $null; Build = $null; MaintenanceMode = $null
	}
	[pscustomobject]@{
		Type = 'host'; MoRef = 'host-11'; Name = 'esxi-01.example.internal'
		ParentMoRef = 'domain-c1'; Build = '99.0.12345678'; MaintenanceMode = $false
	}
	[pscustomobject]@{
		Type = 'vm'; MoRef = 'vm-101'; Name = 'stub-vm-01'
		ParentMoRef = 'host-11'; Build = '12345'; MaintenanceMode = $null
	}

	if ($Pass -eq '1') {
		# Only present on pass 1 -- pass 2 omitting it is what the removal-detection
		# test asserts gets marked removed_at.
		[pscustomobject]@{
			Type = 'host'; MoRef = 'host-12'; Name = 'esxi-02.example.internal'
			ParentMoRef = 'domain-c1'; Build = '99.0.12345678'; MaintenanceMode = $true
		}
	}

	Write-Information 'Discovery complete.'
}

Export-ModuleMember -Function Invoke-WaypointDiscovery
