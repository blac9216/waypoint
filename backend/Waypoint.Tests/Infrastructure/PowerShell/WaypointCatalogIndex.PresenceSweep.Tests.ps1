#Requires -Modules Pester

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

# Issue #1503 (split from design record #1038/#1043): pins the rewritten
# Invoke-WaypointCatalogIndex presence sweep -- the authenticated catalog, not the
# disk, is now the source of artifact identity. Drives the REAL
# WaypointCatalogIndex.psm1 through a fake -VcfDownloadManagerCommonPath (same
# technique WaypointScan.ConvertAssetIdentity.Tests.ps1 and
# WaypointDownloadLoggingTests.cs use): the fake supplies Get-FileManifest returning
# an invented on-disk manifest, and the REAL WaypointLogging.psm1 (issue #579) is
# imported so the issue #719 Write-Log override this rewrite must preserve is
# exercised for real, not stubbed out.
#
# All paths/hashes/sizes below are invented fixture data (AGENTS.md sanitization) --
# no real depot content, filenames, or vendor catalog excerpt.

BeforeAll {
	$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
	$ModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointCatalogIndex/WaypointCatalogIndex.psm1'
	$LoggingModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointLogging/WaypointLogging.psm1'

	Import-Module $LoggingModulePath -Force
	$script:CatalogIndexModule = Import-Module $ModulePath -Force -PassThru

	# Fake vcf-download-manager.common.ps1: supplies Get-FileManifest returning
	# whatever ordered hashtable the running test staged into
	# $Global:WaypointTest_Manifest, plus a Write-Log that reproduces the real
	# script's level-filtered shape (issue #719) so the override this module must
	# re-apply after dot-sourcing is a genuine regression guard, not a formality.
	$script:FakeCommonPath = Join-Path $TestDrive 'fake.vcf-download-manager.common.ps1'
	@'
function Write-Log {
	param(
		[Parameter(Mandatory, Position = 0)][AllowEmptyString()][string]$Message,
		[ValidateSet('Debug', 'Verbose', 'Info', 'Success', 'Warning', 'Error', 'Critical')][string]$Severity = 'Info',
		[string]$Source
	)
	if (-not $Global:SilentMode) { Write-Host "[$Severity] $Message" }
}

function Get-FileManifest {
	param([string]$Directory, [switch]$IncludeHash, [string]$HashAlgorithm = 'SHA256')
	return $Global:WaypointTest_Manifest
}
'@ | Set-Content -Path $script:FakeCommonPath -Encoding utf8

	function script:Invoke-Sweep {
		param([string]$CatalogJson, [hashtable]$Manifest)

		$DepotDir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
		New-Item -ItemType Directory -Path $DepotDir -Force | Out-Null
		$CatalogRelativePath = 'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'
		$CatalogFullPath = Join-Path $DepotDir $CatalogRelativePath
		New-Item -ItemType Directory -Path (Split-Path -Path $CatalogFullPath -Parent) -Force | Out-Null
		Set-Content -Path $CatalogFullPath -Value $CatalogJson -Encoding utf8

		$Global:WaypointTest_Manifest = $Manifest

		return Invoke-WaypointCatalogIndex -DepotPath $DepotDir -VcfDownloadManagerCommonPath $script:FakeCommonPath
	}
}

AfterAll {
	Remove-Item -Path Variable:\Global:WaypointTest_Manifest -ErrorAction SilentlyContinue
}

