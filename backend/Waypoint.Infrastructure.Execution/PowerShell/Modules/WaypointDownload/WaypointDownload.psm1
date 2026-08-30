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
#
# Issue #719: unlike the imported vmware-stig-docker transport files the
# WaypointDiscovery/WaypointScan/WaypointCredentialTest shims dot-source, the
# migrated vcf-download-manager.common.ps1 defines its OWN Write-Log (console/file
# oriented, filtered by $Global:LogLevel/$Global:SilentMode, neither of which this
# shim sets) -- so dot-sourcing it always redefines Write-Log in this function's
# scope, silently dropping every Debug/Verbose call before it can reach a native
# stream PowerShellExecutor captures. Re-defining Write-Log again, after the dot
# source, to delegate into the shared WaypointLogging adapter (issue #579) closes
# that gap using the same severity-to-native-stream mapping compliance-runner
# already relies on, rather than inventing a second one.
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

	# Issue #719: the dot-source above just redefined Write-Log in this scope with
	# the sibling script's own console/file-oriented, level-filtered implementation
	# (see the module header comment). Re-define it again, now, to the shared
	# WaypointLogging adapter (issue #579, preloaded ahead of this module -- see
	# deploy/compose.yaml's PowerShell:ModulePreloadPaths for download-runner) --
	# every severity lands unconditionally on the native PowerShell stream
	# PowerShellExecutor already captures, redacts, and persists as a job.log event.
	# Save-WebFile (and every other function dot-sourced above) resolves Write-Log
	# dynamically at call time, so it picks up this redefinition even though it was
	# defined before this line ran.
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

	Save-WebFile -Url $Url -OutFile $OutFile -ExpectedSize $ExpectedSize -RetryCount $RetryCount -Source $Source
}

Export-ModuleMember -Function Invoke-WaypointDownload
