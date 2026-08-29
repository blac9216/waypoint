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
	    One [pscustomobject] per discovered item: Type ('vcenter'|'cluster'|'host'|'vm'),
	    MoRef, Name, ParentMoRef (nullable), Build (nullable), Version (nullable),
	    MaintenanceMode (nullable bool), InstanceUuid (nullable, issue #1063, 'vm' rows
	    only). The 'vcenter' row (issue #1081) is always first; clusters and hosts are
	    emitted before their children.

	    Issue #974: Version is the host's semantic vSphere product version
	    ($VMHost.Version, e.g. "8.0.3") -- populated only for 'host' rows, alongside
	    (never instead of) Build, which continues to be captured/reported exactly as
	    before. 'cluster'/'vm' rows always report Version = $null.

	    Issue #1081: the 'vcenter' row reports the appliance's own identity/version --
	    MoRef is $Session.InstanceUuid (vSphere's `content.about.instanceUuid`, the
	    only authoritative, stable identifier the appliance itself exposes -- the same
	    property module.targets.ps1 already keys linked-vCenter claims on), Version/
	    Build are $Session.Version/.Build (`content.about.version`/`.build`). Emitted
	    for the ONE session matching the requested -VCenter (never every AllLinked
	    sibling, which would misattribute another vCenter's identity to this target's
	    root), and ONLY when that session actually supplies a non-blank InstanceUuid
	    and Name. When it cannot -- or when no session matches by name and more than
	    one linked session makes "which one is ours" a guess -- no 'vcenter' row is
	    emitted at all: the root stays honestly identity- and version-absent and the
	    rest of the pass is unaffected, rather than a blank MoRef/Name failing the
	    whole discovery job through TryParseItem/ParseDiscoveredItems (#618).
	    A 'vm' row's Build is always $null -- it used to carry the VMware Tools
	    version, which is not a product-version fact for the VM itself and would
	    mislead anything reading Build as a platform fact; the VM's own platform
	    version fact is derived from its parent vcenter row by
	    DiscoverJobHandler.MapToComponents (issue #1063), never reported here.

	    Issue #1063: a 'vm' row's InstanceUuid is vSphere's
	    `Config.InstanceUuid` -- the appliance-assigned, migration-stable identifier
	    that deconflicts identically named VMs across discovery passes. $null for
	    every non-'vm' row.

	    Issue #865 (ADR-0023 completeness gap): the default $ErrorActionPreference is
	    'Continue', so a non-terminating PowerCLI error on one subtree (an unreachable
	    ESXi host, a permission-denied cluster) does not stop the pipeline -- it emits
	    whatever it COULD enumerate and returns, which used to look identical to a
	    genuinely complete pass. This function now wraps each subtree walk (per-cluster
	    and the standalone-host sweep) in its own try/catch with
	    -ErrorAction Stop scoped to just that walk, so a subtree failure is caught
	    locally (its objects are simply skipped) rather than left as an ambient
	    non-terminating error the caller can only infer from HadErrors. The walk emits
	    exactly one trailing completeness-marker record --
	    Type = 'discovery-meta', Complete (bool), Errors (string[]) -- as the LAST
	    output object, giving the C# side an explicit, structured completeness signal
	    instead of a heuristic over HadErrors/streams.
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
	# Issue #580: discovery is an API-only operation -- it never walks VCSA over SSH,
	# so it must never resolve or prompt for a VCSA credential. -SkipVCSACredential
	# tells the shared Connect-StigVIServer (module.transport.vmware.ps1, which
	# carries this one small parameterization -- see its README/NOTICE) to skip that
	# resolution/prompt entirely rather than falling into Get-Credential, which can
	# never succeed in the noninteractive compliance runner.
	$Connection = Connect-StigVIServer -VCenter $VCenter -VSphereCredential $Credential -SkipVCSACredential -Source 'Discovery'

	# Issue #1081: the appliance's own identity/version fact. $Session.InstanceUuid is
	# vSphere's authoritative, stable instance identifier (content.about.instanceUuid)
	# -- the same property module.targets.ps1 already keys linked-vCenter claims on --
	# and $Session.Version/.Build are the appliance's own semantic version/build
	# (content.about.version/.build), never the ESXi hosts' values. Only the ONE
	# session matching the requested -VCenter is used: -AllLinked can return sibling
	# vCenters too, and attributing one of THEIR identities to THIS target's root would
	# be exactly the guessed-identity failure ADR-0023 forbids.
	#
	# Issue #1081 (round-1 review, major 3): the row is emitted ONLY when the identity
	# and name are both actually present, and it is the LAST thing decided rather than
	# an unconditional emission. Two reasons, both fail-closed:
	#
	#   * TryParseItem rejects a blank MoRef/Name and ParseDiscoveredItems counts that
	#     as malformed, which DiscoverJobHandler turns into a FAILED discovery job
	#     (issue #618's no-silent-success rule). Emitting the row unconditionally
	#     therefore meant an appliance that reported a blank instanceUuid would take
	#     the WHOLE pass down -- cluster/host/VM inventory included -- rather than
	#     leaving the root honestly identity-less. Epic #726 section 3 wants an absent
	#     fact to be absent and the component skipped, never a failed pass, so a
	#     $PrimarySession that cannot supply both fields yields NO vcenter row at all
	#     (which the mapper already handles: the root simply stays identity- and
	#     version-absent, and everything else about the pass is unaffected).
	#
	#   * When no session matches the requested -VCenter by name (e.g. -VCenter given
	#     as an IP while the session reports its FQDN), a single session is still
	#     unambiguously THIS target's appliance and is used. More than one session and
	#     no name match is a genuine ambiguity under -AllLinked -- "first session" would
	#     be a guess at which linked vCenter is ours -- so nothing is emitted instead.
	$PrimarySession = @($Connection.Sessions | Where-Object { $_.Name -eq $VCenter }) | Select-Object -First 1
	if (-not $PrimarySession) {
		$UnmatchedSessions = @($Connection.Sessions)
		if ($UnmatchedSessions.Count -eq 1) {
			$PrimarySession = $UnmatchedSessions[0]
		}
	}

	if ($PrimarySession -and -not [string]::IsNullOrWhiteSpace($PrimarySession.InstanceUuid) -and -not [string]::IsNullOrWhiteSpace($PrimarySession.Name)) {
		[pscustomobject]@{
			Type            = 'vcenter'
			MoRef           = $PrimarySession.InstanceUuid
			Name            = $PrimarySession.Name
			ParentMoRef     = $null
			Build           = $PrimarySession.Build
			Version         = $PrimarySession.Version
			MaintenanceMode = $null
		}
	}

	# Issue #865: collected per-subtree failure messages, surfaced verbatim (already
	# PowerCLI's own text, no secrets ever flow through this path) in the trailing
	# discovery-meta record so DiscoverJobHandler can raise them as the pass's
	# partial-failure diagnostic rather than the caller having to re-derive "why" from
	# HadErrors/streams alone.
	$EnumerationErrors = [System.Collections.Generic.List[string]]::new()

	try {
		foreach ($Session in $Connection.Sessions) {
			foreach ($Cluster in (Get-Cluster -Server $Session)) {
				[pscustomobject]@{
					Type            = 'cluster'
					MoRef           = $Cluster.ExtensionData.MoRef.Value
					Name            = $Cluster.Name
					ParentMoRef     = $null
					Build           = $null
					Version         = $null
					MaintenanceMode = $null
				}

				# Each cluster's host/VM walk is its own completeness boundary: a
				# permission-denied or unreachable subtree under ONE cluster must not
				# silently look identical to a fully-enumerated pass. -ErrorAction Stop
				# turns what PowerCLI would otherwise report as a non-terminating error
				# (pipeline keeps going, HadErrors set) into a terminating one scoped to
				# just this try, caught here and recorded -- the rest of the vCenter is
				# still walked.
				try {
					foreach ($VMHost in (Get-VMHost -Server $Session -Location $Cluster -ErrorAction Stop)) {
						[pscustomobject]@{
							Type            = 'host'
							MoRef           = $VMHost.ExtensionData.MoRef.Value
							Name            = $VMHost.Name
							ParentMoRef     = $Cluster.ExtensionData.MoRef.Value
							Build           = $VMHost.Build
							Version         = $VMHost.Version
							MaintenanceMode = ($VMHost.ConnectionState -eq 'Maintenance')
						}

						try {
							foreach ($VM in (Get-VM -Server $Session -Location $VMHost -ErrorAction Stop)) {
								[pscustomobject]@{
									Type            = 'vm'
									MoRef           = $VM.ExtensionData.MoRef.Value
									Name            = $VM.Name
									ParentMoRef     = $VMHost.ExtensionData.MoRef.Value
									# Issue #1081: never the VMware Tools version -- not a product-version
									# fact for the VM itself; would mislead anything reading Build as a
									# platform fact. The VM's own platform version is derived from its
									# parent vcenter's fact by DiscoverJobHandler.MapToComponents (#1063),
									# never reported here.
									Build           = $null
									Version         = $null
									# Issue #1063: the VM's authoritative, migration-stable vSphere instance
									# UUID -- deconflicts identically named VMs across discovery passes.
									# $VM.ExtensionData.Config can legitimately be $null for some VM states
									# (e.g. an invalid/orphaned object); property access on $null returns
									# $null here rather than throwing (no Set-StrictMode in this module).
									InstanceUuid    = $VM.ExtensionData.Config.InstanceUuid
									MaintenanceMode = $null
								}
							}
						} catch {
							$EnumerationErrors.Add("VM enumeration failed under host '$($VMHost.Name)' (cluster '$($Cluster.Name)'): $($_.Exception.Message)")
						}
					}
				} catch {
					$EnumerationErrors.Add("Host enumeration failed under cluster '$($Cluster.Name)': $($_.Exception.Message)")
				}
			}

			# Hosts with no cluster (standalone) belong directly to the datacenter --
			# emitted with a null ParentMoRef so they appear at the tree's top level,
			# same as a cluster does. Same per-subtree completeness boundary as above.
			try {
				foreach ($VMHost in (Get-VMHost -Server $Session -ErrorAction Stop | Where-Object { -not $_.Parent -or $_.Parent -isnot [VMware.VimAutomation.ViCore.Types.V1.Inventory.Cluster] })) {
					[pscustomobject]@{
						Type            = 'host'
						MoRef           = $VMHost.ExtensionData.MoRef.Value
						Name            = $VMHost.Name
						ParentMoRef     = $null
						Build           = $VMHost.Build
						Version         = $VMHost.Version
						MaintenanceMode = ($VMHost.ConnectionState -eq 'Maintenance')
					}

					try {
						foreach ($VM in (Get-VM -Server $Session -Location $VMHost -ErrorAction Stop)) {
							[pscustomobject]@{
								Type            = 'vm'
								MoRef           = $VM.ExtensionData.MoRef.Value
								Name            = $VM.Name
								ParentMoRef     = $VMHost.ExtensionData.MoRef.Value
								# Issue #1081: never the VMware Tools version -- see comment above.
								Build           = $null
								Version         = $null
								# Issue #1063: see comment above.
								InstanceUuid    = $VM.ExtensionData.Config.InstanceUuid
								MaintenanceMode = $null
							}
						}
					} catch {
						$EnumerationErrors.Add("VM enumeration failed under standalone host '$($VMHost.Name)': $($_.Exception.Message)")
					}
				}
			} catch {
				$EnumerationErrors.Add("Standalone host enumeration failed: $($_.Exception.Message)")
			}
		}
	} finally {
		if ($Connection.DisconnectAtCleanup) {
			Write-Information "Disconnecting from vCenter '$VCenter' after discovery."
			Disconnect-VIServer -Server $Connection.Sessions -Confirm:$false -ErrorAction SilentlyContinue
		}
	}

	# Issue #865: the explicit completeness signal. Always the LAST object emitted, so
	# DiscoverJobHandler can pull it off the end of the output list without scanning:
	# Complete = $false whenever ANY subtree above was skipped due to a caught error --
	# a genuinely empty vCenter (no clusters, no standalone hosts, zero errors) still
	# reports Complete = $true, matching the "genuinely empty" success case #618
	# already established for the item list itself.
	[pscustomobject]@{
		Type     = 'discovery-meta'
		Complete = ($EnumerationErrors.Count -eq 0)
		Errors   = $EnumerationErrors.ToArray()
	}
}

Export-ModuleMember -Function Invoke-WaypointDiscovery
