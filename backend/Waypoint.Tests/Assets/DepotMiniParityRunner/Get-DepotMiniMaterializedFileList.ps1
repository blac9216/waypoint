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

# PR #1742 review round-1 note 1: shelled out to by
# Parity/DepotMiniLoaderParityTests.cs so the REAL, unmodified New-DepotMiniFixture.ps1
# materializes its OWN copy of the shared depot-mini/ tree, and this script reports
# only the depot-relative file list (never the content) -- the C# side materializes
# independently via DepotMiniFixture.cs and the two lists are compared for exact
# equality, proving both loaders stage the SAME tree rather than merely equivalent
# catalog/hash content.
param(
	[Parameter(Mandatory)]
	[string]$RepoRoot,

	[Parameter(Mandatory)]
	[string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$LoaderPath = Join-Path $RepoRoot 'backend/Waypoint.Tests/Fixtures/depot-mini/New-DepotMiniFixture.ps1'
. $LoaderPath

$Fixture = New-DepotMiniFixture
try {
	$Manifest = Get-DepotMiniFileManifest -Directory $Fixture.RootPath
	@($Manifest.Keys | Sort-Object) | ConvertTo-Json -AsArray | Set-Content -Path $OutputPath -Encoding utf8
} finally {
	Remove-Item -Path $Fixture.RootPath -Recurse -Force -ErrorAction SilentlyContinue
}
