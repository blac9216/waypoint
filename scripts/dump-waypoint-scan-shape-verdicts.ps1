#!/usr/bin/env pwsh
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

# Issue #1099: the PowerShell side of scripts/parser-shape-diff.sh's differential
# harness. Dumps a JSON map of "Get-WaypointProfileDeclaredInputNameSet/<shape-id>" ->
# resolved (bool) for the corpus WaypointScanShapeCorpus.psm1 defines, running the
# TARGET ref's WaypointScan.psm1 (via -ModulePath) against THIS checkout's corpus
# module (via -CorpusPath) -- both paths point into whichever git worktree the caller
# is dumping, so old-ref and new-ref dumps each exercise their own ref's parser code
# against their own ref's corpus (see the doc's note on the bootstrapping constraint:
# a ref before this script and the corpus module landed cannot be diffed).
#
# Usage:
#   pwsh -NoProfile -File scripts/dump-waypoint-scan-shape-verdicts.ps1 `
#       -ModulePath <worktree>/backend/.../WaypointScan.psm1 `
#       -CorpusPath <worktree>/backend/Waypoint.Tests/.../WaypointScanShapeCorpus.psm1 `
#       -OutJson <path>.json
param(
	[Parameter(Mandatory)]
	[ValidateNotNullOrEmpty()]
	[string]$ModulePath,
	[Parameter(Mandatory)]
	[ValidateNotNullOrEmpty()]
	[string]$CorpusPath,
	[Parameter(Mandatory)]
	[ValidateNotNullOrEmpty()]
	[string]$OutJson
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -Path $ModulePath -PathType Leaf)) {
	Write-Error "error: WaypointScan.psm1 not found at '$ModulePath' -- this ref cannot be dumped."
	exit 3
}
if (-not (Test-Path -Path $CorpusPath -PathType Leaf)) {
	Write-Error "error: WaypointScanShapeCorpus.psm1 not found at '$CorpusPath' -- this ref predates issue #1099's PowerShell differential harness and cannot be dumped."
	exit 3
}

Import-Module $CorpusPath -Force
$scanModule = Import-Module $ModulePath -Force -PassThru

$verdicts = [ordered]@{}
foreach ($expectation in (Get-WaypointScanShapeExpectations)) {
	$shapeId = $expectation.ShapeId
	$resolved = Test-WaypointScanShapeResolves -ScanModule $scanModule -ShapeId $shapeId
	$verdicts["Get-WaypointProfileDeclaredInputNameSet/$shapeId"] = [bool]$resolved
}

$outDir = Split-Path -Parent $OutJson
if ($outDir -and -not (Test-Path -Path $outDir)) {
	New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$verdicts | ConvertTo-Json | Set-Content -Path $OutJson -Encoding utf8NoBOM
