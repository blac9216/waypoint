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

# Waypoint-owned shim (issue #194, epic #9 slice 2). It is the thin, Waypoint-authored
# seam that dot-sources the project-owned sibling repository's unmodified
# vcf-docker-download scripts and
# adapts their output to the shape Invoke-WaypointCatalogIndex.Command needs.
#
# Domain-model open question 4 (docs/domain-model.md) is answered by what this module
# calls: Get-FileManifest is a pure filesystem walk of the depot share (no vendor
# binary, no depot token) -- building the index does NOT require the download tool.
# The depot token parameter below is accepted and threaded through for forward
# compatibility with a future vendor-catalog-refresh path, but the indexing walk
# itself never reads it.
#
# Issue #719: Get-FileManifest lives in the same vcf-download-manager.common.ps1 file
# as Save-WebFile, and dot-sourcing it here redefines Write-Log the same way it does
# for WaypointDownload.psm1 -- see that module's header comment for the full
# rationale. The override below closes the same gap for this module's own callers
# (Write-Log calls from Set-Permissions/Get-FileManifest/Remove-EmptyDirectories).

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
