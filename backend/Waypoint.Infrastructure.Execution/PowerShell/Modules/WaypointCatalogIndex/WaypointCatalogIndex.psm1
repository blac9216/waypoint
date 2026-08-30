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

# Waypoint-owned shim (issue #194, epic #9 slice 2; rewritten for issue #1503, split
# from design record #1038/#1043). It is the thin, Waypoint-authored seam that
# dot-sources the project-owned sibling repository's unmodified vcf-docker-download
# scripts and adapts their output to the presence-sweep contract
# CatalogIndexJobHandler (issue #1512) needs.
#
# Domain-model open question 4 (docs/domain-model.md) is answered by what this module
# calls: Get-FileManifest is a pure filesystem walk of the depot share (no vendor
# binary, no depot token) -- the sweep does NOT require the download tool. The depot
# token parameter below is accepted and threaded through for forward compatibility
# with a future vendor-catalog-refresh path, but the sweep itself never reads it.
#
# Issue #1038/#1488: the authenticated vendor catalog (productVersionCatalog.json,
# already on disk under DepotPath -- the same document
# Waypoint.Core.Downloads.ManagedToolOptions.ProductVersionCatalogPath names for the
# connected-pull side, and the same shape VendorProductVersionCatalogParser.cs parses)
# is now the single source of artifact identity. This function no longer walks the
# depot and emits one row per file found; it walks the CATALOG and verifies each
# entry's presence on disk by relative path + size/hash, matching migration 0100's
# (issue #1488) DepotArtifactUpsert identity fields exactly (RelativePath/Sha256/
# SizeBytes/Status) so #1512's job-handler wiring can consume the output with no
# translation. Files found on disk that match no catalog entry -- and are not one of
# the two recognized catalog-adjacent exceptions below -- are emitted as a distinct
# unknown-file shape (never silently dropped, decision Q11).
#
# Recognized catalog-adjacent exceptions (ratified #1026/#1027 scope addition, folded
# into #1503 by #1038's split plan):
#   - vCenter updaterepo zip-expand directories: a catalog-listed VCENTER *.zip
#     installs as an expanded tree under COMP/VCENTER/vmw/<uuid>/<version>/. The
#     sweep verifies the expanded tree's presence as the zip entry's installed form
#     (status 'present') instead of requiring the zip file itself on disk, and does
#     not enumerate the expanded tree's own contents as unknown files.
#   - upgrade_info.xml: a vendor-signed file that sits alongside the catalog and
#     enumerates vCenter upgrade paths. Any file with that basename is reported as a
#     known/indexed presence record, never as unknown.
#
# Assumption (stated here and in the #1503 PR body per that issue's own "Risks"
# section): the catalog's binaries[].fileName remains a bare filename identity, the
# same simplifying choice VendorProductVersionCatalogParser.cs already makes for the
# connected-pull side -- true nested-relative-path resolution for every catalog entry
# is explicitly out of this slice's scope (that parser's own doc comment defers it to
# "presence-sweep behavior, #1503", but doing it for every artifact family is a much
# larger surface than this M-sized issue's fixtures cover; the zip-expand-directory
# and upgrade_info.xml cases above are the two nested-path exceptions #1026/#1027
# actually ratified, and are handled explicitly).

$Script:VcfDownloadManagerCommonPath = $env:WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH

# Issue #1488: matches ManagedToolOptions.ProductVersionCatalogPath's default
# ("PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json") so the sweep
# reads the same document the connected-pull path authenticates, without duplicating
# that default in a second place beyond this comment's pointer to it.
$Script:DefaultCatalogRelativePath = 'PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json'

# Issue #1026/#1027 ratified scope addition: vCenter updaterepo zip-expand
# directories live at COMP/VCENTER/vmw/<uuid>/<version>/ under the depot root.
$Script:ZipExpandDirPattern = '(?i)^COMP/VCENTER/vmw/(?<Uuid>[^/]+)/(?<Version>[^/]+)/'

