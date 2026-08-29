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
	# Splits a row's remainder (everything after the shape-ID column, minus the closing
	# pipe) on its last UNESCAPED pipe, so a description containing a literal `\|` --
	# as the block-scalar row does -- does not shift which column is read as Expected.
	# Mirrors ShapeInventoryDoc.LastColumn (C#).
	function Get-LastColumn {
		param([Parameter(Mandatory)][AllowEmptyString()][string]$RowRemainder)

		for ($i = $RowRemainder.Length - 1; $i -ge 0; $i--) {
			if ($RowRemainder[$i] -eq '|' -and ($i -eq 0 -or $RowRemainder[$i - 1] -ne '\')) {
				return $RowRemainder.Substring($i + 1)
			}
		}
		return $RowRemainder
	}

	# Classifies an Expected cell as 'accept', 'reject', or $null when its leading word
	# is neither. Mirrors ShapeInventoryDoc.ClassifyExpectedCell (C#) and the same
	# normalization scripts/parser-shape-diff.sh applies: strip leading whitespace and
	# markdown emphasis/backticks, then compare the leading run of letters
	# case-insensitively.
	function Get-ExpectedCellVerdict {
		param([Parameter(Mandatory)][AllowEmptyString()][string]$Cell)

		$token = ([regex]::Match($Cell.TrimStart().TrimStart('*', '_', '`', ' '), '^[A-Za-z]+')).Value.ToLowerInvariant()
		switch ($token) {
			'accepted' { return 'accept' }
			'rejected' { return 'reject' }
			default { return $null }
		}
	}

	# Yields each documented row's shape ID and Expected-cell text under the
	# "## `Get-WaypointProfileDeclaredInputNameSet`" heading -- mirrors
	# ShapeInventoryDoc.EnumerateExpectedCells (C#) closely enough to catch the same
	# doc<->fixture drift, without depending on the C# test assembly from a Pester run.
	# The single place this file reads the table from, so ID parsing and Expected-cell
	# parsing cannot drift on how a row is split into columns.
	function Get-DocumentedShapeRow {
		$doc = Get-Content -Raw -Path $DocPath
		$headingIndex = $doc.IndexOf('## `Get-WaypointProfileDeclaredInputNameSet`')
		if ($headingIndex -lt 0) {
			throw "docs/compliance-content-shape-inventory.md is missing the Get-WaypointProfileDeclaredInputNameSet section this Pester guard parses."
		}
		$rest = $doc.Substring($headingIndex + 1)
		$nextHeadingIndex = $rest.IndexOf("`n## ")
		$section = if ($nextHeadingIndex -ge 0) { $rest.Substring(0, $nextHeadingIndex) } else { $rest }

		$rows = [System.Collections.Generic.List[object]]::new()
		foreach ($line in ($section -split "`n")) {
			if ($line -match '^\| `([a-z0-9-]+)` \|(.*)\|\s*$') {
				$rows.Add([pscustomobject]@{
						ShapeId      = $Matches[1]
						ExpectedCell = Get-LastColumn -RowRemainder $Matches[2]
					})
			}
		}
		return $rows
	}

	$script:DocumentedShapeRows = Get-DocumentedShapeRow
	$script:DocumentedShapeIds = $script:DocumentedShapeRows | ForEach-Object { $_.ShapeId }
	$script:CorpusShapeIds = (Get-WaypointScanShapeExpectationTable) | ForEach-Object { $_.ShapeId }
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

	# Issue #1121's anti-disarm property, PowerShell side: without these two assertions
	# the Expected column is decoration -- the per-shape test's pass/fail is decided
	# entirely by the corpus's own Kind, a second expectation source, so flipping a doc
	# row's Expected cell from Accepted to Rejected (or to unclassifiable prose) left
	# the suite green. The C# sections are protected by
	# ShapeInventoryDoc.AssertExpectedVocabulary + AssertVerdictMatchesFixtures; these
	# are their Pester analogues.
	It 'documents every Expected cell with a classifiable verdict token' {
		$malformed = $script:DocumentedShapeRows |
			Where-Object { $null -eq (Get-ExpectedCellVerdict -Cell $_.ExpectedCell) } |
			ForEach-Object { "$($_.ShapeId): `"$($_.ExpectedCell.Trim())`"" }

		$malformed | Should -BeNullOrEmpty -Because 'every Expected cell must begin with Accepted or Rejected -- scripts/parser-shape-diff.sh parses that token and treats anything else as UNVERIFIABLE'
	}

	It 'reconciles each documented Expected verdict with what the corpus fixture asserts' {
		$mismatches = foreach ($row in $script:DocumentedShapeRows) {
			if ($script:CorpusShapeIds -notcontains $row.ShapeId) { continue }
			$documented = Get-ExpectedCellVerdict -Cell $row.ExpectedCell
			$asserted = Get-WaypointScanShapeVerdict -ShapeId $row.ShapeId
			if ($documented -ne $asserted) {
				"$($row.ShapeId): doc's Expected cell reads '$(if ($null -eq $documented) { 'unclassifiable' } else { $documented })', but its fixture asserts '$asserted'"
			}
		}

		$mismatches | Should -BeNullOrEmpty -Because 'a documentation-only edit to the Expected cell must not be able to disarm a shape while this suite stays green'
	}

	Context 'per-shape resolution' {
		It 'resolves shape "<_>" per its documented expectation' -ForEach (Get-WaypointScanShapeExpectationTable | ForEach-Object { $_.ShapeId }) {
			$shapeId = $_
			$resolved = Test-WaypointScanShapeResolution -ScanModule $script:ScanModule -ShapeId $shapeId
			$resolved | Should -BeTrue -Because "shape '$shapeId' must behave as its corpus expectation Kind asserts (which the 'reconciles each documented Expected verdict' test binds to the doc's Expected cell)"
		}
	}
}
