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
#
# Issue #865 adds the trailing discovery-meta completeness marker every pass now
# emits (mirroring the real Invoke-WaypointDiscovery's new output contract), plus a
# dedicated 'partial' pass:
#   'partial'   -- simulates a non-terminating per-subtree PowerCLI error (an
#                  unreachable ESXi host): reports only cluster + host-11 + vm-101
#                  (host-12 is dropped, as if its subtree failed) and a
#                  discovery-meta marker with Complete = $false and a fabricated
#                  error message. DiscoverJobHandler must upsert/refresh the seen
#                  objects but NOT mark host-12 removed/absent.
#
# Issue #995 adds a dedicated pass for the empty-string-Version regression:
#   'empty-version' -- a cluster with one host reporting a real semantic Version
#                  (matches migration 0064's seeded 'vsphere'/'8.0.3' row, so it can
#                  link) and one powered-off/disconnected host reporting Version as
#                  an EMPTY STRING ("", not $null -- exactly what a real
#                  powered-off/disconnected/connecting ESXi host returns). Before
#                  #995's fix, the empty-version host's "" slipped past
#                  ResolveCatalogLinkageAsync's `is null` guard and reached
#                  CatalogRepository's ArgumentException.ThrowIfNullOrWhiteSpace,
#                  aborting the WHOLE job -- so this pass proves one bad host no
#                  longer zeroes out the target's components.

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
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
		return
	}

	if ($Pass -eq '995-empty-version') {
		# Issue #995: one linkable host (real seeded semantic Version) plus one
		# powered-off/disconnected host reporting Version as an EMPTY STRING (never
		# $null) -- the exact shape a real vSphere host in that power/connection state
		# returns. No cluster/vm rows needed; this pass isolates the regression.
		#
		# Issue #1081: an unseeded vcenter identity/version -- present (so the root
		# component still gets vendor identity/version, matching every other pass) but
		# deliberately not matching any real catalog row, so this pass's own linkage
		# assertions stay about the two HOSTS, not the root.
		[pscustomobject]@{
			Type = 'vcenter'; MoRef = 'vcenter-instance-995'; Name = 'vcsa-01.example.internal'
			ParentMoRef = $null; Build = '00000000'; Version = '0.0.0-invented-unseeded'; MaintenanceMode = $null
		}
		[pscustomobject]@{
			Type = 'host'; MoRef = 'host-995-linkable'; Name = 'esxi-11.example.internal'
			ParentMoRef = $null; Build = '99.0.11111111'; Version = '8.0.3'; MaintenanceMode = $false
		}
		[pscustomobject]@{
			Type = 'host'; MoRef = 'host-995-empty-version'; Name = 'esxi-12.example.internal'
			ParentMoRef = $null; Build = '99.0.22222222'; Version = ''; MaintenanceMode = $false
		}
		Write-Information 'Discovery complete.'
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
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
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
		return
	}

	if ($Pass -eq '1081-linked') {
		# Issue #1081's own linkage/declared-service proof: an vcenter identity/version
		# that DOES match a real seeded catalog row (migration 0064's 'vsphere'/'8.0.3',
		# same seed DiscoverJobHandlerEndToEndTests already links host-11 against),
		# proving the whole chain discovery -> component fact -> catalog linkage ->
		# declared-service expansion actually fires for the vcenter root, the exact
		# gap epic #726's round-11 live validation found ("declared_services_upserted:
		# 0 ... never exercised against a real appliance").
		[pscustomobject]@{
			Type = 'vcenter'; MoRef = 'vcenter-instance-1081-linked'; Name = 'vcsa-01.example.internal'
			ParentMoRef = $null; Build = '99.0.87654321'; Version = '8.0.3'; MaintenanceMode = $null
		}
		Write-Information 'Discovery complete.'
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
		return
	}

	if ($Pass -eq '1063-bulk-derivation') {
		# Issue #1063's bulk-stamping/name-collision proof: a linked vcenter fact
		# (real seeded 'vsphere'/'8.0.3', same seed the '1081-linked' pass above
		# uses) plus TWO identically named VMs under it -- both must derive the
		# SAME version/build from the root and be recorded as distinct components
		# via their distinct InstanceUuid/MoRef despite the shared display name.
		[pscustomobject]@{
			Type = 'vcenter'; MoRef = 'vcenter-instance-1063-bulk'; Name = 'vcsa-01.example.internal'
			ParentMoRef = $null; Build = '99.0.87654321'; Version = '8.0.3'; MaintenanceMode = $null
		}
		[pscustomobject]@{
			Type = 'host'; MoRef = 'host-1063-bulk'; Name = 'esxi-22.example.internal'
			ParentMoRef = $null; Build = '99.0.88888888'; Version = '8.0.3'; MaintenanceMode = $false
		}
		[pscustomobject]@{
			Type = 'vm'; MoRef = 'vm-1063-bulk-a'; Name = 'duplicate-vm-name'
			ParentMoRef = 'host-1063-bulk'; Build = $null; Version = $null; MaintenanceMode = $null
			InstanceUuid = 'vm-instance-uuid-1063-bulk-a'
		}
		[pscustomobject]@{
			Type = 'vm'; MoRef = 'vm-1063-bulk-b'; Name = 'duplicate-vm-name'
			ParentMoRef = 'host-1063-bulk'; Build = $null; Version = $null; MaintenanceMode = $null
			InstanceUuid = 'vm-instance-uuid-1063-bulk-b'
		}
		Write-Information 'Discovery complete.'
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
		return
	}

	if ($Pass -eq '1063-no-parent-version') {
		# Issue #1063's honest-degradation case (issue #1115's exact-name-only
		# session-match miss, or any other boundary where the appliance's own
		# content.about could not be observed): no 'vcenter' row at all, matching
		# WaypointDiscovery.psm1's real fail-closed emission guard. A VM discovered
		# under this root must stay honestly version-absent -- never inherit a value
		# from a prior pass, never guess.
		[pscustomobject]@{
			Type = 'host'; MoRef = 'host-1063-no-parent'; Name = 'esxi-21.example.internal'
			ParentMoRef = $null; Build = '99.0.99999999'; Version = '8.0.3'; MaintenanceMode = $false
		}
		[pscustomobject]@{
			Type = 'vm'; MoRef = 'vm-1063-no-parent'; Name = 'stub-vm-21'
			ParentMoRef = 'host-1063-no-parent'; Build = $null; Version = $null; MaintenanceMode = $null
			InstanceUuid = 'vm-instance-uuid-1063-no-parent'
		}
		Write-Information 'Discovery complete.'
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
		return
	}

	# Issue #1081: the appliance's own identity/version, present on every pass below
	# this point (1, 2, partial) -- deliberately an UNSEEDED version (no real catalog
	# row matches it) so these general-purpose passes' component-count assertions stay
	# unaffected by catalog linkage/declared-service expansion; see the '1081-linked'
	# pass above for that proof.
	[pscustomobject]@{
		Type = 'vcenter'; MoRef = 'vcenter-instance-e2e'; Name = 'vcsa-01.example.internal'
		ParentMoRef = $null; Build = '00000000'; Version = '0.0.0-invented-unseeded'; MaintenanceMode = $null
	}

	[pscustomobject]@{
		Type = 'cluster'; MoRef = 'domain-c1'; Name = 'stub-cluster-01'
		ParentMoRef = $null; Build = $null; Version = $null; MaintenanceMode = $null
	}
	[pscustomobject]@{
		# Issue #974: invented Version distinct from Build, mirroring the real
		# $VMHost.Version/$VMHost.Build split -- host-11 is the "Version reported"
		# case, matched against migration 0064's real seeded 'vsphere'/'8.0.3' row by
		# DiscoverJobHandlerEndToEndTests.
		Type = 'host'; MoRef = 'host-11'; Name = 'esxi-01.example.internal'
		ParentMoRef = 'domain-c1'; Build = '99.0.12345678'; Version = '8.0.3'; MaintenanceMode = $false
	}
	[pscustomobject]@{
		# Issue #1063: InstanceUuid is a fabricated, migration-stable identifier
		# distinct from the MoRef -- DiscoverJobHandlerEndToEndTests asserts it lands
		# on the inventory_items row and that this VM's component fact is DERIVED
		# from the vcenter root above (root/vm here share the deliberately unseeded
		# '0.0.0-invented-unseeded' version -- see the general-purpose-pass comment
		# above this function).
		Type = 'vm'; MoRef = 'vm-101'; Name = 'stub-vm-01'
		ParentMoRef = 'host-11'; Build = '12345'; Version = $null; MaintenanceMode = $null
		InstanceUuid = 'vm-instance-uuid-101'
	}

	if ($Pass -eq '1' -or $Pass -eq 'partial') {
		# Present on pass 1 and (see below) reported as PRESENT going into a 'partial'
		# pass's prior state -- pass 2 (and 'partial' itself) omitting it from THIS
		# pass's own output is what the removal/absence-detection tests assert on: pass
		# 2 must mark it removed/absent (complete pass), 'partial' must NOT (incomplete
		# pass, ADR-0023 unverified cache).
		if ($Pass -eq '1') {
			[pscustomobject]@{
				# Issue #974: host-12 is the "Version unavailable this pass" case --
				# Build is still reported (retained fact), but Version is $null, so
				# DiscoverJobHandler.MapToComponents must resolve ExactVersion=null
				# (fail-closed) for this host rather than falling back to Build.
				Type = 'host'; MoRef = 'host-12'; Name = 'esxi-02.example.internal'
				ParentMoRef = 'domain-c1'; Build = '99.0.12345678'; Version = $null; MaintenanceMode = $true
			}
		}
	}

	Write-Information 'Discovery complete.'

	if ($Pass -eq 'partial') {
		# Issue #865: host-12's subtree "failed" (never enumerated this pass) --
		# Complete = $false is the explicit signal DiscoverJobHandler gates absence on.
		[pscustomobject]@{
			Type     = 'discovery-meta'
			Complete = $false
			Errors   = @("Host enumeration failed under cluster 'stub-cluster-01': invented-unreachable-esxi-02-error")
		}
	} else {
		[pscustomobject]@{ Type = 'discovery-meta'; Complete = $true; Errors = @() }
	}
}

Export-ModuleMember -Function Invoke-WaypointDiscovery