function Invoke-WaypointCatalogIndex {
	<#
	.SYNOPSIS
	    Presence-sweeps the offline depot share against the authenticated vendor
	    catalog -- the Waypoint adaptation of the vcf-docker-download indexing path,
	    rewritten (issue #1503) so catalog entries, not disk files, are the source of
	    identity.

	.PARAMETER DepotPath
	    Root directory of the offline depot share to sweep.

	.PARAMETER DepotToken
	    The decrypted depot token, bound as a typed parameter (never interpolated into
	    this or any script text -- security.md controls 1/2). Accepted but unused by
	    the pure-filesystem sweep. Kept on the signature so a future
	    vendor-catalog-refresh addition does not change the calling contract or
	    reopen the parameter-binding question.

	.PARAMETER CatalogRelativePath
	    Path to the authenticated productVersionCatalog.json, relative to DepotPath.
	    Defaults to the same location ManagedToolOptions.ProductVersionCatalogPath
	    names for the connected-pull side.

	.PARAMETER VcfDownloadManagerCommonPath
	    Path to the sibling repository's vcf-download-manager.common.ps1, for
	    Get-FileManifest.

	.OUTPUTS
	    One [pscustomobject] per catalog entry and per unknown file, distinguished by
	    RecordType:
	      'ArtifactPresence' -- RelativePath, Sha256, SizeBytes, Status
	      ('present'/'missing'), Product, Version. Matches #1488's DepotArtifactUpsert
	      identity fields exactly.
	      'UnknownFile' -- RelativePath, SizeBytes. A file present on disk that
	      matches no catalog entry and is not a recognized catalog-adjacent exception.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$DepotPath,

		[Parameter()]
		[AllowEmptyString()]
		[AllowNull()]
		[string]$DepotToken,

		[Parameter()]
		[string]$CatalogRelativePath = $Script:DefaultCatalogRelativePath,

		[Parameter()]
		[string]$VcfDownloadManagerCommonPath = $Script:VcfDownloadManagerCommonPath
	)

	if ([string]::IsNullOrWhiteSpace($VcfDownloadManagerCommonPath)) {
		throw 'WaypointCatalogIndex: no vcf-download-manager.common.ps1 path configured (WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH or -VcfDownloadManagerCommonPath).'
	}

	if (-not (Test-Path -Path $VcfDownloadManagerCommonPath -PathType Leaf)) {
		throw "WaypointCatalogIndex: vcf-download-manager.common.ps1 not found at '$VcfDownloadManagerCommonPath'."
	}

	# Dot-source the unmodified sibling-repository script to bring Get-FileManifest into scope.
	. $VcfDownloadManagerCommonPath

	# Issue #719: re-define Write-Log again, now that the dot-source above has
	# shadowed it with the sibling script's own filtered console/file
	# implementation -- see WaypointDownload.psm1's matching override for the full
	# rationale. Delegates to the shared WaypointLogging adapter (issue #579,
	# preloaded ahead of this module -- deploy/compose.yaml's
	# PowerShell:ModulePreloadPaths for download-runner) so every severity lands
	# unconditionally on the native stream PowerShellExecutor already captures.
	function Write-Log {
		[CmdletBinding()]
		param(
			[Parameter(Mandatory, Position = 0)]
			[AllowEmptyString()]
			[string]$Message,

			[Parameter()]
			[ValidateSet('Debug', 'Verbose', 'Info', 'Success', 'Warning', 'Error', 'Critical')]
			[string]$Severity = 'Info',

			[Parameter()]
			[string]$Source
		)

		WaypointLogging\Write-Log -Message $Message -Severity $Severity -Source $Source
	}

	$CatalogPath = Join-Path -Path $DepotPath -ChildPath $CatalogRelativePath
	if (-not (Test-Path -Path $CatalogPath -PathType Leaf)) {
		throw "WaypointCatalogIndex: authenticated catalog not found at '$CatalogPath'."
	}

	Write-Log "Sweeping depot share against catalog: $CatalogPath" -Severity 'Info'

	$CatalogJson = Get-Content -LiteralPath $CatalogPath -Raw
	$CatalogEntries = ConvertFrom-WaypointCatalogJson -Json $CatalogJson

	Write-Log "Building on-disk manifest for $DepotPath" -Severity 'Verbose'
	$Manifest = Get-FileManifest -Directory $DepotPath -IncludeHash -HashAlgorithm SHA256

	# RelativePath keys the manifest hashtable already uses forward-slash-normalized
	# paths (Get-FileManifest, vcf-download-manager.common.ps1) -- reuse that
	# normalization for every comparison below.
	$ZipExpandDirs = Get-ZipExpandDirectories -ManifestKeys $Manifest.Keys
	$ConsumedRelativePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

	$PresenceCount = 0
	foreach ($CatalogEntry in $CatalogEntries) {
		$PresenceCount++
		$ZipExpandMatch = $null
		if ($CatalogEntry.RelativePath -like '*.zip') {
			$ZipExpandMatch = $ZipExpandDirs | Where-Object {
				$_.Version -eq $CatalogEntry.Version -and (
					[string]::IsNullOrEmpty($CatalogEntry.Product) -or $_.Component -eq $CatalogEntry.Product
				)
			} | Select-Object -First 1
		}

		if ($ZipExpandMatch) {
			foreach ($ExpandedRelativePath in $ZipExpandMatch.RelativePaths) {
				[void]$ConsumedRelativePaths.Add($ExpandedRelativePath)
			}

			[pscustomobject]@{
				RecordType   = 'ArtifactPresence'
				RelativePath = $CatalogEntry.RelativePath
				Sha256       = $CatalogEntry.Sha256
				SizeBytes    = $CatalogEntry.SizeBytes
				Status       = 'present'
				Product      = $CatalogEntry.Product
				Version      = $CatalogEntry.Version
			}
			continue
		}

		$DiskEntry = $Manifest[$CatalogEntry.RelativePath]
		$Status = Test-CatalogEntryPresent -CatalogEntry $CatalogEntry -DiskEntry $DiskEntry
		if ($DiskEntry) {
			[void]$ConsumedRelativePaths.Add($CatalogEntry.RelativePath)
		}

		[pscustomobject]@{
			RecordType   = 'ArtifactPresence'
			RelativePath = $CatalogEntry.RelativePath
			Sha256       = $CatalogEntry.Sha256
			SizeBytes    = $CatalogEntry.SizeBytes
			Status       = $Status
			Product      = $CatalogEntry.Product
			Version      = $CatalogEntry.Version
		}

		if ($PresenceCount % 25 -eq 0) {
			Write-Log "Swept $PresenceCount catalog entries so far..." -Severity 'Verbose'
		}
	}

	# upgrade_info.xml: known/indexed, never unknown (ratified #1026/#1027 addition).
	foreach ($RelativePath in $Manifest.Keys) {
		if ([System.IO.Path]::GetFileName($RelativePath) -ieq 'upgrade_info.xml') {
			$Entry = $Manifest[$RelativePath]
			[void]$ConsumedRelativePaths.Add($RelativePath)

			[pscustomobject]@{
				RecordType   = 'ArtifactPresence'
				RelativePath = $RelativePath
				Sha256       = $Entry.Hash
				SizeBytes    = $Entry.Size
				Status       = 'present'
				Product      = $null
				Version      = $null
			}
		}
	}

	$UnknownCount = 0
	foreach ($RelativePath in $Manifest.Keys) {
		if ($ConsumedRelativePaths.Contains($RelativePath)) {
			continue
		}

		$Entry = $Manifest[$RelativePath]
		$UnknownCount++

		[pscustomobject]@{
			RecordType   = 'UnknownFile'
			RelativePath = $RelativePath
			SizeBytes    = $Entry.Size
		}
	}

	Write-Log "Sweep complete: $PresenceCount catalog entries, $UnknownCount unknown file(s)." -Severity 'Info'
}

