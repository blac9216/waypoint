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

# Issue #1099's PowerShell-side shape inventory corpus for
# Get-WaypointProfileDeclaredInputNameSet (WaypointScan.psm1) -- the single source of
# truth this module exports, mirroring the C# *ShapeInventoryTests classes'
# ShapeExpectations table pattern (docs/compliance-content-shape-inventory.md).
# Both WaypointScan.ShapeInventory.Tests.ps1 (Pester) and
# scripts/dump-waypoint-scan-shape-verdicts.ps1 (the differential harness's PS side)
# import THIS module rather than duplicating the fixture/expectation list, so the two
# cannot silently drift apart the way the doc table and C# fixtures did before issue
# #1077's guard existed.
#
# Every fixture below is INVENTED for this guard only -- no real vendor/DISA
# inspec.yml content is reproduced here (AGENTS.md sanitization policy). The target
# input name every accept-flavoured fixture declares is the invented
# 'nsx_manager_address' (matching InspecManifestShapeInventoryTests' convention so a
# reviewer comparing the two tables side by side sees the same target name).

$script:TargetName = 'nsx_manager_address'
$script:DependsOnlyName = 'some_other_profile'

# One row per documented shape ID, in the order
# docs/compliance-content-shape-inventory.md documents them under the
# "Get-WaypointProfileDeclaredInputNameSet" heading. `Kind` selects which assertion
# applies:
#   'declared'        -- $script:TargetName must be a member of the returned set.
#   'empty'            -- the returned set must be empty.
#   'declared-exclude-depends' -- $script:TargetName must be a member AND
#                          $script:DependsOnlyName must NOT be a member.
$script:ShapeExpectations = @(
	[ordered]@{ ShapeId = 'indented-dash-sequence';            Kind = 'declared' }
	[ordered]@{ ShapeId = 'column0-dash-sequence';              Kind = 'declared' }
	[ordered]@{ ShapeId = 'name-not-first-key';                 Kind = 'declared' }
	[ordered]@{ ShapeId = 'column0-comment-between-entries';    Kind = 'declared' }
	[ordered]@{ ShapeId = 'trailing-inline-comment';            Kind = 'declared' }
	[ordered]@{ ShapeId = 'trailing-comment-tab-separator';     Kind = 'declared' }
	[ordered]@{ ShapeId = 'trailing-comment-multi-space-separator'; Kind = 'declared' }
	[ordered]@{ ShapeId = 'block-scalar-folded-description';    Kind = 'declared' }
	[ordered]@{ ShapeId = 'block-scalar-literal-description';   Kind = 'declared' }
	[ordered]@{ ShapeId = 'nested-extra-keys-ignored';          Kind = 'declared' }
	[ordered]@{ ShapeId = 'empty-inputs-sequence';               Kind = 'empty' }
	[ordered]@{ ShapeId = 'missing-inputs-key';                  Kind = 'empty' }
	[ordered]@{ ShapeId = 'document-start-end-markers';         Kind = 'declared' }
	[ordered]@{ ShapeId = 'quoted-scalar-name-double';          Kind = 'declared' }
	[ordered]@{ ShapeId = 'quoted-scalar-name-single';          Kind = 'declared' }
	[ordered]@{ ShapeId = 'nested-name-under-value-mapping';    Kind = 'declared' }
	[ordered]@{ ShapeId = 'nested-name-under-value-sequence';   Kind = 'declared' }
	[ordered]@{ ShapeId = 'inputs-depends-adjacency';           Kind = 'declared-exclude-depends' }
	[ordered]@{ ShapeId = 'tab-block-indentation';               Kind = 'declared' }
	[ordered]@{ ShapeId = 'crlf-line-endings';                   Kind = 'declared' }
)

function Get-WaypointScanShapeExpectationTable {
	[CmdletBinding()]
	param()
	return $script:ShapeExpectations
}

