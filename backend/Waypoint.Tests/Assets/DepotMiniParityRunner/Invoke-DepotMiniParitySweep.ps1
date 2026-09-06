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

# Issue #1696 parser-parity contract: shelled out to by
# Parity/DepotMiniCatalogParityContractTests.cs so the REAL, unmodified
# WaypointCatalogIndex.psm1 runs the presence sweep over its OWN materialization of
# the shared depot-mini/ fixture -- content-derived hashes/sizes make the two
# independent materializations (this one and DepotMiniFixture.cs's) identical without
# needing to share a directory. Writes one JSON array of the sweep's raw records to
# -OutputPath (never stdout -- Write-Log's own host/information output would
# otherwise interleave with the JSON on redirected stdout).
param(
	[Parameter(Mandatory)]
	[string]$RepoRoot,

	[Parameter(Mandatory)]
	[string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$LoggingModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointLogging/WaypointLogging.psm1'
$CatalogIndexModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointCatalogIndex/WaypointCatalogIndex.psm1'
$LoaderPath = Join-Path $RepoRoot 'backend/Waypoint.Tests/Fixtures/depot-mini/New-DepotMiniFixture.ps1'

Import-Module $LoggingModulePath -Force
Import-Module $CatalogIndexModulePath -Force
. $LoaderPath

$Fixture = New-DepotMiniFixture

$FakeCommonPath = Join-Path ([System.IO.Path]::GetTempPath()) ("wp-depot-mini-fake-common-" + [guid]::NewGuid().ToString('N') + '.ps1')
@'
function Write-Log {
	param(
		[Parameter(Mandatory, Position = 0)][AllowEmptyString()][string]$Message,
		[string]$Severity = 'Info',
		[string]$Source
	)
}

function Get-FileManifest {
	param([string]$Directory, [switch]$IncludeHash, [string]$HashAlgorithm = 'SHA256')
	return Get-DepotMiniFileManifest -Directory $Directory
}
'@ | Set-Content -Path $FakeCommonPath -Encoding utf8

try {
	# Assigning to a variable captures only the success/output stream -- Write-Log's
	# host/information output prints to the console independently and never lands in
	# $Results, but writing straight to a file (never stdout) removes any doubt.
	$Results = @(Invoke-WaypointCatalogIndex -DepotPath $Fixture.RootPath -VcfDownloadManagerCommonPath $FakeCommonPath)
	$Results | ConvertTo-Json -Depth 6 -AsArray | Set-Content -Path $OutputPath -Encoding utf8
} finally {
	Remove-Item -Path $FakeCommonPath -Force -ErrorAction SilentlyContinue
	Remove-Item -Path $Fixture.RootPath -Recurse -Force -ErrorAction SilentlyContinue
}