<#
.SYNOPSIS
    Parses the authenticated productVersionCatalog.json into flattened catalog-entry
    objects (RelativePath, Sha256, SizeBytes, Product, Version) -- the same
    patches.*[].artifacts.bundles[].binaries[] shape
    Waypoint.Core.Catalog.VendorProductVersionCatalogParser.cs parses on the connected
    side, deduplicated by fileName the same way. Throws on malformed JSON (fail
    closed, matching that parser's own contract) -- callers classify that as a job
    failure, never a silently empty sweep.
#>
function ConvertFrom-WaypointCatalogJson {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Json
	)

	$Document = $Json | ConvertFrom-Json -ErrorAction Stop -Depth 32

	$ByFileName = [ordered]@{}
	$Patches = $Document.patches
	if (-not $Patches) {
		return @()
	}

	foreach ($ComponentName in $Patches.PSObject.Properties.Name) {
		foreach ($Entry in @($Patches.$ComponentName)) {
			$Version = $Entry.productVersion
			$Bundles = $Entry.artifacts.bundles
			foreach ($Bundle in @($Bundles)) {
				foreach ($Binary in @($Bundle.binaries)) {
					if ([string]::IsNullOrWhiteSpace($Binary.fileName)) {
						continue
					}

					$ByFileName[$Binary.fileName] = [pscustomobject]@{
						RelativePath = $Binary.fileName
						Sha256       = $Binary.checksum
						SizeBytes    = $Binary.size
						Product      = $ComponentName
						Version      = $Version
					}
				}
			}
		}
	}

	return @($ByFileName.Values)
}