function New-WaypointScanShapeFixtureContent {
	# Pure function -- builds and returns a string, no state change -- but the 'New'
	# verb still trips PSUseShouldProcessForStateChangingFunctions.
	[Diagnostics.CodeAnalysis.SuppressMessage('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure builder function; no state is changed.')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ShapeId
	)

	switch ($ShapeId) {
		'indented-dash-sequence' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    type: String`n    required: true`n"
		}
		'column0-dash-sequence' {
			return "name: invented-profile`ninputs:`n- name: nsx_manager_address`n  type: String`n  required: true`n"
		}
		'name-not-first-key' {
			return "name: invented-profile`ninputs:`n  - description: NSX manager address`n    name: nsx_manager_address`n    type: String`n"
		}
		'column0-comment-between-entries' {
			return "name: invented-profile`ninputs:`n  - name: unrelated_input`n# a column-0 comment between entries`n  - name: nsx_manager_address`n    type: String`n"
		}
		'trailing-inline-comment' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address # legacy 3.x key retained for compatibility`n    type: String`n"
		}
		'trailing-comment-tab-separator' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`t# legacy 3.x key retained for compatibility`n    type: String`n"
		}
		'trailing-comment-multi-space-separator' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address   # legacy 3.x key retained for compatibility`n    type: String`n"
		}
		'block-scalar-folded-description' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    description: >`n      The NSX manager address used to authenticate the scan.`n      Spans multiple folded lines.`n    type: String`n"
		}
		'block-scalar-literal-description' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    description: |`n      The NSX manager address used to authenticate the scan.`n      Spans multiple literal lines.`n    type: String`n"
		}
		'nested-extra-keys-ignored' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    type: String`n    required: true`n    sensitive: true`n    value:`n      default: invented.example.internal`n"
		}
		'empty-inputs-sequence' {
			return "name: invented-profile`ninputs: []`n"
		}
		'missing-inputs-key' {
			return "name: invented-profile`n"
		}
		'document-start-end-markers' {
			return "---`nname: invented-profile`ninputs:`n  - name: nsx_manager_address`n    type: String`n...`n"
		}
		'quoted-scalar-name-double' {
			return "name: invented-profile`ninputs:`n  - name: `"nsx_manager_address`"`n    type: String`n"
		}
		'quoted-scalar-name-single' {
			return "name: invented-profile`ninputs:`n  - name: 'nsx_manager_address'`n    type: String`n"
		}
		'nested-name-under-value-mapping' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    value:`n      name: nested_should_not_win`n    type: String`n"
		}
		'nested-name-under-value-sequence' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    value:`n      - name: nested_should_not_win`n    type: String`n"
		}
		'inputs-depends-adjacency' {
			return "name: invented-profile`ninputs:`n  - name: nsx_manager_address`n    type: String`ndepends:`n  - name: some_other_profile`n"
		}
		'tab-block-indentation' {
			return "name: invented-profile`ninputs:`n`t- name: nsx_manager_address`n`t  type: String`n"
		}
		'crlf-line-endings' {
			return "name: invented-profile`r`ninputs:`r`n  - name: nsx_manager_address`r`n    type: String`r`n    required: true`r`n"
		}
		default {
			throw "WaypointScanShapeCorpus: unknown shape id '$ShapeId'"
		}
	}
}

# Writes the shape's fixture to <ProfileRoot>/inspec.yml verbatim (no re-indentation,
# no line-ending normalization) so a CRLF or column-0 shape genuinely survives to
# disk, mirroring InspecManifestShapeInventoryTests' WriteProfileFixtureRaw discipline
# (PR #1084 round-1 review's "fixture-helper quality" note).
function New-WaypointScanShapeFixture {
	[CmdletBinding(SupportsShouldProcess)]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ShapeId,
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfileRoot
	)

	if (-not $PSCmdlet.ShouldProcess($ProfileRoot, "Write invented shape fixture '$ShapeId'")) {
		return
	}

	New-Item -ItemType Directory -Path $ProfileRoot -Force | Out-Null
	$content = New-WaypointScanShapeFixtureContent -ShapeId $ShapeId
	$manifestPath = Join-Path $ProfileRoot 'inspec.yml'
	[System.IO.File]::WriteAllText($manifestPath, $content)
}

# Invokes the TARGET module's private Get-WaypointProfileDeclaredInputNameSet against
# one shape's fixture and returns the boolean "resolved" signal the differential
# harness diffs old-vs-new on -- the PowerShell analogue of
# InspecManifestShapeInventoryTests.Resolves. $ScanModule must be a module object from
# `Import-Module ... -PassThru` (the function is module-private, so it can only be
# invoked via `& $ScanModule { ... }`, never directly from outside the module).
function Test-WaypointScanShapeResolution {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[System.Management.Automation.PSModuleInfo]$ScanModule,
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ShapeId
	)

	$expectation = $script:ShapeExpectations | Where-Object { $_.ShapeId -eq $ShapeId } | Select-Object -First 1
	if (-not $expectation) {
		throw "WaypointScanShapeCorpus: no expectation registered for shape id '$ShapeId'"
	}

	$root = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
	try {
		New-WaypointScanShapeFixture -ShapeId $ShapeId -ProfileRoot $root
		$declared = & $ScanModule { param($p) Get-WaypointProfileDeclaredInputNameSet -ProfilePath $p } $root

		switch ($expectation.Kind) {
			'empty' { return ($null -eq $declared -or @($declared).Count -eq 0) }
			'declared' { return (@($declared) -contains $script:TargetName) }
			'declared-exclude-depends' {
				return ((@($declared) -contains $script:TargetName) -and -not (@($declared) -contains $script:DependsOnlyName))
			}
			default { throw "WaypointScanShapeCorpus: unknown expectation kind '$($expectation.Kind)' for shape id '$ShapeId'" }
		}
	} finally {
		if (Test-Path -Path $root) {
			Remove-Item -Recurse -Force -Path $root
		}
	}
}

Export-ModuleMember -Function Get-WaypointScanShapeExpectationTable, New-WaypointScanShapeFixtureContent, New-WaypointScanShapeFixture, Test-WaypointScanShapeResolution
