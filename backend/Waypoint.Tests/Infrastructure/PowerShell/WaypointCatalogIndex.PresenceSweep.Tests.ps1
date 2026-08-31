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

	# Round-3 review finding 2 -- the class-killing invariant. Rounds 1-3 each closed a
	# single branch that emitted a presence record without consuming its path, and each
	# time the fixture agreed with the code. This asserts the property itself: no
	# RelativePath may appear in BOTH an ArtifactPresence record and an UnknownFile
	# record. It runs inside Invoke-Sweep, so it is enforced for EVERY scenario in this
	# suite -- present and future -- not only for the case that named it.
	function script:Assert-NoPresenceUnknownOverlap {
		param([object[]]$Results)

		$PresencePaths = [System.Collections.Generic.HashSet[string]]::new(
			[string[]]@($Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' } | ForEach-Object { $_.RelativePath }),
			[System.StringComparer]::OrdinalIgnoreCase)
		$Overlap = @($Results |
				Where-Object { $_.RecordType -eq 'UnknownFile' -and $PresencePaths.Contains($_.RelativePath) } |
				ForEach-Object { $_.RelativePath })

		$Overlap -join ', ' | Should -BeNullOrEmpty -Because 'no path may be reported as both an ArtifactPresence record and an UnknownFile record'
	}

	function script:Invoke-Sweep {
		param([string]$CatalogJson, [hashtable]$Manifest)

		$DepotDir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
		New-Item -ItemType Directory -Path $DepotDir -Force | Out-Null
		$CatalogRelativePath = 'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'
		$CatalogFullPath = Join-Path $DepotDir $CatalogRelativePath
		New-Item -ItemType Directory -Path (Split-Path -Path $CatalogFullPath -Parent) -Force | Out-Null
		Set-Content -Path $CatalogFullPath -Value $CatalogJson -Encoding utf8

		$Global:WaypointTest_Manifest = $Manifest

		$SweepResults = @(Invoke-WaypointCatalogIndex -DepotPath $DepotDir -VcfDownloadManagerCommonPath $script:FakeCommonPath)
		script:Assert-NoPresenceUnknownOverlap -Results $SweepResults
		return $SweepResults
	}
}

AfterAll {
	Remove-Item -Path Variable:\Global:WaypointTest_Manifest -ErrorAction SilentlyContinue
}

