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

# Issue #1099's PowerShell-side counterpart of RealContentConformanceTests.cs: walks a
# locally cloned vendor content repository (read-only) and reports how many real
# inspec.yml manifests Get-WaypointProfileDeclaredInputNameSet resolves at least one
# declared input for. Skips cleanly (Pester -Skip, no assertion) when the clone is
# absent, exactly like the C# tests, so this never depends on vendor content being
# present. As docs/compliance-content-shape-inventory.md's "What this guard does and
# does not cover" section explains: this proves what the parser ACCEPTS today, not
# that no shape is silently absent -- pair with scripts/parser-shape-diff.sh for that.
#
# No vendor/DISA content is read into this repository -- only aggregate counts are
# reported (AGENTS.md sanitization policy).
#
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path <this file> -CI"

Describe 'Get-WaypointProfileDeclaredInputNameSet real-content conformance' {
	BeforeAll {
		$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../../..')).Path
		$ScanModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointScan/WaypointScan.psm1'
		$script:ScanModule = Import-Module $ScanModulePath -Force -PassThru
		$script:VendorContentRepoRoot = '/workspaces/git/dod-compliance-and-automation'
	}

	It 'resolves at least one declared input for every real manifest that declares inputs:' {
		# Skipped inside the test body (rather than via a discovery-time -Skip) because
		# Pester v6's discovery phase runs in a separate scope from BeforeAll/It, so a
		# $script: variable set in BeforeDiscovery is not visible here -- see this
		# file's own debugging history in issue #1099's PR description.
		if (-not (Test-Path -Path $script:VendorContentRepoRoot -PathType Container)) {
			Set-ItResult -Skipped -Because "vendor content clone not present at $script:VendorContentRepoRoot"
			return
		}

		$manifests = Get-ChildItem -Path $script:VendorContentRepoRoot -Recurse -Filter 'inspec.yml' -File
		$manifests.Count | Should -BeGreaterThan 0

		$accepted = 0
		$rejected = [System.Collections.Generic.List[string]]::new()
		foreach ($manifest in $manifests) {
			$text = [System.IO.File]::ReadAllText($manifest.FullName)
			$declaresInputs = $text -match '(?m)^inputs\s*:'
			$declared = & $script:ScanModule { param($p) Get-WaypointProfileDeclaredInputNameSet -ProfilePath $p } $manifest.DirectoryName

			if ((-not $declaresInputs) -or (@($declared).Count -gt 0)) {
				$accepted++
			} else {
				$rejected.Add($manifest.FullName)
			}
		}

		Write-Information "Get-WaypointProfileDeclaredInputNameSet: $accepted/$($manifests.Count) real manifests accepted, $($rejected.Count) rejected." -InformationAction Continue
		$rejected | Should -BeNullOrEmpty -Because 'a real manifest that declares inputs: should resolve at least one of them'
	}
}
