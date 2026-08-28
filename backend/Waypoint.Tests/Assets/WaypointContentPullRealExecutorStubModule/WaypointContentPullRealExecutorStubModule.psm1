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

# Issue #972 end-to-end fixture: reuses the REAL WaypointComplianceContent.psm1's own
# Get-WaypointComplianceContentProfiles / Get-WaypointComplianceContentRawManifest /
# Get-WaypointComplianceContentControlFileNames / Get-WaypointComplianceContentControls
# helpers unmodified (they are not the suspect -- issue #972 proved those already work),
# and only replaces Sync-WaypointComplianceContentTree's git clone/fetch/checkout steps
# with a no-op over a pre-built invented fixture tree, so this drives the SAME assembly
# path through the SAME real PowerShellExecutor/WaypointRunspacePool the runner uses --
# without a real git remote or the real (unavailable, licensed-tool-adjacent) inspec
# binary.
#
# Issue #984: InspecCheckRan/Passed call the REAL WaypointComplianceContent.psm1
# Test-WaypointInspecCheck (not a hand-stubbed true) for controls/-bearing profiles.
#
# Issue #993: the real module split into Sync-WaypointComplianceContentTree (phase 1:
# git + enumerate) and Get-WaypointComplianceContentEntries (phase 2: chunked, bounded
# per-leaf checks) so ContentPullJobHandler could bound each PowerShell invocation to
# its own chunk's worst case instead of the whole tree. This stub mirrors that same
# two-function split -- ContentPullJobHandler issues both commands by their REAL names
# now (Sync-WaypointComplianceContentTree / Get-WaypointComplianceContentEntries), so
# this test no longer needs a remapping adapter for the command NAME, only for skipping
# git (ContentPath here is a pre-built fixture tree, not a live clone target).
#
# The real WaypointComplianceContent.psm1 is imported separately, alongside this stub,
# via PowerShellOptions.ModulePreloadPaths (both land in the same InitialSessionState),
# so this module calls its exported Get-WaypointComplianceContent* functions directly
# rather than reaching across a relative build-output path of its own. Exporting THIS
# stub's functions under the REAL module's own names would collide at import time (both
# modules import into the same InitialSessionState) -- instead this stub keeps its own
# distinct names and ContentPullJobHandlerTests' RemappingExecutor maps the handler's
# real command names onto these stub names, exactly like the pre-#993 single-function
# version did.

function Invoke-WaypointContentPullRealExecutorSyncStub {
	<#
	.SYNOPSIS
	    Issue #993 (originally #972): same Profiles enumeration as the real module's
	    Sync-WaypointComplianceContentTree, over a pre-built fixture tree instead of a
	    live git checkout -- no `inspec check` here, matching the real function's own
	    phase-1 scope.

	.PARAMETER ContentPath
	    Root of an already-materialized invented fixture tree (inspec.yml/controls
	    directories already on disk -- this function does no git operations at all).

	.PARAMETER Commit
	    A fabricated commit sha this stub returns as-is (ContentPullJobHandler's
	    ParseSyncOutput needs a non-empty Commit to proceed past its own "no commit"
	    guard).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ContentPath,
		[Parameter(Mandatory)][string]$Commit
	)

	$profiles = @(Get-WaypointComplianceContentProfiles -ContentPath $ContentPath -Commit $Commit)

	[PSCustomObject]@{
		Commit   = $Commit
		Profiles = $profiles
	}
}

Export-ModuleMember -Function Invoke-WaypointContentPullRealExecutorSyncStub
