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

# PowerShell equivalent of Waypoint.Tests/Support/DepotMiniFixture.cs (issue #1696):
# stages the SAME checked-in depot-mini/ tree this file lives beside -- single-sourced
# content, not a second hand-built fixture. Dot-source this file from a Pester suite's
# BeforeAll, then call New-DepotMiniFixture.

function New-DepotMiniFixture {
	<#
	.SYNOPSIS
	    Materializes ../depot-mini into a fresh temp directory: strips every
	    `.placeholder` suffix, assembles the two UMDS metadata zips from the checked-in
	    umds-parts/*.xml (never a checked-in zip -- the sanitize scanner refuses that
	    extension unconditionally), and rewrites the catalog's `{{sha256:...}}` /
	    `{{size:...}}` template tokens against the materialized files' real bytes.
	.OUTPUTS
	    [pscustomobject] with RootPath, CatalogPath, CatalogJson.
	#>
	[CmdletBinding()]
	param()

	$SourceRoot = Join-Path $PSScriptRoot 'depot-mini'
	if (Test-Path -Path (Join-Path $PSScriptRoot 'README.md')) {
		# This script lives inside depot-mini/ itself when dot-sourced directly.
		$SourceRoot = $PSScriptRoot
	}

	if (-not (Test-Path -Path $SourceRoot -PathType Container)) {
		throw "New-DepotMiniFixture: depot-mini source not found at '$SourceRoot'."
	}

	$RootPath = Join-Path ([System.IO.Path]::GetTempPath()) ("wp-depot-mini-" + [guid]::NewGuid().ToString('N'))
	New-Item -ItemType Directory -Path $RootPath -Force | Out-Null

	Get-ChildItem -LiteralPath $SourceRoot -Recurse -File | Where-Object {
		$_.Name -ne 'README.md' -and $_.FullName -notmatch '[\\/]umds-parts[\\/]' -and $_.Name -ne 'New-DepotMiniFixture.ps1'
	} | ForEach-Object {
		$RelativePath = $_.FullName.Substring($SourceRoot.Length).TrimStart('\', '/')
		$DestName = $RelativePath -replace '\.placeholder$', ''
		$DestPath = Join-Path $RootPath $DestName
		New-Item -ItemType Directory -Path (Split-Path -Path $DestPath -Parent) -Force | Out-Null
		Copy-Item -LiteralPath $_.FullName -Destination $DestPath -Force
	}

	New-DepotMiniEsxMetadataZip -SourceRoot $SourceRoot -RootPath $RootPath -VendorDirRelative 'PROD/COMP/ESX_HOST/patch-store/hostupdate/vmw'
	New-DepotMiniEsxMetadataZip -SourceRoot $SourceRoot -RootPath $RootPath -VendorDirRelative 'ESX_LEGACY_STORE/hostupdate/vmw'

	$CatalogPath = Join-Path $RootPath 'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
	$CatalogJson = ConvertTo-DepotMiniResolvedCatalogJson -RootPath $RootPath -Json (Get-Content -LiteralPath $CatalogPath -Raw)
	Set-Content -LiteralPath $CatalogPath -Value $CatalogJson -Encoding utf8 -NoNewline

	return [pscustomobject]@{
		RootPath    = $RootPath
		CatalogPath = $CatalogPath
		CatalogJson = $CatalogJson
	}
}

function New-DepotMiniEsxMetadataZip {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$SourceRoot,
		[Parameter(Mandatory)][string]$RootPath,
		[Parameter(Mandatory)][string]$VendorDirRelative
	)

	$VendorDir = Join-Path $RootPath $VendorDirRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
	if (-not (Test-Path -Path $VendorDir -PathType Container)) {
		return
	}

	$PartsDir = Join-Path $SourceRoot 'umds-parts'
	$StagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("wp-depot-mini-zip-" + [guid]::NewGuid().ToString('N'))
	New-Item -ItemType Directory -Path (Join-Path $StagingDir 'vibs') -Force | Out-Null
	Copy-Item -LiteralPath (Join-Path $PartsDir 'vendor-index.xml') -Destination (Join-Path $StagingDir 'vendor-index.xml')
	Copy-Item -LiteralPath (Join-Path $PartsDir 'vmware.xml') -Destination (Join-Path $StagingDir 'vmware.xml')
	$i = 0
	Get-ChildItem -LiteralPath $PartsDir -Filter 'vib-*.xml' | ForEach-Object {
		Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $StagingDir "vibs/vib-$i.xml".Replace('/', [System.IO.Path]::DirectorySeparatorChar))
		$i++
	}

	$ZipPath = Join-Path $VendorDir 'metadata-fixture1696.zip'
	if (Test-Path -Path $ZipPath) {
		Remove-Item -LiteralPath $ZipPath -Force
	}

	Compress-Archive -Path (Join-Path $StagingDir '*') -DestinationPath $ZipPath
	Remove-Item -LiteralPath $StagingDir -Recurse -Force
}

function ConvertTo-DepotMiniResolvedCatalogJson {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$RootPath,
		[Parameter(Mandatory)][string]$Json
	)

	$Sha256 = [System.Security.Cryptography.SHA256]::Create()
	try {
		$Json = [regex]::Replace($Json, '"\{\{size:([^}]+)\}\}"', {
				param($Match)
				$Path = Join-Path $RootPath $Match.Groups[1].Value.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
				(Get-Item -LiteralPath $Path).Length.ToString()
			})

		$Json = [regex]::Replace($Json, '\{\{sha256:([^}]+)\}\}', {
				param($Match)
				$Path = Join-Path $RootPath $Match.Groups[1].Value.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
				$Bytes = [System.IO.File]::ReadAllBytes($Path)
				[System.BitConverter]::ToString($Sha256.ComputeHash($Bytes)).Replace('-', '')
			})
	} finally {
		$Sha256.Dispose()
	}

	return $Json
}

<#
.SYNOPSIS
    Real filesystem walk of $Directory, DepotPath-relative and forward-slash-keyed
    (matching Get-FileManifest's own contract) -- a genuine `Get-FileManifest`
    stand-in for staging Invoke-WaypointCatalogIndex against the materialized
    depot-mini tree, rather than a hand-typed hashtable.
#>
function Get-DepotMiniFileManifest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Directory
	)

	$Sha256 = [System.Security.Cryptography.SHA256]::Create()
	try {
		$Manifest = [ordered]@{}
		Get-ChildItem -LiteralPath $Directory -Recurse -File | ForEach-Object {
			$RelativePath = $_.FullName.Substring($Directory.Length).TrimStart('\', '/').Replace('\', '/')
			$Bytes = [System.IO.File]::ReadAllBytes($_.FullName)
			$Hash = [System.BitConverter]::ToString($Sha256.ComputeHash($Bytes)).Replace('-', '')
			$Manifest[$RelativePath] = @{ Size = $_.Length; Hash = $Hash }
		}
		return $Manifest
	} finally {
		$Sha256.Dispose()
	}
}