<#
.SYNOPSIS
    Groups manifest relative paths by recognized vCenter updaterepo zip-expand
    directory (COMP/VCENTER/vmw/<uuid>/<version>/), returning one object per distinct
    directory: Version, Component ('VCENTER'), and every manifest RelativePath nested
    under it.
#>
function Get-ZipExpandDirectories {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyCollection()]
		[string[]]$ManifestKeys
	)

	$ByDir = [ordered]@{}
	foreach ($RelativePath in $ManifestKeys) {
		$Match = [regex]::Match($RelativePath, $Script:ZipExpandDirPattern)
		if (-not $Match.Success) {
			continue
		}

		$DirKey = $Match.Value
		if (-not $ByDir.Contains($DirKey)) {
			$ByDir[$DirKey] = [pscustomobject]@{
				Version       = $Match.Groups['Version'].Value
				Component     = 'VCENTER'
				RelativePaths = [System.Collections.Generic.List[string]]::new()
			}
		}

		$ByDir[$DirKey].RelativePaths.Add($RelativePath)
	}

	return @($ByDir.Values)
}

<#
.SYNOPSIS
    Verifies one catalog entry against its (possibly absent) disk manifest entry by
    relative path + size/hash. A catalog entry with no matching manifest key is
    'missing'; one whose manifest entry disagrees on a known size or hash is treated
    as 'missing' too (identity verification, not mere path presence) -- a null
    catalog or disk value on either side is not compared.
#>
function Test-CatalogEntryPresent {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[psobject]$CatalogEntry,

		[Parameter()]
		[AllowNull()]
		$DiskEntry
	)

	if (-not $DiskEntry) {
		return 'missing'
	}

	if ($null -ne $CatalogEntry.SizeBytes -and $null -ne $DiskEntry.Size -and $CatalogEntry.SizeBytes -ne $DiskEntry.Size) {
		return 'missing'
	}

	if (-not [string]::IsNullOrWhiteSpace($CatalogEntry.Sha256) -and -not [string]::IsNullOrWhiteSpace($DiskEntry.Hash) -and $CatalogEntry.Sha256 -ine $DiskEntry.Hash) {
		return 'missing'
	}

	return 'present'
}

Export-ModuleMember -Function Invoke-WaypointCatalogIndex
