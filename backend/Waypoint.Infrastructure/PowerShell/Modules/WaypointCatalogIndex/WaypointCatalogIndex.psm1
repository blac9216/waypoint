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

# Waypoint-owned shim (issue #194, epic #9 slice 2). This module is NOT vendor code --
# it is the thin, Waypoint-authored seam that dot-sources the UNMODIFIED
# vcf-docker-download scripts (CLAUDE.md: "the sibling repos' vendor scripts run as
# unmodified vendor code -- Waypoint orchestrates them, it does not fork them") and
# adapts their output to the shape Invoke-WaypointCatalogIndex.Command needs.
#
# Domain-model open question 4 (docs/domain-model.md) is answered by what this module
# calls: Get-FileManifest is a pure filesystem walk of the depot share (no vendor
# binary, no depot token) -- building the index does NOT require the download tool.
# The depot token parameter below is accepted and threaded through for forward
# compatibility with a future vendor-catalog-refresh path, but the indexing walk
# itself never reads it.

$Script:VcfDownloadManagerCommonPath = $env:WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH

function Invoke-WaypointCatalogIndex {
	<#
	.SYNOPSIS
	    Builds the depot catalog index from files already present on the offline depot
	    share -- the Waypoint adaptation of the vcf-docker-download indexing path.

	.PARAMETER DepotPath
	    Root directory of the offline depot share to index.

	.PARAMETER DepotToken
	    The decrypted depot token, bound as a typed parameter (never interpolated into
	    this or any script text -- security.md controls 1/2). Accepted but unused by
	    the pure-filesystem indexing walk; kept on the signature so a future
	    vendor-catalog-refresh addition does not change the calling contract or
	    reopen the parameter-binding question.

	.OUTPUTS
	    One [pscustomobject] per indexed file: ExternalId, Sha256, Status, Product,
	    Version, SizeBytes, RelativePath.
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
		[string]$VcfDownloadManagerCommonPath = $Script:VcfDownloadManagerCommonPath
	)

	if ([string]::IsNullOrWhiteSpace($VcfDownloadManagerCommonPath)) {
		throw 'WaypointCatalogIndex: no vcf-download-manager.common.ps1 path configured (WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH or -VcfDownloadManagerCommonPath).'
	}

	if (-not (Test-Path -Path $VcfDownloadManagerCommonPath -PathType Leaf)) {
		throw "WaypointCatalogIndex: vcf-download-manager.common.ps1 not found at '$VcfDownloadManagerCommonPath'."
	}

	# Dot-source the unmodified vendor script to bring Get-FileManifest into scope.
	. $VcfDownloadManagerCommonPath

	Write-Information "Indexing depot share: $DepotPath"

	$Manifest = Get-FileManifest -Directory $DepotPath -IncludeHash -HashAlgorithm SHA256

	$Count = 0
	foreach ($RelativePath in $Manifest.Keys) {
		$Entry = $Manifest[$RelativePath]
		$Count++

		# Product/version are not derivable from a bare file manifest without
		# over-schemating vendor path conventions (ADR-0002) -- left null here;
		# a caller that also parses vendor catalog JSON (productVersionCatalog.json
		# and equivalents) can enrich these before upserting.
		[pscustomobject]@{
			ExternalId   = $RelativePath
			Sha256       = $Entry.Hash
			Status       = 'indexed'
			Product      = $null
			Version      = $null
			SizeBytes    = $Entry.Size
			RelativePath = $RelativePath
		}

		if ($Count % 25 -eq 0) {
			Write-Information "Indexed $Count files so far..."
		}
	}

	Write-Information "Indexing complete: $Count files."
}

Export-ModuleMember -Function Invoke-WaypointCatalogIndex
