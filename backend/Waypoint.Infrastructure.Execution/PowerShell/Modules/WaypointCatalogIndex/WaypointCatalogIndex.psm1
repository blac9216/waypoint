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
#   - vCenter updaterepo zip-expand directories: a catalog binary carrying the
#     vendor catalog's own `metadata[]` tag `zip-expand` (configuration key
#     `relative`, e.g. value `vmw/<uuid>/<version>` -- #1027's depot-consumption
#     finding, verbatim from the shipped vcf-download-tool's own catalog writer)
#     installs as an expanded tree under COMP/<Product>/<that relative value>/,
#     itself under the depot root (DepotPath). The sweep reads that per-binary
#     metadata directly rather than pattern-matching disk directories against a
#     guessed shape or version string -- each zip entry carries its own exact
#     expand path, so two same-version zips (round-1 review finding 5) each bind to
#     their own tree, never to each other's. The sweep verifies the expanded tree's
#     presence as the zip entry's installed form (status 'present') instead of
#     requiring the zip file itself on disk, and does not enumerate the expanded
#     tree's own contents as unknown files.
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

# Issue #1027 depot-consumption finding (`lcm.depot.adapter.remote.v2.rootDir` /
# `…vcfBinariesDir`): every catalog binary resolves to
# PROD/COMP/<catalog product key>/<fileName> under DepotPath by default; a
# zip-expand binary's own relative-expand-path metadata (see
# Get-BinaryZipExpandRelativePath) is joined onto this same PROD/COMP root
# (round-1 review finding 1: the prior COMP/-anchored-at-root pattern never
# matched, because manifest keys are DepotPath-relative, i.e. PROD-prefixed).
# Round-2 review finding 1/4: this rule is applied by Get-CatalogEntryDepotRelativePath
# (ordinary binaries) and Get-ZipExpandDepotPrefix (zip-expand binaries) to EVERY
# catalog entry, not only the zip-expand prefix -- the manifest Get-FileManifest
# returns is keyed DepotPath-relative for every file, plain artifacts included, so an
# ordinary entry's manifest lookup and its emitted RelativePath/ExternalId must use the
# same resolved path or a fully-staged depot reports every entry both missing and
# unknown at once (round-2 finding 1).
$Script:DepotRoot = 'PROD'
$Script:ComponentBinariesDir = 'COMP'

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
	      'ArtifactPresence' -- RelativePath, ExternalId (round-1 review finding 3:
	      the live CatalogIndexJobHandler.TryParseArtifact reads ExternalId, not
	      RelativePath, as the upsert identity -- kept equal to RelativePath so the
	      current handler stays functional pending #1512's wiring rework), Sha256,
	      SizeBytes, Status ('present'/'missing'), Product, Version. Matches #1488's
	      DepotArtifactUpsert identity fields exactly.
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
	$ConsumedRelativePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

	$PresenceCount = 0
	foreach ($CatalogEntry in $CatalogEntries) {
		$PresenceCount++

		# Round-2 review findings 1/2: every catalog entry -- zip-expand and ordinary
		# alike -- resolves to its depot-relative path (PROD/COMP/<Product>/<fileName>)
		# for BOTH the manifest lookup and the emitted RelativePath/ExternalId. Manifest
		# keys (Get-FileManifest) are DepotPath-relative for every on-disk file, so a
		# bare-filename lookup never matches, and CatalogIndexJobHandler.cs persists
		# ExternalId straight through as DepotArtifactUpsert.RelativePath (#1488) --
		# emitting a bare filename there duplicates rows instead of updating them.
		$DepotRelativePath = Get-CatalogEntryDepotRelativePath -Product $CatalogEntry.Product -FileName $CatalogEntry.RelativePath

		$ExpandedRelativePaths = $null
		if (-not [string]::IsNullOrWhiteSpace($CatalogEntry.ZipExpandRelativePath)) {
			$ExpandPrefix = Get-ZipExpandDepotPrefix -Product $CatalogEntry.Product -RelativePath $CatalogEntry.ZipExpandRelativePath
			$ExpandedRelativePaths = @($Manifest.Keys | Where-Object { $_.StartsWith($ExpandPrefix, [System.StringComparison]::OrdinalIgnoreCase) })
		}

		if ($ExpandedRelativePaths -and $ExpandedRelativePaths.Count -gt 0) {
			foreach ($ExpandedRelativePath in $ExpandedRelativePaths) {
				[void]$ConsumedRelativePaths.Add($ExpandedRelativePath)
			}

			[pscustomobject]@{
				RecordType   = 'ArtifactPresence'
				RelativePath = $DepotRelativePath
				ExternalId   = $DepotRelativePath
				Sha256       = $CatalogEntry.Sha256
				SizeBytes    = $CatalogEntry.SizeBytes
				Status       = 'present'
				Product      = $CatalogEntry.Product
				Version      = $CatalogEntry.Version
			}
			continue
		}

		$DiskEntry = $Manifest[$DepotRelativePath]
		$Status = Test-CatalogEntryPresent -CatalogEntry $CatalogEntry -DiskEntry $DiskEntry
		if ($DiskEntry) {
			[void]$ConsumedRelativePaths.Add($DepotRelativePath)
		}

		[pscustomobject]@{
			RecordType   = 'ArtifactPresence'
			RelativePath = $DepotRelativePath
			ExternalId   = $DepotRelativePath
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
				ExternalId   = $RelativePath
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
						RelativePath          = $Binary.fileName
						Sha256                = $Binary.checksum
						SizeBytes             = $Binary.size
						Product               = $ComponentName
						Version               = $Version
						ZipExpandRelativePath = Get-BinaryZipExpandRelativePath -Binary $Binary
					}
				}
			}
		}
	}

	return @($ByFileName.Values)
}

