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
# and only replaces Invoke-WaypointComplianceContentPull's git clone/fetch/checkout steps
# with a no-op over a pre-built invented fixture tree, so this drives the SAME assembly
# path (ContentEntries built via Get-Content -Raw inside a [PSCustomObject] literal) that
# lost RawYaml, through the SAME real PowerShellExecutor/WaypointRunspacePool the runner
# uses -- without a real git remote or the real (unavailable, licensed-tool-adjacent)
# inspec binary.
#
# Issue #984: InspecCheckRan/Passed now also call the REAL WaypointComplianceContent.psm1
# Test-WaypointInspecCheck (not a hand-stubbed true) for controls/-bearing profiles, so
# this end-to-end fixture chain exercises the exact bounded-check path issue #984 fixed
# (System.Diagnostics.Process, no Start-Job/Wait-Job) under the real in-process SMA host.
# ContentPullJobHandlerTests.Execute_RealExecutor_FixtureContentTree_StagesAndPromotesProfiles
# puts an invented stub "inspec" executable on PATH before calling this, so the check
# genuinely runs (and completes well inside its bound) rather than reporting "not found".
#
# The real WaypointComplianceContent.psm1 is imported separately, alongside this stub,
# via PowerShellOptions.ModulePreloadPaths (both land in the same InitialSessionState),
# so this module calls its exported Get-WaypointComplianceContent* functions directly
# rather than reaching across a relative build-output path of its own.

function Invoke-WaypointContentPullRealExecutorStub {
	<#
	.SYNOPSIS
	    Issue #972: same ContentEntries/Profiles assembly as the real module's
	    Invoke-WaypointComplianceContentPull, over a pre-built fixture tree instead of a
	    live git checkout.

	.PARAMETER ContentPath
	    Root of an already-materialized invented fixture tree (inspec.yml/controls
	    directories already on disk -- this function does no git operations at all).

	.PARAMETER Commit
	    A fabricated commit sha this stub returns as-is (issue #972's ParseOutput needs
	    a non-empty Commit to proceed past its own "no commit" guard).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ContentPath,
		[Parameter(Mandatory)][string]$Commit
	)

	$profiles = @(Get-WaypointComplianceContentProfiles -ContentPath $ContentPath -Commit $Commit)

	# Same assembly shape as the real module (WaypointComplianceContent.psm1 lines
	# ~131-156): RawYaml comes from Get-Content -Raw inside a [PSCustomObject] literal
	# built inside a foreach inside an @() array subexpression -- the exact nesting
	# issue #972 proved loses the string without PowerShellValueUnwrap.
	$entries = @(foreach ($p in $profiles) {
			$hasControlsDirectory = Test-Path (Join-Path $p._ProfileDirectory 'controls')
			# Issue #984: run the REAL bounded check (not a hand-stubbed true) so this
			# end-to-end chain exercises the exact path issue #984 fixed, under the real
			# in-process SMA host -- the caller puts an invented stub "inspec" executable
			# on PATH so this genuinely runs and completes well inside its bound.
			$inspecCheck = if ($hasControlsDirectory) {
				Test-WaypointInspecCheck -ProfileDirectory $p._ProfileDirectory -TimeoutSeconds 30
			}
			else {
				[PSCustomObject]@{ Ran = $false; Passed = $false; Detail = 'no controls/ directory -- not an executable-leaf candidate, inspec check skipped' }
			}
			[PSCustomObject]@{
				ProfileKey            = $p.ProfileKey
				RawYaml               = Get-WaypointComplianceContentRawManifest -ProfileDirectory $p._ProfileDirectory
				HasControlsDirectory  = $hasControlsDirectory
				HasFilesDirectory     = Test-Path (Join-Path $p._ProfileDirectory 'files')
				ControlFileNames      = @(Get-WaypointComplianceContentControlFileNames -ProfileDirectory $p._ProfileDirectory)
				InspecCheckRan        = $inspecCheck.Ran
				InspecCheckPassed     = $inspecCheck.Passed
				InspecCheckDetail     = $inspecCheck.Detail
			}
		})

	foreach ($p in $profiles) {
		$p | Add-Member -NotePropertyName Controls -NotePropertyValue @(Get-WaypointComplianceContentControls -ProfileDirectory $p._ProfileDirectory)
		$p.PSObject.Properties.Remove('_ProfileDirectory')
	}

	[PSCustomObject]@{
		Commit         = $Commit
		Profiles       = $profiles
		ContentEntries = $entries
	}
}

Export-ModuleMember -Function Invoke-WaypointContentPullRealExecutorStub