Describe 'Invoke-WaypointCatalogIndex presence sweep (issue #1503)' {

	BeforeAll {
		# Round-1 review finding 4: fixture is depot-shape-faithful, not
		# code-shape-faithful -- manifest keys are DepotPath-relative (PROD-rooted,
		# per #1027's depot-consumption finding), the VCENTER productVersion carries
		# a real build suffix, and two same-version VCENTER zips each carry their
		# own distinct zip-expand `metadata[]` (finding 5's disambiguation case).
		# All paths/hashes/sizes/uuids below are invented fixture data (AGENTS.md
		# sanitization) -- no real depot content, filenames, or vendor catalog excerpt.
		$script:CatalogJson = @'
{
  "patches": {
    "VCENTER": [
      {
        "productVersion": "9.1.0.5210.25573614",
        "artifacts": { "bundles": [ { "id": "b1", "binaries": [
          { "fileName": "vcsa-patch.iso", "checksum": "AAAA", "size": 100 },
          { "fileName": "vcsa-corrupt.iso", "checksum": "DEAD", "size": 100 },
          { "fileName": "vcsa-full-a-updaterepo.zip", "checksum": "BBBB", "size": 5000,
            "metadata": [ { "tag": "zip-expand",
              "configuration": { "key": "relative", "value": "vmw/1111aaaa/9.1.0.5210" } } ] },
          { "fileName": "vcsa-full-b-updaterepo.zip", "checksum": "FFFF", "size": 6000,
            "metadata": [ { "tag": "zip-expand",
              "configuration": { "key": "relative", "value": "vmw/2222bbbb/9.1.0.5210" } } ] }
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
			'PROD/COMP/VCENTER/vcsa-patch.iso'                                    = @{ Size = 100; Hash = 'AAAA' }
			'PROD/COMP/VCENTER/vcsa-corrupt.iso'                                  = @{ Size = 999; Hash = 'DEAD' }
			# Round-3 review finding 1/2: a correctly staged depot holds the zip binary
			# ITSELF alongside its expanded tree -- the production steady state, which
			# no fixture staged for three rounds, so the zip's double-report (present +
			# UnknownFile) was unreachable by any test.
			'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'                        = @{ Size = 5000; Hash = 'BBBB' }
			'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file1.dat'       = @{ Size = 10; Hash = 'X' }
			'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file2.dat'       = @{ Size = 20; Hash = 'Y' }
			'PROD/metadata/upgrade_info.xml'                                      = @{ Size = 50; Hash = 'Z' }
			'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'  = @{ Size = 1; Hash = 'CAT' }
			'stray/unexpected-file.bin'                                           = @{ Size = 99; Hash = 'W' }
		}

		$script:Results = script:Invoke-Sweep -CatalogJson $script:CatalogJson -Manifest $script:Manifest
	}

	It 'reports a matched present file with the DepotArtifactUpsert-shaped identity fields (issue #1488)' {
		# Round-2 review finding 1: keyed depot-relative (PROD/COMP/<Product>/<fileName>),
		# matching how Get-FileManifest keys a real depot -- not the bare catalog
		# fileName the pre-fix module (and this row, before round 2) looked up by.
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-patch.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'present'
		$Row.ExternalId | Should -Be 'PROD/COMP/VCENTER/vcsa-patch.iso'
		$Row.Sha256 | Should -Be 'AAAA'
		$Row.SizeBytes | Should -Be 100
		$Row.Product | Should -Be 'VCENTER'
		$Row.Version | Should -Be '9.1.0.5210.25573614'
	}

	It 'reports a catalog entry absent from disk as missing' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/NSX/nsx-missing.ova' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'reports a size/hash mismatch as missing, not merely path-present' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-corrupt.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'reports a vCenter zip-expand directory as its own zip catalog entry''s installed-form presence (issue #1027, round-1 finding 1/2)' {
		# Round-2 review finding 2: the zip binary's own identity is depot-relative too
		# (PROD/COMP/<Product>/<fileName>), consistent with the ordinary-artifact rows --
		# not the bare catalog fileName round 1 left unflagged.
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'present'
		$Row.ExternalId | Should -Be 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'
	}

	It 'reports a zip staged alongside its own expanded tree exactly once, never also as an unknown file (round-3 finding 1, issue #1503 AC 2)' {
		$Rows = @($script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip' })
		$Rows.Count | Should -Be 1
		$Rows[0].Status | Should -Be 'present'

		$Unknown = @($script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' })
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'
	}

	It 'never reports any path as both an ArtifactPresence record and an UnknownFile record (round-3 finding 2 invariant)' {
		script:Assert-NoPresenceUnknownOverlap -Results $script:Results
	}

	It 'does not report the second same-version zip present just because the first one''s tree exists (round-1 finding 5)' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-full-b-updaterepo.zip' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'does not enumerate the zip-expand directory''s own contents as unknown files' {
		$Unknown = $script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' }
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file1.dat'
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file2.dat'
	}

	It 'reports upgrade_info.xml as a known/indexed presence record, never unknown' {
		$Row = $script:Results | Where-Object { $_.RelativePath -eq 'PROD/metadata/upgrade_info.xml' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.RecordType | Should -Be 'ArtifactPresence'
		$Row.Status | Should -Be 'present'
	}

	It 'reports the on-disk catalog document and the unrelated stray file as the only genuinely unknown files (#1634 tracks reducing this noise)' {
		$Unknown = @($script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' })
		$Unknown.RelativePath | Should -Contain 'stray/unexpected-file.bin'
		$Unknown.RelativePath | Should -Contain 'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'
		$Unknown.Count | Should -Be 2
	}

	It 'invokes the shared WaypointLogging adapter for its own progress messages (issue #719 override preserved)' {
		$Output = script:Invoke-Sweep -CatalogJson $script:CatalogJson -Manifest $script:Manifest 6>&1
		$InfoMessages = @($Output | Where-Object { $_ -is [System.Management.Automation.InformationRecord] })
		($InfoMessages | ForEach-Object { $_.MessageData }) -join "`n" | Should -Match 'Sweep complete'
	}
}

Describe 'Invoke-WaypointCatalogIndex on a fully and correctly staged depot (round-3 review finding 1)' {

	# The partially-staged depot masks the defect; the fully staged one -- zip binary
	# AND its expanded tree both on disk -- is the production steady state and is where
	# the zip double-reported. Isolated here so the case cannot be diluted by the
	# larger fixture's other rows.
	BeforeAll {
		$script:StagedCatalogJson = @'
{
  "patches": {
    "VCENTER": [
      {
        "productVersion": "9.1.0.5210.25573614",
        "artifacts": { "bundles": [ { "id": "b1", "binaries": [
          { "fileName": "vcsa-full-a-updaterepo.zip", "checksum": "BBBB", "size": 5000,
            "metadata": [ { "tag": "zip-expand",
              "configuration": { "key": "relative", "value": "vmw/1111aaaa/9.1.0.5210" } } ] }
        ] } ] }
      }
    ]
  }
}
'@

		$script:StagedManifest = [ordered]@{
			'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'                       = @{ Size = 5000; Hash = 'BBBB' }
			'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file1.dat'      = @{ Size = 10; Hash = 'X' }
			'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'  = @{ Size = 1; Hash = 'CAT' }
		}

		$script:StagedResults = script:Invoke-Sweep -CatalogJson $script:StagedCatalogJson -Manifest $script:StagedManifest
	}

	It 'emits no UnknownFile record for the staged zip or any file of its expanded tree' {
		$Unknown = @($script:StagedResults | Where-Object { $_.RecordType -eq 'UnknownFile' })
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file1.dat'
		$Unknown.Count | Should -Be 1
	}

	It 'never reports any path as both an ArtifactPresence record and an UnknownFile record (round-3 finding 2 invariant)' {
		script:Assert-NoPresenceUnknownOverlap -Results $script:StagedResults
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

Describe 'Get-BinaryZipExpandRelativePath (module-internal, issue #1027 depot-consumption finding)' {

	It 'reads the relative value from a zip-expand metadata tag (configuration as a single object)' {
		$Binary = [pscustomobject]@{
			fileName = 'vcsa-full-a-updaterepo.zip'
			metadata = @(
				[pscustomobject]@{ tag = 'zip-expand'; configuration = [pscustomobject]@{ key = 'relative'; value = 'vmw/1111aaaa/9.1.0.5210' } }
			)
		}

		$Value = & $script:CatalogIndexModule { param($B) Get-BinaryZipExpandRelativePath -Binary $B } $Binary
		$Value | Should -Be 'vmw/1111aaaa/9.1.0.5210'
	}

	It 'reads the relative value from a zip-expand metadata tag (configuration as an array of key/value pairs)' {
		$Binary = [pscustomobject]@{
			fileName = 'vcsa-full-b-updaterepo.zip'
			metadata = @(
				[pscustomobject]@{
					tag           = 'zip-expand'
					configuration = @(
						[pscustomobject]@{ key = 'unrelated'; value = 'ignored' }
						[pscustomobject]@{ key = 'relative'; value = 'vmw/2222bbbb/9.1.0.5210' }
					)
				}
			)
		}

		$Value = & $script:CatalogIndexModule { param($B) Get-BinaryZipExpandRelativePath -Binary $B } $Binary
		$Value | Should -Be 'vmw/2222bbbb/9.1.0.5210'
	}

	It 'returns $null for a binary carrying no zip-expand metadata' {
		$Binary = [pscustomobject]@{ fileName = 'vcsa-patch.iso' }
		$Value = & $script:CatalogIndexModule { param($B) Get-BinaryZipExpandRelativePath -Binary $B } $Binary
		$Value | Should -BeNullOrEmpty
	}
}

Describe 'Get-ZipExpandDepotPrefix (module-internal, round-1 review finding 1)' {

	It 'anchors the expand prefix at the depot root, not at COMP' {
		$Prefix = & $script:CatalogIndexModule { param($P, $R) Get-ZipExpandDepotPrefix -Product $P -RelativePath $R } 'VCENTER' 'vmw/1111aaaa/9.1.0.5210'
		$Prefix | Should -Be 'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/'
	}
}
