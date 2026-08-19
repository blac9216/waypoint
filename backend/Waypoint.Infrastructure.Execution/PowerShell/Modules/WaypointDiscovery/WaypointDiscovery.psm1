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

# Waypoint-owned shim (issue #21, epic #13), following WaypointCatalogIndex's pattern
# (issue #194): this is the thin, Waypoint-authored seam that dot-sources the
# project-owned sibling repository's unmodified vmware-stig-docker
# module.transport.vmware.ps1 and
# adapts its Connect-StigVIServer/Get-StigTargets output to the flat
# cluster/host/vm shape DiscoverJobHandler needs.
#
# Only the discovery half of the sibling-repository module is used here (Connect-StigVIServer +
# a direct Get-View/Get-Cluster/Get-VMHost/Get-VM walk) -- Get-StigTargets itself
# builds InSpec-scan targets (profile paths, report dirs), which a pure inventory
# enumeration does not require. Re-implementing the cluster/host/VM walk directly
# against PowerCLI here (rather than trying to coerce Get-StigTargets' scan-target
# shape into an inventory tree) keeps this module honest about what it actually needs
# from PowerCLI: object identity (MoRef), name, parent, build, and (for hosts)
# maintenance mode.

$Script:VmwareStigDockerModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH

function Invoke-WaypointDiscovery {
	<#
	.SYNOPSIS
	    Connects to a vCenter (AllLinked) and enumerates clusters/hosts/VMs into the
	    flat item list DiscoverJobHandler upserts into inventory_items.

	.PARAMETER VCenter
	    FQDN or IP of the vCenter Server (target.connection.host).

	.PARAMETER Username
	    vSphere SSO username, decrypted credential username half.

	.PARAMETER Password
	    vSphere SSO password, bound as a typed parameter -- never interpolated into
	    script text (security.md controls 1/2).

	.PARAMETER VmwareStigDockerTransportPath
	    Path to the sibling repo's unmodified module.transport.vmware.ps1, dot-sourced
	    to bring Connect-StigVIServer into scope.

	.OUTPUTS
	    One [pscustomobject] per discovered item: Type ('cluster'|'host'|'vm'), MoRef,
	    Name, ParentMoRef (nullable), Build (nullable), MaintenanceMode (nullable
	    bool). Clusters and hosts are emitted before their children.
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

		[Parameter()]
		[string]$VmwareStigDockerTransportPath = $Script:VmwareStigDockerModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerTransportPath)) {
		throw 'WaypointDiscovery: no module.transport.vmware.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH or -VmwareStigDockerTransportPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerTransportPath -PathType Leaf)) {
		throw "WaypointDiscovery: module.transport.vmware.ps1 not found at '$VmwareStigDockerTransportPath'."
	}

	# Dot-source the unmodified sibling-repository script to bring Connect-StigVIServer into scope.
	. $VmwareStigDockerTransportPath

	$SecurePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
	$Credential = [pscredential]::new($Username, $SecurePassword)

	Write-Information "Connecting to vCenter '$VCenter' for discovery..."
	$Connection = Connect-StigVIServer -VCenter $VCenter -VSphereCredential $Credential -Source 'Discovery'

	try {
		foreach ($Session in $Connection.Sessions) {
			foreach ($Cluster in (Get-Cluster -Server $Session)) {
				[pscustomobject]@{
					Type            = 'cluster'
					MoRef           = $Cluster.ExtensionData.MoRef.Value
					Name            = $Cluster.Name
					ParentMoRef     = $null
					Build           = $null
					MaintenanceMode = $null
				}

				foreach ($VMHost in (Get-VMHost -Server $Session -Location $Cluster)) {
					[pscustomobject]@{
						Type            = 'host'
						MoRef           = $VMHost.ExtensionData.MoRef.Value
						Name            = $VMHost.Name
						ParentMoRef     = $Cluster.ExtensionData.MoRef.Value
						Build           = $VMHost.Build
						MaintenanceMode = ($VMHost.ConnectionState -eq 'Maintenance')
					}

					foreach ($VM in (Get-VM -Server $Session -Location $VMHost)) {
						[pscustomobject]@{
							Type            = 'vm'
							MoRef           = $VM.ExtensionData.MoRef.Value
							Name            = $VM.Name
							ParentMoRef     = $VMHost.ExtensionData.MoRef.Value
							Build           = $VM.Guest.ToolsVersion
							MaintenanceMode = $null
						}
					}
				}
			}

			# Hosts with no cluster (standalone) belong directly to the datacenter --
			# emitted with a null ParentMoRef so they appear at the tree's top level,
			# same as a cluster does.
			foreach ($VMHost in (Get-VMHost -Server $Session | Where-Object { -not $_.Parent -or $_.Parent -isnot [VMware.VimAutomation.ViCore.Types.V1.Inventory.Cluster] })) {
				[pscustomobject]@{
					Type            = 'host'
					MoRef           = $VMHost.ExtensionData.MoRef.Value
					Name            = $VMHost.Name
					ParentMoRef     = $null
					Build           = $VMHost.Build
					MaintenanceMode = ($VMHost.ConnectionState -eq 'Maintenance')
				}

				foreach ($VM in (Get-VM -Server $Session -Location $VMHost)) {
					[pscustomobject]@{
						Type            = 'vm'
						MoRef           = $VM.ExtensionData.MoRef.Value
						Name            = $VM.Name
						ParentMoRef     = $VMHost.ExtensionData.MoRef.Value
						Build           = $VM.Guest.ToolsVersion
						MaintenanceMode = $null
					}
				}
			}
		}
	} finally {
		if ($Connection.DisconnectAtCleanup) {
			Write-Information "Disconnecting from vCenter '$VCenter' after discovery."
			Disconnect-VIServer -Server $Connection.Sessions -Confirm:$false -ErrorAction SilentlyContinue
		}
	}
}

Export-ModuleMember -Function Invoke-WaypointDiscovery
