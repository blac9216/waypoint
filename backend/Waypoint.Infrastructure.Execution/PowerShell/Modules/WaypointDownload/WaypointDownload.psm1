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

# Waypoint-owned shim (issue #10, M1 vertical slice). It is the thin,
# Waypoint-authored seam that dot-sources the project-owned sibling repository's
# unmodified vcf-docker-download Save-WebFile function and adapts its resume/retry to the shape
# Invoke-WaypointDownload's caller (DownloadJobHandler) needs.

$Script:VcfDownloadManagerCommonPath = $env:WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH

function Invoke-WaypointDownload {
	<#
	.SYNOPSIS
	    Downloads one artifact with resume/retry, the Waypoint adaptation of
	    vcf-docker-download's Save-WebFile.

	.PARAMETER Url
	    Source URL to download.

	.PARAMETER OutFile
	    Destination path on the artifact store volume.

	.PARAMETER ExpectedSize
	    Expected byte count, when known (0 means unknown).

	.PARAMETER RetryCount
	    Maximum retry attempts on transient failure.

	.PARAMETER Source
	    A short label identifying the depot/source, forwarded to Save-WebFile for its
	    own logging -- carries no secret material.

	.OUTPUTS
	    One [pscustomobject]: Url, LocalPath, Success, Skipped, Size.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Url,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$OutFile,

		[Parameter()]
		[long]$ExpectedSize = 0,

		[Parameter()]
		[int]$RetryCount = 3,

		[Parameter()]
		[string]$Source = 'depot',

		[Parameter()]
		[string]$VcfDownloadManagerCommonPath = $Script:VcfDownloadManagerCommonPath
	)

	if ([string]::IsNullOrWhiteSpace($VcfDownloadManagerCommonPath)) {
		throw 'WaypointDownload: no vcf-download-manager.common.ps1 path configured (WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH or -VcfDownloadManagerCommonPath).'
	}

	if (-not (Test-Path -Path $VcfDownloadManagerCommonPath -PathType Leaf)) {
		throw "WaypointDownload: vcf-download-manager.common.ps1 not found at '$VcfDownloadManagerCommonPath'."
	}

	# Dot-source the unmodified sibling-repository script to bring Save-WebFile into scope. It
	# owns resume (merging a `.resume.tmp` partial), the retry/backoff loop, and
	# returns the exact shape this function passes straight through.
	. $VcfDownloadManagerCommonPath

	Save-WebFile -Url $Url -OutFile $OutFile -ExpectedSize $ExpectedSize -RetryCount $RetryCount -Source $Source
}

Export-ModuleMember -Function Invoke-WaypointDownload
