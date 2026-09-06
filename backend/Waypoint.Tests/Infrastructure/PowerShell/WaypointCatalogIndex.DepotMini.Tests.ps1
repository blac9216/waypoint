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

# Issue #1696 deliverable 6: drives the REAL Invoke-WaypointCatalogIndex against the
# SHARED depot-mini fixture (New-DepotMiniFixture.ps1), rather than the hand-typed
# catalog JSON/manifest WaypointCatalogIndex.PresenceSweep.Tests.ps1 builds inline.
# This file adds coverage the shared fixture uniquely offers (a K8s-dominant product
# with many versions, a build-suffixed version whose numeric segments exceed 255 --
# docs issue #1694's sanitize-scanner probe -- and a size-only catalog row), AND, per
# PR #1742 review round-1 finding 1, now also carries the layout-asserting cases
# migrated from WaypointCatalogIndex.PresenceSweep.Tests.ps1 (present-file identity
# fields, absent-from-disk, size/hash mismatch, zip-expand directory contents not
# unknown) -- that suite's own remaining ~20 cases (fail-closed behavior, the
# WaypointLogging adapter, and the module-internal Get-BinaryZipExpandRelativePath/
# Get-ZipExpandDepotPrefix unit tests, none of which touch a depot tree) stay
# hand-built; see #1740.

BeforeAll {
	# Shelled out to the SAME runner Parity/DepotMiniCatalogParityContractTests.cs uses
	# (Invoke-DepotMiniParitySweep.ps1), rather than dot-sourcing New-DepotMiniFixture.ps1
	# and calling Invoke-WaypointCatalogIndex in-process here: doing the latter under
	# Pester 6.1's BeforeAll wrapping (Invoke-InNewScriptScope) intermittently
	# misattributes the module's real, legal `continue` statements as escaping their
	# loop (the exact class pester/Pester#2669 describes) -- reproducible only under
	# Pester, never in a bare `pwsh -File` run of the identical module call. Running the
	# sweep in its own child process sidesteps that interaction entirely and keeps this
	# suite proving the same thing: the REAL module against the shared fixture.
	$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
	$RunnerScript = Join-Path $RepoRoot 'backend/Waypoint.Tests/Assets/DepotMiniParityRunner/Invoke-DepotMiniParitySweep.ps1'
	$OutputPath = Join-Path $TestDrive 'depot-mini-sweep-results.json'

	& pwsh -NoProfile -File $RunnerScript -RepoRoot $RepoRoot -OutputPath $OutputPath
	if ($LASTEXITCODE -ne 0) {
		throw "Invoke-DepotMiniParitySweep.ps1 exited $LASTEXITCODE"
	}

	$script:Results = @(Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json)

	# PR #1742 review round-2 relay finding F1a: the real materialized size of
	# vcsa-patch.iso, read directly from its checked-in placeholder (stripping the
	# `.placeholder` suffix never changes the bytes), so the assertion below checks
	# the SAME real value the sweep computed rather than a hand-typed literal.
	$script:VcsaPatchIsoSize = (Get-Item -LiteralPath (Join-Path $RepoRoot 'backend/Waypoint.Tests/Fixtures/depot-mini/PROD/COMP/VCENTER/vcsa-patch.iso.placeholder')).Length
}

Describe 'Invoke-WaypointCatalogIndex against the shared depot-mini fixture (issue #1696)' {

	It 'reports all 12 TKG versions, most missing (K8s-dominant grouping, #1027 empirical finding)' {
		$TkgRows = @($script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.Product -eq 'TKG' })
		$TkgRows.Count | Should -Be 12
		@($TkgRows | Where-Object { $_.Status -eq 'present' }).Count | Should -Be 2
		@($TkgRows | Where-Object { $_.Status -eq 'missing' }).Count | Should -Be 10
	}

	It 'resolves a build-suffixed version whose numeric segments exceed 255 without misparsing it (docs issue #1694 probe)' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-fixture-9.1.0.6543.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Version | Should -Be '9.1.0.6543'
		$Row.Status | Should -Be 'missing'
	}

	It 'treats a size-only catalog row (no checksum) as present when the size matches on disk' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/ESXI/esxi-image.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Sha256 | Should -BeNullOrEmpty
		$Row.Status | Should -Be 'present'
	}

	It 'reports the fully-staged zip-expand binary present exactly once, never also unknown (round-3 finding 1)' {
		$Rows = @($script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip' })
		$Rows.Count | Should -Be 1
		$Rows[0].Status | Should -Be 'present'
		# PR #1742 review round-2 relay finding F1b: the zip binary's own depot-relative
		# identity (PR #1629 round-2 finding 2's exact subject) must be asserted here too.
		$Rows[0].ExternalId | Should -Be 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'

		$Unknown = @($script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' })
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vcsa-full-a-updaterepo.zip'
	}

	It 'does not report the second same-version zip present just because the first one''s tree exists (round-1 finding 5)' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-full-b-updaterepo.zip' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'reports upgrade_info.xml as known/indexed, never unknown' {
		$Row = $script:Results | Where-Object { $_.RelativePath -eq 'PROD/metadata/upgrade_info.xml' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.RecordType | Should -Be 'ArtifactPresence'
		# PR #1742 review round-2 relay finding F1c: a regression to 'missing' must fail
		# this case, not just a regression to RecordType 'UnknownFile'.
		$Row.Status | Should -Be 'present'
	}

	It 'reports the deliberately-staged unknown file exactly once, at its depot-relative path' {
		$Unknown = @($script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' -and $_.RelativePath -eq 'stray/unexpected-file.bin' })
		$Unknown.Count | Should -Be 1
	}

	It 'reports a matched present file with the DepotArtifactUpsert-shaped identity fields (issue #1488)' {
		# Migrated from WaypointCatalogIndex.PresenceSweep.Tests.ps1 (PR #1742 review
		# round-1 finding 1): keyed depot-relative (PROD/COMP/<Product>/<fileName>),
		# matching how Get-FileManifest keys a real depot -- not the bare catalog
		# fileName a pre-#1503 module looked up by.
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-patch.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'present'
		$Row.ExternalId | Should -Be 'PROD/COMP/VCENTER/vcsa-patch.iso'
		$Row.Sha256 | Should -Match '^[0-9A-Fa-f]{64}$'
		$Row.SizeBytes | Should -Be $script:VcsaPatchIsoSize
		$Row.Product | Should -Be 'VCENTER'
		$Row.Version | Should -Be '9.1.0.5210.25573614'
	}

	It 'reports a catalog entry absent from disk as missing (migrated from PresenceSweep.Tests.ps1)' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/NSX/nsx-missing.ova' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'reports a size/hash mismatch as missing, not merely path-present (migrated from PresenceSweep.Tests.ps1)' {
		$Row = $script:Results | Where-Object { $_.RecordType -eq 'ArtifactPresence' -and $_.RelativePath -eq 'PROD/COMP/VCENTER/vcsa-corrupt.iso' }
		$Row | Should -Not -BeNullOrEmpty
		$Row.Status | Should -Be 'missing'
	}

	It 'does not enumerate the zip-expand directory''s own contents as unknown files (migrated from PresenceSweep.Tests.ps1)' {
		$Unknown = @($script:Results | Where-Object { $_.RecordType -eq 'UnknownFile' })
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file1.dat'
		$Unknown.RelativePath | Should -Not -Contain 'PROD/COMP/VCENTER/vmw/1111aaaa/9.1.0.5210/installed-file2.dat'
	}
}