Describe 'Invoke-WaypointCatalogIndex presence sweep (issue #1503)' {

	BeforeAll {
		$script:CatalogJson = @'
{
  "patches": {
    "VCENTER": [
      {
        "productVersion": "8.0.3.00900",
        "artifacts": { "bundles": [ { "id": "b1", "binaries": [
          { "fileName": "vcsa-patch.iso", "checksum": "AAAA", "size": 100 },
          { "fileName": "vcsa-corrupt.iso", "checksum": "DEAD", "size": 100 },
          { "fileName": "vcsa-full.zip", "checksum": "BBBB", "size": 5000 }
        ] } ] }
      }
    ],
    "NSX": [
      {
        "productVersion": "4.2.0",
        "artifacts": { "bundles": [ { "id": "b2", "binaries": [
          { "fileName": "nsx-missing.ova", "checksum": "CCCC", "size": 300 }
        ] } ] }
      }
    ]
  }
}
'@

		$script:Manifest = [ordered]@{
			'vcsa-patch.iso'                                            = @{ Size = 100; Hash = 'AAAA' }
			'vcsa-corrupt.iso'                                          = @{ Size = 999; Hash = 'DEAD' }
			'COMP/VCENTER/vmw/1111aaaa/8.0.3.00900/installed-file1.dat' = @{ Size = 10; Hash = 'X' }
			'COMP/VCENTER/vmw/1111aaaa/8.0.3.00900/installed-file2.dat' = @{ Size = 20; Hash = 'Y' }
			'PROD/metadata/upgrade_info.xml'                            = @{ Size = 50; Hash = 'Z' }
			'stray/unexpected-file.bin'                                 = @{ Size = 99; Hash = 'W' }
		}

		$script:Results = script:Invoke-Sweep -CatalogJson $script:CatalogJson -Manifest $script:Manifest
	}

	It 'reports a matched present file with the DepotArtifactUpsert-shaped identity fields (issue #1488)' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'vcsa-patch.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'present'
		$Row.Sha256 | Should -Be 'AAAA'
		$Row.SizeBytes | Should -Be 100
		$Row.Product | Should -Be 'VCENTER'
		$Row.Version | Should -Be '8.0.3.00900'
	}

	It 'reports a catalog entry absent from disk as missing' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'nsx-missing.ova' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'reports a size/hash mismatch as missing, not merely path-present' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'vcsa-corrupt.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'reports a vCenter zip-expand directory as the zip catalog entry''s installed-form presence' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'vcsa-full.zip' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'present'
	}

	It 'does not enumerate the zip-expand directory''s own contents as unknown files' {
		$Unknown = $script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' }
		$Unknown.RelativePath | Should -Not -Contain 'COMP/VCENTER/vmw/1111aaaa/8.0.3.00900/installed-file1.dat'
		$Unknown.RelativePath | Should -Not -Contain 'COMP/VCENTER/vmw/1111aaaa/8.0.3.00900/installed-file2.dat'
	}

	It 'reports upgrade_info.xml as a known/indexed presence record, never unknown' {
		$Row = $script:Results | Where-Object { $_.RelativePath -eq 'PROD/metadata/upgrade_info.xml' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.RecordType | Should -Be 'ArtifactPresence'
		$Row.Status | Should -Be 'present'
	}

	It 'reports exactly one genuinely unknown file' {
		$Unknown = @($script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' })
		$Unknown.Count | Should -Be 1
		$Unknown[0].RelativePath | Should -Be 'stray/unexpected-file.bin'
		$Unknown[0].SizeBytes | Should -Be 99
	}

	It 'invokes the shared WaypointLogging adapter for its own progress messages (issue #719 override preserved)' {
		$Output = script:Invoke-Sweep -CatalogJson $script:CatalogJson -Manifest $script:Manifest 6>&1
		$InfoMessages = @($Output | Where-Object { $_ -is [System.Management.Automation.InformationRecord] })
		($InfoMessages | ForEach-Object { $_.MessageData }) -join "`n" | Should -Match 'Sweep complete'
	}
}

Describe 'Invoke-WaypointCatalogIndex fail-closed behavior' {

	It 'throws on malformed catalog JSON rather than returning an empty sweep' {
		{ script:Invoke-Sweep -CatalogJson '{ not valid json' -Manifest ([ordered]@{}) } | Should -Throw
	}

	It 'throws when the catalog file does not exist at the configured path' {
		$DepotDir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
		New-Item -ItemType Directory -Path $DepotDir -Force | Out-Null
		$Global:WaypointTest_Manifest = [ordered]@{}

		{ Invoke-WaypointCatalogIndex -DepotPath $DepotDir -VcfDownloadManagerCommonPath $script:FakeCommonPath } | Should -Throw
	}
}

Describe 'Get-ZipExpandDirectories (module-internal, issue #1026/#1027 ratified scope)' {

	It 'groups manifest keys under a recognized COMP/VCENTER/vmw/<uuid>/<version>/ directory' {
		$ManifestKeys = @(
			'COMP/VCENTER/vmw/uuid-a/8.0.3.00900/one.dat',
			'COMP/VCENTER/vmw/uuid-a/8.0.3.00900/two.dat',
			'unrelated/file.dat'
		)

		$Dirs = & $script:CatalogIndexModule { param($Keys) Get-ZipExpandDirectories -ManifestKeys $Keys } $ManifestKeys

		$Dirs.Count | Should -Be 1
		$Dirs[0].Version | Should -Be '8.0.3.00900'
		$Dirs[0].Component | Should -Be 'VCENTER'
		$Dirs[0].RelativePaths.Count | Should -Be 2
	}

	It 'recognizes no directory when nothing matches the pattern' {
		$Dirs = & $script:CatalogIndexModule { param($Keys) Get-ZipExpandDirectories -ManifestKeys $Keys } @('some/other/path.dat')
		$Dirs.Count | Should -Be 0
	}
}
