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

# Issue #1099's Pester counterpart to the C# *ShapeInventoryTests classes
# (docs/compliance-content-shape-inventory.md), for the one parser in #1077's scope
# that is PowerShell rather than C#: Get-WaypointProfileDeclaredInputNameSet
# (WaypointScan.psm1), the shared manifest scanner PR #1135 extracted for both the
# NSX and vSphere scan paths. Its history is the worst in the guard's scope -- issue
# #1071 found several silent misses, and PR #1084's own first fix commit introduced a
# NEW one, caught by that PR's round-1 review before merge.
#
# Every fixture is invented in WaypointScanShapeCorpus.psm1 (AGENTS.md sanitization:
# no real vendor/DISA inspec.yml content). This file asserts BOTH directions against
# the doc, the same "doc <-> fixture" completeness property ShapeInventoryDoc.
# AssertCompleteness gives the C# parsers -- reimplemented here in PowerShell because
# the corpus and the parser under test are both PowerShell, and because this Pester
# run is not part of `dotnet test` (see the doc's intro).
#
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path <this file> -CI"

# Imported at container scope (not inside BeforeAll) because Pester's discovery phase
# -- which evaluates BeforeDiscovery / -ForEach below to generate the per-shape It
# blocks -- runs before any BeforeAll.
Import-Module (Join-Path $PSScriptRoot 'WaypointScanShapeCorpus.psm1') -Force

BeforeAll {
	$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../../..')).Path
	$ScanModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointScan/WaypointScan.psm1'
	$DocPath = Join-Path $RepoRoot 'docs/compliance-content-shape-inventory.md'

	$script:ScanModule = Import-Module $ScanModulePath -Force -PassThru

	# Parses shape IDs documented under the
	# "## `Get-WaypointProfileDeclaredInputNameSet` (`WaypointScan.psm1`)" heading of
	# the shape inventory doc -- mirrors ShapeInventoryDoc.ParseShapeIds (C#) closely
	# enough to catch the same doc<->fixture drift, without depending on the C# test
	# assembly from a Pester run.
	function Get-DocumentedShapeIds {
		$doc = Get-Content -Raw -Path $DocPath
		$headingIndex = $doc.IndexOf('## `Get-WaypointProfileDeclaredInputNameSet`')
		if ($headingIndex -lt 0) {
			throw "docs/compliance-content-shape-inventory.md is missing the Get-WaypointProfileDeclaredInputNameSet section this Pester guard parses."
		}
		$rest = $doc.Substring($headingIndex + 1)
		$nextHeadingIndex = $rest.IndexOf("`n## ")
		$section = if ($nextHeadingIndex -ge 0) { $rest.Substring(0, $nextHeadingIndex) } else { $rest }

		$ids = [System.Collections.Generic.List[string]]::new()
		foreach ($line in ($section -split "`n")) {
			if ($line -match '^\| `([a-z0-9-]+)` \|') {
				$ids.Add($Matches[1])
			}
		}
		return $ids
	}

	$script:DocumentedShapeIds = Get-DocumentedShapeIds
	$script:CorpusShapeIds = (Get-WaypointScanShapeExpectations) | ForEach-Object { $_.ShapeId }
}

Describe 'Get-WaypointProfileDeclaredInputNameSet shape inventory' {
	It 'documents exactly the shapes the corpus implements (both directions)' {
		$documentedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$script:DocumentedShapeIds)
		$corpusSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$script:CorpusShapeIds)

		$documentedButNotImplemented = $script:DocumentedShapeIds | Where-Object { -not $corpusSet.Contains($_) }
		$implementedButNotDocumented = $script:CorpusShapeIds | Where-Object { -not $documentedSet.Contains($_) }

		$documentedButNotImplemented | Should -BeNullOrEmpty -Because 'every documented shape needs a corpus fixture'
		$implementedButNotDocumented | Should -BeNullOrEmpty -Because 'every corpus fixture needs a documented row'
	}

	Context 'per-shape resolution' {
		BeforeDiscovery {
			$shapeIds = (Get-WaypointScanShapeExpectations) | ForEach-Object { $_.ShapeId }
		}

		It 'resolves shape "<_>" per its documented expectation' -ForEach $shapeIds {
			$shapeId = $_
			$resolved = Test-WaypointScanShapeResolves -ScanModule $script:ScanModule -ShapeId $shapeId
			$resolved | Should -BeTrue -Because "shape '$shapeId' is documented as Accepted in docs/compliance-content-shape-inventory.md"
		}
	}
}