<#
.SYNOPSIS
    Reads a catalog binary's own zip-expand metadata (issue #1027 depot-consumption
    finding): a vCenter updaterepo zip binary carries a `metadata[]` entry with
    `tag: "zip-expand"` and a `configuration` of `{key: "relative", value: "vmw/<uuid>/
    <version>"}` naming exactly where that specific zip installs as an expanded tree,
    relative to its component's COMP directory. Returns $null when the binary carries
    no such metadata (an ordinary, non-expanding binary). `configuration` is read
    defensively as either a single object or an array of key/value pairs -- the
    catalog document's own shape is not ours to assume beyond what #1027 documented.
#>
function Get-BinaryZipExpandRelativePath {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowNull()]
		[psobject]$Binary
	)

	foreach ($MetadataEntry in @($Binary.metadata)) {
		if ($null -eq $MetadataEntry -or $MetadataEntry.tag -ine 'zip-expand') {
			continue
		}

		foreach ($ConfigurationEntry in @($MetadataEntry.configuration)) {
			if ($null -eq $ConfigurationEntry) {
				continue
			}

			if ($ConfigurationEntry.key -ieq 'relative' -and -not [string]::IsNullOrWhiteSpace($ConfigurationEntry.value)) {
				return $ConfigurationEntry.value
			}
		}
	}

	return $null
}

<#
.SYNOPSIS
    Resolves an ordinary catalog binary's depot-relative path
    (PROD/COMP/<Product>/<fileName>) -- the same DepotPath-relative root
    Get-ZipExpandDepotPrefix anchors its zip-expand prefix to (round-2 review finding
    1: applied here to every catalog entry's own identity, zip-expand and ordinary
    alike, not only to the expanded tree's prefix), so the manifest lookup and the
    emitted RelativePath/ExternalId agree with what Get-FileManifest actually keys the
    on-disk manifest by.
#>
function Get-CatalogEntryDepotRelativePath {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[AllowNull()]
		[string]$Product,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$FileName
	)

	return "$Script:DepotRoot/$Script:ComponentBinariesDir/$Product/$FileName"
}

<#
.SYNOPSIS
    Builds the depot-root-relative prefix (PROD/COMP/<Product>/<RelativePath>/) a zip
    binary's own zip-expand metadata (Get-BinaryZipExpandRelativePath) resolves to, so
    the sweep can match the expanded tree's manifest keys directly -- one exact prefix
    per catalog binary, never a version-wide directory scan (round-1 review finding 5:
    two same-version zips each carry their own distinct relative value and therefore
    never collide here).
#>
function Get-ZipExpandDepotPrefix {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[AllowNull()]
		[string]$Product,

		[Parameter(Mandatory)]
		[string]$RelativePath
	)

	$TrimmedRelativePath = $RelativePath.Trim('/')
	return "$Script:DepotRoot/$Script:ComponentBinariesDir/$Product/$TrimmedRelativePath/"
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
